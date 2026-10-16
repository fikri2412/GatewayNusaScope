import { refreshSession } from './auth'
import { clearSession, getToken, saveSession } from './session'
import type { Session } from './session'
import type { ActivityDto, AttachmentDto, AttachmentPolicyDto, ConversationDto, JobDto, MessageDto, ModelInfoDto, SendMessageResponse } from './types'

// Tanpa VITE_API_URL (mis. `npm run build` produksi) permintaan relatif ke origin yang sama, bukan ke string "undefined".
export const API_URL = ((import.meta.env.VITE_API_URL as string | undefined) ?? '').replace(/\/+$/, '')

/** Pesan untuk ditampilkan dari nilai yang dilempar (dipakai App, Composer, useConversation). */
export const errorMessage = (e: unknown) => (e instanceof Error ? e.message : String(e))

export const SESSION_EXPIRED = 'Sesi berakhir, silakan masuk lagi.'

/**
 * Galat HTTP: `message` = teks galat server (fallback pesan lama), `code` = kode mesin server, `body` = badan JSON
 * yang sudah diurai (null bila badan bukan JSON). Dipakai UI untuk membedakan mis. 429, 409, atau 503.
 */
export class ApiError extends Error {
  readonly status: number
  readonly code: string | null
  readonly body: unknown

  constructor(message: string, status: number, code: string | null, body: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.body = body
  }
}

let onUnauthorized: (() => void) | null = null

/** Dipasang App: dipanggil saat server menolak token (401), sesudah sesi dibersihkan. */
export const setOnUnauthorized = (fn: (() => void) | null) => {
  onUnauthorized = fn
}

// Endpoint yang memasang atau memakai cookie refresh. Tanpa credentials 'include' browser membuang Set-Cookie dari fetch lintas origin, jadi login/register juga wajib.
const COOKIE_PATHS = ['/api/auth/login', '/api/auth/register', '/api/auth/refresh', '/api/auth/logout']

/** Badan galat server `{error, code}`; teks server dipakai apa adanya supaya pesan Indonesia tetap satu sumber. */
async function errorFrom(res: Response): Promise<ApiError> {
  const body: unknown = await res.json().catch(() => null)
  const { error, code } = (body ?? {}) as { error?: string; code?: string }
  const fallback = res.status === 429 ? 'Terlalu banyak percobaan, coba lagi nanti.' : `Permintaan gagal (${res.status})`
  return new ApiError(error ?? fallback, res.status, code ?? null, body)
}

let refreshInFlight: Promise<Session | null> | null = null

/** true bila sesi berhasil diperbarui. Sesi null maupun galat jaringan sama-sama berarti harus masuk lagi. */
async function tryRefresh(): Promise<boolean> {
  try {
    // Single-flight: beberapa 401 bersamaan berbagi satu refresh karena cookie rotasi hanya sah dipakai sekali.
    const session = await (refreshInFlight ??= refreshSession().finally(() => { refreshInFlight = null }))
    if (!session) return false
    saveSession(session)
    return true
  } catch {
    return false
  }
}

/** Token untuk SignalR: sesi yang kedaluwarsa diperbarui dulu lewat refresh (single-flight) supaya reconnect tidak 401 terus. */
export async function freshToken(): Promise<string> {
  if (!getToken()) await tryRefresh()
  return getToken() ?? ''
}

/**
 * Satu jalur fetch untuk semua permintaan: header Authorization, lalu 401 = satu kali refresh + ulangi sekali.
 * Gagal refresh: sesi dibersihkan, App diberi tahu, pemanggil menerima ApiError SESSION_EXPIRED.
 */
export async function fetchWithAuth(path: string, init?: RequestInit, authenticated = true, retried = false): Promise<Response> {
  // FormData: biarkan browser mengisi Content-Type (boundary multipart).
  const headers: Record<string, string> = {}
  if (typeof init?.body === 'string') headers['Content-Type'] = 'application/json'
  const token = authenticated ? getToken() : null
  if (token) headers.Authorization = `Bearer ${token}`
  const res = await fetch(API_URL + path, {
    ...init,
    credentials: init?.credentials ?? (COOKIE_PATHS.includes(path) ? 'include' : undefined),
    headers,
  })
  if (res.status === 401 && authenticated) {
    if (!retried && (await tryRefresh())) return fetchWithAuth(path, init, authenticated, true)
    clearSession()
    onUnauthorized?.()
    throw new ApiError(SESSION_EXPIRED, 401, 'session_expired', null)
  }
  if (!res.ok) throw await errorFrom(res)
  return res
}

