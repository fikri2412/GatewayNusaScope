import { createContext, useContext } from 'react'
import type { UserSummary } from './types'

export interface AuthValue {
  user: UserSummary | null
  login: (email: string, password: string) => Promise<void>
  /** Ends the session and navigates (default `/login`; pass a target to carry a message, e.g. `/login?changed=1`). */
  logout: (redirect?: string) => Promise<void>
}

export const AuthContext = createContext<AuthValue | null>(null)

export function useAuth(): AuthValue {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth harus dipakai di dalam AuthProvider')
  return value
}

export function isPlatform(user: UserSummary): boolean {
  return user.role === 'platform_admin'
}

export function canConfigure(user: UserSummary): boolean {
  return user.role === 'owner' || user.role === 'admin'
}
