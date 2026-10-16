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

// sessionStorage bisa melempar (storage diblokir, mode privat lama): sesi tetap bertahan di memori selama halaman terbuka,
// bukan membuat render gagal atau memaksa login ulang padahal server baru saja menerbitkan sesi yang sah.
let fallback: string | null = null
const storedRaw = (): string | null => {
  try {
    return sessionStorage.getItem(KEY) ?? fallback
  } catch {
    return fallback
  }
}

/** null bila sesi tidak ada, rusak, atau sudah kedaluwarsa (isinya langsung dibuang). */
export const loadSession = (): Session | null => {
  const raw = storedRaw()
  if (!raw) return null
  try {
    const session = JSON.parse(raw) as Session
    if (session.token && session.username && Date.parse(session.expiresAt) > Date.now()) return session
  } catch {
    // isi bukan JSON: diperlakukan seperti sesi yang tidak ada
  }
  clearSession()
  return null
}

export const saveSession = (session: Session) => {
  const raw = JSON.stringify(session)
  fallback = raw
  try {
    sessionStorage.setItem(KEY, raw)
  } catch {
    // tetap di memori (fallback)
  }
}

export function clearSession() {
  fallback = null
  try {
    sessionStorage.removeItem(KEY)
  } catch {
    // storage diblokir: tidak ada yang perlu dibuang
  }
}

/** Token untuk header Authorization dan SignalR; null bila sesi tidak ada atau kedaluwarsa. */
export const getToken = () => loadSession()?.token ?? null
