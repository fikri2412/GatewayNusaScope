import type { TokenResponse } from './types'

// Session lives in memory only — never localStorage/sessionStorage.
let accessToken: string | null = null
let refreshToken: string | null = null
let onLost: (() => void) | null = null
let refreshing: Promise<boolean> | null = null

export function setSession(tokens: TokenResponse): void {
  accessToken = tokens.accessToken
  refreshToken = tokens.refreshToken
}

export function clearSession(): void {
  accessToken = null
  refreshToken = null
}

export function setSessionLostHandler(handler: (() => void) | null): void {
  onLost = handler
}

export function currentRefreshToken(): string | null {
  return refreshToken
}

export class ApiError extends Error {
  status: number
  code: string | null
  type: string | null
  param: string | null

  constructor(status: number, message: string, code: string | null = null, type: string | null = null, param: string | null = null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.type = type
    this.param = param
  }
}

type Query = Record<string, string | number | boolean | null | undefined>

function url(path: string, params?: Query): string {
  if (!params) return path
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value))
  }
  const qs = search.toString()
  return qs ? `${path}?${qs}` : path
}

async function parseError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { error?: { message?: string; type?: string; code?: string; param?: string } }
    const e = body.error
    return new ApiError(response.status, e?.message ?? `HTTP ${response.status}`, e?.code ?? null, e?.type ?? null, e?.param ?? null)
  } catch {
    return new ApiError(response.status, `HTTP ${response.status}`)
  }
}

async function refreshSession(): Promise<boolean> {
  if (!refreshToken) return false
  if (!refreshing) {
    const token = refreshToken
    refreshing = fetch('/admin/api/auth/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: token }),
    })
      .then(async (response) => {
        if (!response.ok) return false
        setSession((await response.json()) as TokenResponse)
        return true
      })
      .catch(() => false)
      .then((ok) => {
        if (!ok) {
          clearSession()
          onLost?.()
        }
        return ok
      })
      .finally(() => {
        refreshing = null
      })
  }
  return refreshing
}

async function send(method: string, path: string, body?: unknown): Promise<Response> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`
  return fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
}

/** Sends one request; on 401 the refresh is single-flight and the request is retried exactly once. */
async function request<T>(method: string, path: string, body?: unknown, params?: Query, retry = true): Promise<T> {
  const target = url(path, params)
  let response = await send(method, target, body)
  if (response.status === 401 && retry && accessToken !== null && (await refreshSession())) {
    response = await send(method, target, body)
  }
  if (!response.ok) throw await parseError(response)
  if (response.status === 204) return undefined as T
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const api = {
  get: <T>(path: string, params?: Query) => request<T>('GET', path, undefined, params),
  post: <T>(path: string, body?: unknown, params?: Query, retry = true) => request<T>('POST', path, body ?? {}, params, retry),
  patch: <T>(path: string, body: unknown, params?: Query) => request<T>('PATCH', path, body, params),
  put: <T>(path: string, body: unknown, params?: Query) => request<T>('PUT', path, body, params),
  del: <T>(path: string, params?: Query) => request<T>('DELETE', path, undefined, params),
}

/** Fetches a CSV/text attachment with auth and saves it via the browser. */
export async function downloadFile(path: string, params: Query, filename: string, retry = true): Promise<void> {
  const target = url(path, params)
  let response = await send('GET', target)
  if (response.status === 401 && retry && accessToken !== null && (await refreshSession())) {
    response = await send('GET', target)
  }
  if (!response.ok) throw await parseError(response)
  const blob = await response.blob()
  const href = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = href
  anchor.download = filename
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(href)
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return 'Terjadi kesalahan.'
}
