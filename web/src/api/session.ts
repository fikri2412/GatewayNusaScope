export interface Session {
  token: string
  username: string
  expiresAt: string
}

export type ThinkingMode = 'fast' | 'deep'

/** Preferensi tab yang rusak tidak boleh membuat chat gagal dimuat atau mengirim mode tak dikenal. */
export function parseThinkingModes(raw: string | null): Record<string, ThinkingMode> {
  try {
    const value: unknown = raw === null ? null : JSON.parse(raw)
    if (!value || typeof value !== 'object' || Array.isArray(value)) return {}
    return Object.fromEntries(Object.entries(value).filter(([, mode]) => mode === 'fast' || mode === 'deep'))
  } catch {
    return {}
  }
}

const KEY = 'operator-ai.session'

/** null bila sesi tidak ada, rusak, atau sudah kedaluwarsa (isinya langsung dibuang). */
export const loadSession = (): Session | null => {
  const raw = sessionStorage.getItem(KEY)
  if (!raw) return null
  try {
    const session = JSON.parse(raw) as Session
    if (session.token && session.username && Date.parse(session.expiresAt) > Date.now()) return session
  } catch {
    // isi bukan JSON: diperlakukan seperti sesi yang tidak ada
  }
  sessionStorage.removeItem(KEY)
  return null
}

export const saveSession = (session: Session) => sessionStorage.setItem(KEY, JSON.stringify(session))

export const clearSession = () => sessionStorage.removeItem(KEY)

/** Token untuk header Authorization dan SignalR; null bila sesi tidak ada atau kedaluwarsa. */
export const getToken = () => loadSession()?.token ?? null
