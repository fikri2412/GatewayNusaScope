import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, clearSession, currentRefreshToken, setSession, setSessionLostHandler } from './api'
import { navigate } from './router'
import type { TokenResponse, UserSummary } from './types'

interface AuthValue {
  user: UserSummary | null
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserSummary | null>(null)

  useEffect(() => {
    setSessionLostHandler(() => {
      setUser(null)
      navigate('/login')
    })
    return () => setSessionLostHandler(null)
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    const tokens = await api.post<TokenResponse>('/admin/api/auth/login', { email, password }, undefined, false)
    setSession(tokens)
    setUser(tokens.user)
  }, [])

  const logout = useCallback(async () => {
    const refreshToken = currentRefreshToken()
    clearSession()
    setUser(null)
    navigate('/login')
    if (refreshToken) {
      try {
        await api.post<void>('/admin/api/auth/logout', { refreshToken }, undefined, false)
      } catch {
        // Best effort: the local session is already gone.
      }
    }
  }, [])

  const value = useMemo(() => ({ user, login, logout }), [user, login, logout])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

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
