import { useEffect, useRef, useState, type ReactNode } from 'react'
import { AuthProvider } from './AuthProvider'
import { isPlatform, useAuth } from './auth'
import { ChangePasswordPage, LoginPage, RedeemPage } from './pages/AuthPages'
import { CatalogPage, PlansPage, PlatformAuditPage, PlatformUsagePage, TemplatesPage, TenantsPage } from './pages/PlatformPages'
import { AlertsPage, WebhooksPage } from './pages/MaintenancePages'
import { ModelsPage, ProvidersPage } from './pages/ProviderPages'
import { SearchProvidersPage } from './pages/SearchProviderPages'
import { SettingsPage } from './pages/SettingsPages'
import { AuditPage, DashboardPage, KeysPage, PoliciesPage, ProjectsPage, UsagePage, UsersPage } from './pages/TenantPages'
import { navigate, useHashLocation } from './router'
import type { Role } from './types'
import { Alert, Brand, Field, Link, Modal, ThemeControl } from './ui'

interface RouteDef {
  path: string
  label: string
  roles: Role[]
  render: () => ReactNode
}

const TENANT_ROLES: Role[] = ['owner', 'admin', 'viewer']

const ROUTES: RouteDef[] = [
  { path: '/', label: 'Dashboard', roles: TENANT_ROLES, render: () => <DashboardPage /> },
  { path: '/providers', label: 'Provider', roles: TENANT_ROLES, render: () => <ProvidersPage /> },
  { path: '/web-search', label: 'Web Search', roles: ['owner', 'admin'], render: () => <SearchProvidersPage /> },
  { path: '/models', label: 'Model', roles: TENANT_ROLES, render: () => <ModelsPage /> },
  { path: '/projects', label: 'Project', roles: TENANT_ROLES, render: () => <ProjectsPage /> },
  { path: '/keys', label: 'API Key', roles: TENANT_ROLES, render: () => <KeysPage /> },
  { path: '/policies', label: 'Policies', roles: TENANT_ROLES, render: () => <PoliciesPage /> },
  { path: '/settings', label: 'Pengaturan', roles: TENANT_ROLES, render: () => <SettingsPage /> },
  { path: '/usage', label: 'Usage', roles: TENANT_ROLES, render: () => <UsagePage /> },
  { path: '/alerts', label: 'Alerts', roles: TENANT_ROLES, render: () => <AlertsPage /> },
  { path: '/webhooks', label: 'Webhooks', roles: TENANT_ROLES, render: () => <WebhooksPage /> },
  { path: '/users', label: 'Pengguna', roles: ['owner'], render: () => <UsersPage /> },
  { path: '/audit', label: 'Audit', roles: TENANT_ROLES, render: () => <AuditPage /> },
  { path: '/account', label: 'Akun', roles: [...TENANT_ROLES, 'platform_admin'], render: () => <ChangePasswordPage /> },
  { path: '/platform/tenants', label: 'Tenant', roles: ['platform_admin'], render: () => <TenantsPage /> },
  { path: '/platform/plans', label: 'Plan', roles: ['platform_admin'], render: () => <PlansPage /> },
  { path: '/platform/templates', label: 'Template', roles: ['platform_admin'], render: () => <TemplatesPage /> },
  { path: '/platform/catalog', label: 'Katalog', roles: ['platform_admin'], render: () => <CatalogPage /> },
  { path: '/platform/usage', label: 'Pemakaian', roles: ['platform_admin'], render: () => <PlatformUsagePage /> },
  { path: '/platform/audit', label: 'Audit platform', roles: ['platform_admin'], render: () => <PlatformAuditPage /> },
]

const NAV_GROUPS = [
  { label: 'Overview', paths: ['/'] },
  { label: 'Konfigurasi', paths: ['/providers', '/web-search', '/models', '/projects', '/keys', '/policies', '/settings'] },
  { label: 'Monitoring', paths: ['/usage', '/alerts', '/webhooks', '/audit'] },
  { label: 'Tim', paths: ['/users'] },
  { label: 'Platform', paths: ['/platform/tenants', '/platform/plans', '/platform/templates', '/platform/catalog', '/platform/usage', '/platform/audit'] },
]

