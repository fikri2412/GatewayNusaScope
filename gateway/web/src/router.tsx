import { useEffect, useState, type ReactNode } from 'react'

/** Minimal hash router (no dependency): #/providers, #/models?x=y, #/platform/tenants. */
export function useHashLocation(): { path: string; segments: string[]; query: URLSearchParams } {
  const [hash, setHash] = useState(() => window.location.hash)
  useEffect(() => {
    const update = () => setHash(window.location.hash)
    window.addEventListener('hashchange', update)
    return () => window.removeEventListener('hashchange', update)
  }, [])
  const raw = hash.startsWith('#') ? hash.slice(1) : hash
  const [pathname, search = ''] = raw.split('?')
  const clean = pathname || '/'
  return {
    path: clean,
    segments: clean.split('/').filter(Boolean),
    query: new URLSearchParams(search),
  }
}

export function navigate(to: string): void {
  window.location.hash = to.startsWith('#') ? to : `#${to}`
}

export function Link({ to, children, className, current }: { to: string; children: ReactNode; className?: string; current?: boolean }) {
  return (
    <a
      href={`#${to}`}
      className={className}
      aria-current={current ? 'page' : undefined}
      onClick={(event) => {
        event.preventDefault()
        navigate(to)
      }}
    >
      {children}
    </a>
  )
}
