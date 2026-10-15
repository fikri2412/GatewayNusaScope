import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, clearSession, currentRefreshToken, revokeRefreshToken, setSession, setSessionLostHandler, setSessionUserHandler } from './api'
import { AuthContext, type AuthValue } from './auth'
import { navigate } from './router'
import type { TokenResponse, UserSummary } from './types'

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserSummary | null>(null)

  useEffect(() => {
    setSessionUserHandler(setUser)
    setSessionLostHandler(() => {
      setUser(null)
      navigate('/login?expired=1')
    })
    return () => {
      setSessionUserHandler(null)
      setSessionLostHandler(null)
    }
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    const tokens = await api.post<TokenResponse>('/admin/api/auth/login', { email, password }, undefined, false)
    // setSession reports tokens.user, so the context owner is always the user carried by the live session.
    setSession(tokens)
  }, [])

  const logout = useCallback(async (redirect = '/login') => {
    const refreshToken = currentRefreshToken()
    clearSession()
    setUser(null)
    navigate(redirect)
    if (refreshToken) await revokeRefreshToken(refreshToken)
  }, [])

  const value = useMemo<AuthValue>(() => ({ user, login, logout }), [user, login, logout])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
