import { useEffect, type ReactNode } from 'react'
import { AuthProvider, isPlatform, useAuth } from './auth'
import { ChangePasswordPage, LoginPage, RedeemPage } from './pages/AuthPages'
import { CatalogPage, PlansPage, PlatformAuditPage, PlatformUsagePage, TemplatesPage, TenantsPage } from './pages/PlatformPages'
import { AlertsPage, WebhooksPage } from './pages/MaintenancePages'
import { ModelsPage, ProvidersPage } from './pages/ProviderPages'
import { AuditPage, DashboardPage, KeysPage, PoliciesPage, ProjectsPage, UsagePage, UsersPage } from './pages/TenantPages'
import { Link, navigate, useHashLocation } from './router'
import type { Role } from './types'
import { Alert } from './ui'

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
  { path: '/models', label: 'Model', roles: TENANT_ROLES, render: () => <ModelsPage /> },
  { path: '/projects', label: 'Project', roles: TENANT_ROLES, render: () => <ProjectsPage /> },
  { path: '/keys', label: 'API Key', roles: TENANT_ROLES, render: () => <KeysPage /> },
  { path: '/policies', label: 'Policies', roles: TENANT_ROLES, render: () => <PoliciesPage /> },
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

function Shell() {
  const { user, logout } = useAuth()
  const { path } = useHashLocation()

  useEffect(() => {
    if (user && isPlatform(user) && !path.startsWith('/platform') && path !== '/account') navigate('/platform/tenants')
    if (user && !isPlatform(user) && path.startsWith('/platform')) navigate('/')
  }, [user, path])

  if (!user) return null

  const visible = ROUTES.filter((route) => route.roles.includes(user.role))
  const route = visible.find((item) => item.path === path) ?? null

  return (
    <div className="app">
      <button type="button" className="skip" onClick={() => document.getElementById('main')?.focus()}>
        Lewati ke konten
      </button>
      <header className="topbar">
        <div className="brand">
          <strong>AI Gateway</strong>
          <span className="muted small">{isPlatform(user) ? 'platform' : user.tenantName ?? 'tenant'}</span>
        </div>
        <nav aria-label="Navigasi utama" className="nav">
          {visible.map((item) => (
            <Link key={item.path} to={item.path} current={item.path === path}>
              {item.label}
            </Link>
          ))}
        </nav>
        <div className="who">
          <span className="small">{user.displayName}</span>
          <span className="badge">{user.role}</span>
          {user.tenantId && !isPlatform(user) ? (
            <button type="button" className="ghost" onClick={() => navigate('/account')}>
              Akun
            </button>
          ) : null}
          <button type="button" className="ghost" onClick={() => void logout()}>
            Keluar
          </button>
        </div>
      </header>
      <main id="main" className="content" tabIndex={-1}>
        {route ? route.render() : <Alert kind="warn">Halaman “{path}” tidak ditemukan atau bukan untuk peran Anda.</Alert>}
      </main>
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
  return <Shell />
}

export default function App() {
  return (
    <AuthProvider>
      <Root />
    </AuthProvider>
  )
}
