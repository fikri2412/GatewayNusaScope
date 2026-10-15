import { ApiError, fetchWithAuth } from './client'
import type { Session } from './session'

const AUTH = '/api/auth'

/** POST tanpa Bearer: endpoint auth hanya memakai cookie (refresh/logout) atau badan JSON. */
const post = (path: string, body?: unknown) =>
  fetchWithAuth(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) }, false)

/** Badan AuthResponse: {token, username, expiresAt}. */
const sessionFrom = async (res: Response): Promise<Session> => (await res.json()) as Session

/** `rememberMe` menentukan cookie refresh ikut dipasang; access token selalu hanya di memori tab. */
export async function login(username: string, password: string, rememberMe: boolean): Promise<Session> {
  return sessionFrom(await post(`${AUTH}/login`, { username, password, rememberMe }))
}

/** Daftar sekaligus masuk: respons register sudah berisi token. */
export async function register(username: string, password: string, email: string, rememberMe: boolean): Promise<Session> {
  return sessionFrom(await post(`${AUTH}/register`, { username, password, email, rememberMe }))
}

/**
 * Tukar cookie refresh dengan sesi baru (cookie dirotasi server). null = cookie tidak ada/tidak berlaku.
 * Galat jaringan dan galat server lain dilempar supaya pemanggil bisa membedakan "belum pernah masuk" dari "gagal".
 */
export async function refreshSession(): Promise<Session | null> {
  try {
    return sessionFrom(await post(`${AUTH}/refresh`))
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) return null
    throw e
  }
}

/** Terbaik-usaha: gagal keluar tidak boleh menahan UI; cookie refresh dicabut server, sesi lokal dibersihkan pemanggil. */
export async function logoutRemote(): Promise<void> {
  try {
    await post(`${AUTH}/logout`)
  } catch {
    // diabaikan
  }
}

/**
 * Server selalu menjawab sama untuk email apa pun (anti-enumerasi), jadi UI menampilkan pesan generik.
 * 503 = surat/alamat web belum dikonfigurasi di server.
 */
export async function requestPasswordReset(email: string): Promise<void> {
  try {
    await post(`${AUTH}/forgot-password`, { email })
  } catch (e) {
    if (e instanceof ApiError && e.status === 503) {
      throw new ApiError('Atur ulang password belum tersedia di server ini.', e.status, e.code, e.body)
    }
    throw e
  }
}

/** Token sekali pakai; 400 `invalid_or_expired_token` / `invalid_password` dibawa apa adanya sebagai ApiError. */
export async function resetPassword(token: string, password: string): Promise<void> {
  await fetchWithAuth(
    `${AUTH}/reset-password`,
    { method: 'POST', body: JSON.stringify({ token, password }), credentials: 'include' },
    false,
  )
}