const ICON_PATHS: Record<string, string> = {
  dashboard: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
  providers: 'M6 3v5 M18 3v5 M4 8h16v5a8 8 0 0 1-16 0z M12 21v-4',
  'web-search': 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M3 12h18 M12 3c3 3 3 15 0 18 M12 3c-3 3-3 15 0 18',
  models: 'M12 3 3 8l9 5 9-5-9-5z M3 12l9 5 9-5 M3 16l9 5 9-5',
  projects: 'M3 7h7l2-3h9v16H3z',
  keys: 'M8 15a5 5 0 1 1 4-8h9v4h-3v3h-4l-2-2 M6 8h.01',
  policies: 'M12 3 4 6v6c0 5 8 9 8 9s8-4 8-9V6z M8 12l3 3 5-6',
  settings:
    'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h.09A1.65 1.65 0 0 0 10 3.09V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v.09a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z',
  usage: 'M4 20V10 M10 20V4 M16 20v-9 M22 20H2',
  alerts: 'M6 9a6 6 0 0 1 12 0v6l3 3H3l3-3z M10 21h4',
  webhooks: 'M7 7h10v10H7z M3 12h4 M17 12h4 M12 3v4 M12 17v4',
  audit: 'M6 3h12v18H6z M9 7h6 M9 11h6 M9 15h4',
  users: 'M15 7a3 3 0 1 1-6 0 3 3 0 0 1 6 0z M5 21v-3a7 7 0 0 1 14 0v3',
  tenants: 'M4 21V3h11v18 M15 10h5v11 M8 7h3 M8 11h3 M8 15h3 M1 21h22',
  plans: 'M4 5h16v14H4z M4 10h16 M8 15h3',
  templates: 'M5 3h10l4 4v14H5z M15 3v5h4 M8 12h8 M8 16h5',
  catalog: 'M3 4h8v16H3z M13 4h8v16h-8z M6 8h2 M16 8h2',
  search: 'M21 21l-5-5 M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0',
  collapse: 'M9 3v18 M16 8l-4 4 4 4 M3 3h18v18H3z',
  menu: 'M4 6h16 M4 12h16 M4 18h16',
}

function Icon({ name }: { name: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={ICON_PATHS[name]} />
    </svg>
  )
}

function CommandPalette({ routes, onClose }: { routes: RouteDef[]; onClose: () => void }) {
  const [search, setSearch] = useState('')
  const [active, setActive] = useState(0)
  const searchInput = useRef<HTMLInputElement>(null)
  useEffect(() => { searchInput.current?.focus() }, [])
  const matches = routes.filter((route) => `${route.label} ${route.path}`.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()))
  useEffect(() => {
    document.getElementById(`command-result-${active}`)?.scrollIntoView({ block: 'nearest' })
  }, [active, search])

  function choose(route: RouteDef) {
    onClose()
    navigate(route.path)
  }

  return (
    <Modal title="Pindah halaman" onClose={onClose}>
      <div className="command-palette">
        <Field label="Cari halaman">
          <input
            type="search"
            ref={searchInput}
            placeholder="Cari halaman…"
            value={search}
            role="combobox"
            aria-autocomplete="list"
            aria-expanded="true"
            aria-controls="command-results"
            aria-activedescendant={matches[active] ? `command-result-${active}` : undefined}
            onChange={(event) => { setSearch(event.target.value); setActive(0) }}
            onKeyDown={(event) => {
              if (event.key === 'Escape') {
                event.preventDefault()
                event.stopPropagation()
                onClose()
              } else if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && matches.length > 0) {
                event.preventDefault()
                setActive((value) => (value + (event.key === 'ArrowDown' ? 1 : matches.length - 1)) % matches.length)
              } else if (event.key === 'Enter' && matches[active]) {
                event.preventDefault()
                choose(matches[active])
              }
            }}
          />
        </Field>
        <ul id="command-results" className="command-results" role="listbox" aria-label="Halaman yang dapat diakses">
          {matches.map((route, index) => (
            <li key={route.path} role="none">
              <button id={`command-result-${index}`} type="button" role="option" aria-selected={active === index} tabIndex={-1} className="ghost command-result" data-active={active === index} onMouseEnter={() => setActive(index)} onClick={() => choose(route)}>
                <span>{route.label}</span><small>{route.path}</small>
              </button>
            </li>
          ))}
        </ul>
        {matches.length === 0 ? <p className="command-empty muted" role="status">Tidak ada halaman yang cocok.</p> : null}
        <p className="hint"><kbd>↑</kbd> <kbd>↓</kbd> pilih · <kbd>Enter</kbd> buka · <kbd>Esc</kbd> tutup</p>
      </div>
    </Modal>
  )
}

