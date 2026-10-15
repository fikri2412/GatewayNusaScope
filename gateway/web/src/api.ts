import type { TokenResponse, UserSummary } from './types'

// Session lives in memory only — never localStorage/sessionStorage.
let accessToken: string | null = null
let refreshToken: string | null = null
let onLost: (() => void) | null = null
let onUser: ((user: UserSummary) => void) | null = null
let refreshing: Promise<boolean> | null = null
// Identity of the current session: bumped by login and logout, unchanged by refresh rotation.
// A refresh that settles after the identity changed belongs to a dead session and is discarded.
let sessionGeneration = 0

export function setSession(tokens: TokenResponse): void {
  sessionGeneration++
  accessToken = tokens.accessToken
  refreshToken = tokens.refreshToken
  onUser?.(tokens.user)
}

export function clearSession(): void {
  sessionGeneration++
  accessToken = null
  refreshToken = null
}

export function setSessionLostHandler(handler: (() => void) | null): void {
  onLost = handler
}

/** Called with the user carried by every session response that is actually applied (login, refresh rotation). */
export function setSessionUserHandler(handler: ((user: UserSummary) => void) | null): void {
  onUser = handler
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

/** Revokes one refresh token server-side (`POST /admin/api/auth/logout`). Best effort: revocation must never block the UI. */
export async function revokeRefreshToken(token: string): Promise<void> {
  try {
    await fetch('/admin/api/auth/logout', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: token }),
    })
  } catch {
    // Best effort.
  }
}

/**
 * Single-flight refresh. Only a definite rejection (401/400) ends the session; a network failure or a
 * rate-limit/5xx answer keeps it so the next attempt can succeed. Results that arrive after logout or a
 * new login are discarded instead of resurrecting the old session.
 */
async function refreshSession(): Promise<boolean> {
  const token = refreshToken
  if (!token) return false
  if (!refreshing) {
    const generation = sessionGeneration
    refreshing = (async () => {
      let tokens: TokenResponse | null = null
      let terminal = false
      try {
        const response = await fetch('/admin/api/auth/refresh', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ refreshToken: token }),
        })
        if (response.ok) tokens = (await response.json()) as TokenResponse
        else terminal = response.status === 400 || response.status === 401
      } catch {
        // Network failure: keep the session, the refresh token may still be good.
      }
      if (sessionGeneration !== generation) {
        // Logged out or signed in as someone else meanwhile: revoke the rotated token we are dropping.
        if (tokens) void revokeRefreshToken(tokens.refreshToken)
        return false
      }
      if (tokens) {
        // Refresh rotation: same session identity, new token pair.
        accessToken = tokens.accessToken
        refreshToken = tokens.refreshToken
        onUser?.(tokens.user)
        return true
      }
      if (terminal) {
        clearSession()
        onLost?.()
      }
      return false
    })().finally(() => {
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

/**
 * Sends one request; on 401 the refresh is single-flight and the request is retried exactly once.
 * A request from a session that was replaced meanwhile (logout or another login) is never retried,
 * so an old-account mutation can never be replayed with the new session's token.
 */
async function sendWithRefresh(method: string, target: string, body?: unknown, retry = true): Promise<Response> {
  const generation = sessionGeneration
  const sentToken = accessToken
  let response = await send(method, target, body)
  if (response.status === 401 && retry && generation === sessionGeneration && refreshToken !== null) {
    // A sibling request already rotated the tokens: retry with the fresh access token, no extra rotation.
    const refreshed = accessToken !== sentToken || (await refreshSession())
    if (refreshed && generation === sessionGeneration) response = await send(method, target, body)
  }
  return response
}

async function request<T>(method: string, path: string, body?: unknown, params?: Query, retry = true): Promise<T> {
  const response = await sendWithRefresh(method, url(path, params), body, retry)
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
  const response = await sendWithRefresh('GET', url(path, params), undefined, retry)
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