export interface UploadOptions {
  /** Byte yang benar-benar terkirim ke server; total = ukuran badan multipart. */
  onProgress?: (loaded: number, total: number) => void
  signal?: AbortSignal
}

/** fetch tidak melaporkan byte terkirim, jadi unggahan memakai XHR. Badan respons dibungkus Response agar galat server sama dengan jalur fetch. */
function sendForm(path: string, form: FormData, { onProgress, signal }: UploadOptions): Promise<Response> {
  const { promise, resolve, reject } = Promise.withResolvers<Response>()
  if (signal?.aborted) {
    reject(new DOMException('Unggahan dibatalkan.', 'AbortError'))
    return promise
  }
  const xhr = new XMLHttpRequest()
  xhr.open('POST', API_URL + path)
  const token = getToken()
  if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`)
  xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress?.(e.loaded, e.total) }
  xhr.onload = () => resolve(new Response(xhr.responseText, { status: xhr.status }))
  xhr.onerror = () => reject(new Error('Koneksi terputus saat mengunggah.'))
  xhr.onabort = () => reject(new DOMException('Unggahan dibatalkan.', 'AbortError'))
  signal?.addEventListener('abort', () => xhr.abort(), { once: true })
  xhr.send(form)
  return promise
}

/** Unggah multipart dengan progres byte nyata dan pembatalan; token/401 diperlakukan sama dengan fetchWithAuth. */
async function uploadForm(path: string, form: FormData, options: UploadOptions, retried = false): Promise<Response> {
  const res = await sendForm(path, form, options)
  if (res.status === 401) {
    if (!retried && (await tryRefresh())) return uploadForm(path, form, options, true)
    clearSession()
    onUnauthorized?.()
    throw new ApiError(SESSION_EXPIRED, 401, 'session_expired', null)
  }
  if (!res.ok) throw await errorFrom(res)
  return res
}

/**
 * Permintaan ber-badan JSON. Dipakai modul api lain (conversations, collection) supaya jalur token/401 cuma satu.
 * 204 tidak punya badan (mis. hapus) dan menghasilkan undefined.
 */
export async function request<T>(path: string, init?: RequestInit, authenticated = true): Promise<T> {
  const res = await fetchWithAuth(path, init, authenticated)
  if (res.status === 204) return undefined as unknown as T
  return res.json() as Promise<T>
}

/** Unduh lampiran lewat fetch: <a download> tidak bisa mengirim header Authorization. */
export async function downloadAttachment(downloadUrl: string, fileName: string) {
  const blob = await (await fetchWithAuth(downloadUrl)).blob()
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.append(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}

/** Blob lampiran ber-token: `<img src>` ke endpoint langsung tidak bisa mengirim Authorization, jadi harus lewat fetch. */
export async function fetchAttachmentBlob(downloadUrl: string, signal?: AbortSignal) {
  const res = await fetchWithAuth(downloadUrl, { signal })
  return res.blob()
}

export const api = {
  listConversations: () => request<ConversationDto[]>('/api/conversations'),
  createConversation: () =>
    request<ConversationDto>('/api/conversations', { method: 'POST', body: '{}' }),
  listMessages: (id: string) => request<MessageDto[]>(`/api/conversations/${id}/messages`),
  listJobs: (id: string) => request<JobDto[]>(`/api/conversations/${id}/jobs`),
  /** Snapshot tiket aktif; sama dengan event SignalR `activityChanged`, jadi reconnect tidak menebak dari pesan terakhir. */
  listActivity: (id: string) => request<ActivityDto[]>(`/api/conversations/${id}/activity`),
  /** 200 daftar kosong = tidak ada model untuk akun ini; 503 = katalog gagal dibaca (UI harus membedakannya). */
  listModels: () => request<ModelInfoDto[]>('/api/models'),
  /** Batas lampiran efektif saat ini dari snapshot server; UI tidak boleh memakai default hardcoded. */
  getAttachmentPolicy: () => request<AttachmentPolicyDto>('/api/attachment-policy'),
  sendMessage: (id: string, content: string, attachmentIds: string[], model: string, thinkMode?: string) =>
    request<SendMessageResponse>(`/api/conversations/${id}/messages`, {
      method: 'POST',
      body: JSON.stringify({ content, attachmentIds, model, thinkMode }),
    }),
  uploadAttachment: async (id: string, file: File, options: UploadOptions = {}) => {
    const form = new FormData()
    form.append('file', file)
    const res = await uploadForm(`/api/conversations/${id}/attachments`, form, options)
    return (await res.json()) as AttachmentDto
  },
}