function Shell() {
  const { user, logout } = useAuth()
  const { path } = useHashLocation()
  const [rail, setRail] = useState(() => window.matchMedia('(max-width: 1024px)').matches)
  const [mobile, setMobile] = useState(() => window.matchMedia('(max-width: 700px)').matches)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [commandsOpen, setCommandsOpen] = useState(false)
  const sidebar = useRef<HTMLElement>(null)
  const menuButton = useRef<HTMLButtonElement>(null)
  const account = useRef<HTMLDetailsElement>(null)
  const visible = user ? ROUTES.filter((route) => route.roles.includes(user.role)) : []
  const route = visible.find((item) => item.path === path) ?? null

  useEffect(() => {
    if (user && isPlatform(user) && !path.startsWith('/platform') && path !== '/account') navigate('/platform/tenants')
    if (user && !isPlatform(user) && path.startsWith('/platform')) navigate('/')
    document.title = `${route?.label ?? 'Admin'} · GateWay NusaScope`
  }, [user, path, route])

  useEffect(() => {
    const mobileMedia = window.matchMedia('(max-width: 700px)')
    const railMedia = window.matchMedia('(max-width: 1024px)')
    const update = () => { setMobile(mobileMedia.matches); setRail(railMedia.matches); setDrawerOpen(false) }
    mobileMedia.addEventListener('change', update)
    railMedia.addEventListener('change', update)
    return () => {
      mobileMedia.removeEventListener('change', update)
      railMedia.removeEventListener('change', update)
    }
  }, [])

  useEffect(() => {
    const keydown = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        const dialog = document.querySelector('dialog[open]')
        if (dialog && !dialog.querySelector('.command-palette')) return
        event.preventDefault()
        setDrawerOpen(false)
        setCommandsOpen((open) => !open)
      }
      if (event.key === 'Escape' && account.current?.open) {
        account.current.open = false
        account.current.querySelector<HTMLElement>('summary')?.focus()
      }
    }
    const dismiss = (event: PointerEvent) => {
      if (account.current?.open && !account.current.contains(event.target as Node)) account.current.open = false
    }
    document.addEventListener('keydown', keydown)
    document.addEventListener('pointerdown', dismiss)
    return () => {
      document.removeEventListener('keydown', keydown)
      document.removeEventListener('pointerdown', dismiss)
    }
  }, [])

  useEffect(() => {
    if (!mobile || !drawerOpen) return
    const trigger = menuButton.current
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    sidebar.current?.querySelector<HTMLElement>('a[aria-current="page"], a')?.focus()
    const keydown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.preventDefault(); setDrawerOpen(false) }
      if (event.key !== 'Tab') return
      const items = Array.from(sidebar.current?.querySelectorAll<HTMLElement>('a[href], button:not([disabled])') ?? []).filter((item) => item.getClientRects().length > 0)
      const first = items[0]
      const last = items[items.length - 1]
      if (!first || !last) return
      if (!sidebar.current?.contains(document.activeElement) || (event.shiftKey ? document.activeElement === first : document.activeElement === last)) {
        event.preventDefault()
        ;(event.shiftKey ? last : first).focus()
      }
    }
    document.addEventListener('keydown', keydown)
    return () => {
      document.body.style.overflow = previousOverflow
      document.removeEventListener('keydown', keydown)
      if (trigger?.getClientRects().length) trigger.focus()
      else document.getElementById('main')?.focus()
    }
  }, [mobile, drawerOpen])

  if (!user) return null
  const workspace = isPlatform(user) ? 'Platform' : user.tenantName ?? 'Tenant'
  const initials = user.displayName.trim().split(/\s+/).slice(0, 2).map((word) => word.charAt(0)).join('').toUpperCase()

  return (
    <div className={`app-shell${rail ? ' rail-mode' : ''}${drawerOpen ? ' drawer-open' : ''}`}>
      <button type="button" className="skip" onClick={() => document.getElementById('main')?.focus()}>Lewati ke konten</button>
      <aside ref={sidebar} id="sidebar" className="sidebar" aria-label="Workspace" inert={mobile && !drawerOpen}>
        <div className="sidebar-brand"><Brand /></div>
        <div className="sidebar-context" title={workspace}>
          <span className="workspace-avatar" aria-hidden="true">{workspace.slice(0, 2).toUpperCase()}</span>
          <div><strong>{workspace}</strong><span>{user.role.replace('_', ' ')}</span></div>
        </div>
        <nav aria-label="Navigasi utama" onClick={() => setDrawerOpen(false)}>
          {NAV_GROUPS.map((group) => {
            const items = visible.filter((item) => group.paths.includes(item.path))
            return items.length ? (
              <div key={group.label} className="nav-group">
                <div className="nav-label">{group.label}</div>
                {items.map((item) => (
                  <Link key={item.path} to={item.path} current={item.path === path} className="nav-link" label={item.label}>
                    <Icon name={item.path.split('/').pop() || 'dashboard'} /><span>{item.label}</span>
                  </Link>
                ))}
              </div>
            ) : null
          })}
        </nav>
        <div className="sidebar-footer" onClick={() => setDrawerOpen(false)}>
          <Link to="/account" current={path === '/account'} className="nav-link" label="Akun">
            <Icon name="users" /><span>Akun</span>
          </Link>
          <button type="button" className="rail-expand" onClick={() => setRail((value) => !value)} aria-label={rail ? 'Perluas sidebar' : 'Ringkas sidebar'}>
            <Icon name="collapse" /><span>{rail ? 'Perluas sidebar' : 'Ringkas sidebar'}</span>
          </button>
        </div>
      </aside>
      {mobile && drawerOpen ? <button type="button" className="nav-scrim" tabIndex={-1} aria-label="Tutup navigasi" onClick={() => setDrawerOpen(false)} /> : null}
      <div className="workspace" inert={mobile && drawerOpen}>
        <header className="topbar">
          <button ref={menuButton} type="button" className="ghost mobile-menu" aria-label="Buka navigasi" aria-controls="sidebar" aria-expanded={drawerOpen} onClick={() => setDrawerOpen(true)}><Icon name="menu" /></button>
          <nav className="breadcrumbs" aria-label="Breadcrumb">
            <ol><li>{workspace}</li><li aria-current="page">{route?.label ?? 'Halaman tidak ditemukan'}</li></ol>
          </nav>
          <div className="topbar-controls">
            <button type="button" className="shell-search" aria-label="Cari halaman (Ctrl atau Command K)" aria-keyshortcuts="Control+k Meta+k" onClick={() => setCommandsOpen(true)}>
              <Icon name="search" /><span>Cari halaman…</span><kbd>⌘ / Ctrl K</kbd>
            </button>
            <details ref={account} className="avatar-menu">
              <summary className="avatar-button" aria-label={`Menu akun ${user.displayName}`}>{initials}</summary>
              <div className="avatar-popover">
                <strong>{user.displayName}</strong><span className="muted small">{user.email}</span><span className="badge">{user.role}</span>
                <hr /><ThemeControl /><hr />
                <button type="button" className="ghost" onClick={() => { account.current?.removeAttribute('open'); navigate('/account') }}>Akun</button>
                <button type="button" className="ghost" onClick={() => void logout()}>Keluar</button>
              </div>
            </details>
          </div>
        </header>
        <main id="main" className="content" tabIndex={-1}>
          {route ? route.render() : <Alert kind="warn">Halaman “{path}” tidak ditemukan atau bukan untuk peran Anda.</Alert>}
        </main>
      </div>
      {commandsOpen ? <CommandPalette routes={visible} onClose={() => setCommandsOpen(false)} /> : null}
    </div>
  )
}

function Root() {
  const { user } = useAuth()
  const { segments } = useHashLocation()

  if (!user) {
    if (segments[0] === 'invite') return <RedeemPage purpose="invite" />
    if (segments[0] === 'reset') return <RedeemPage purpose="reset" />
    return <LoginPage />
  }
  return <Shell key={`${user.id}:${user.tenantId}:${user.role}`} />
}

export default function App() {
  return (
    <AuthProvider>
      <Root />
    </AuthProvider>
  )
}
