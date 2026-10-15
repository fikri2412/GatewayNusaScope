import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { api, errorMessage } from '../api'
import { useAuth } from '../auth'
import { navigate } from '../router'
import { Alert, Brand, Field, Loading, PageHeader, ThemeControl } from '../ui'

function AuthCard({ title, description, children }: { title: string; description: string; children: ReactNode }) {
  useEffect(() => { document.title = `${title} · GateWay NusaScope` }, [title])
  return (
    <>
      <div className="auth-theme"><ThemeControl /></div>
      <main className="auth-page">
        <section className="card auth-card">
          <div className="auth-brand">
            <Brand />
            <h1>{title}</h1>
            <p className="muted">{description}</p>
          </div>
          {children}
        </section>
      </main>
    </>
  )
}

export function LoginPage() {
  const { login } = useAuth()
  const notice = new URLSearchParams(window.location.hash.split('?')[1] ?? '')
  const passwordChanged = notice.get('changed') === '1'
  const sessionExpired = notice.get('expired') === '1'
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await login(email.trim(), password)
      navigate('/')
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthCard title="Masuk" description="Masuk dengan akun tenant atau platform Anda.">
        <form onSubmit={submit} className="form">
          {passwordChanged ? <Alert kind="success">Password diganti. Sesi berakhir — silakan masuk dengan password baru Anda.</Alert> : null}
          {sessionExpired ? <Alert kind="info">Sesi berakhir. Silakan masuk kembali.</Alert> : null}
          {error ? <Alert>{error}</Alert> : null}
          <Field label="Email">
            <input
              type="email"
              autoComplete="username"
              required
              value={email}
              onChange={(event) => setEmail(event.target.value)}
            />
          </Field>
          <Field label="Password">
            <input
              type="password"
              autoComplete="current-password"
              required
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          </Field>
          <button type="submit" disabled={busy}>
            {busy ? 'Masuk…' : 'Masuk'}
          </button>
        </form>
        <p className="muted">
          Punya token undangan? <a href="#/invite">Terima undangan</a>. Lupa password? <a href="#/reset">Reset password</a>.
        </p>
    </AuthCard>
  )
}

/** Accepts an invite or a reset token: token from the URL (?token=…) or pasted manually. */
export function RedeemPage({ purpose }: { purpose: 'invite' | 'reset' }) {
  const query = new URLSearchParams(window.location.hash.split('?')[1] ?? '')
  const [token, setToken] = useState(query.get('token') ?? '')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const [busy, setBusy] = useState(false)

  const isInvite = purpose === 'invite'

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (password !== confirm) {
      setError('Konfirmasi password tidak sama.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await api.post<void>(`/admin/api/auth/${isInvite ? 'accept-invite' : 'reset-password'}`, { token, password }, undefined, false)
      setDone(true)
      setPassword('')
      setConfirm('')
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthCard
      title={isInvite ? 'Terima Undangan' : 'Reset Password'}
      description={isInvite ? 'Tempel token undangan dari platform/owner untuk membuat password akun Anda.' : 'Tempel token reset password dari owner untuk membuat password baru.'}
    >
        <form onSubmit={submit} className="form">
          {error ? <Alert>{error}</Alert> : null}
          {done ? <Alert kind="success">Berhasil. Silakan masuk dengan password baru Anda. <a href="#/login">Masuk</a></Alert> : null}
          <Field label="Token">
            <input value={token} required onChange={(event) => setToken(event.target.value)} />
          </Field>
          <Field label="Password baru" hint="Minimal sesuai kebijakan password gateway.">
            <input type="password" autoComplete="new-password" required value={password} onChange={(event) => setPassword(event.target.value)} />
          </Field>
          <Field label="Ulangi password">
            <input type="password" autoComplete="new-password" required value={confirm} onChange={(event) => setConfirm(event.target.value)} />
          </Field>
          <button type="submit" disabled={busy || done}>
            {busy ? 'Menyimpan…' : isInvite ? 'Aktifkan akun' : 'Simpan password'}
          </button>
        </form>
        <p className="muted">
          <a href="#/login">Kembali ke halaman masuk</a>
        </p>
    </AuthCard>
  )
}

export function ChangePasswordPage() {
  const { user, logout } = useAuth()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (!user) return <Loading />

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (next !== confirm) {
      setError('Konfirmasi password tidak sama.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await api.post<void>('/admin/api/auth/change-password', { currentPassword: current, newPassword: next })
      // The backend revokes every session on password change; the login screen carries the confirmation.
      await logout('/login?changed=1')
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageHeader title="Akun" description="Ubah password akun Anda." />
      <section className="card">
        <h2>Ubah password</h2>
        <form onSubmit={submit} className="form">
          {error ? <Alert>{error}</Alert> : null}
          <Field label="Password saat ini">
            <input type="password" autoComplete="current-password" required value={current} onChange={(event) => setCurrent(event.target.value)} />
          </Field>
          <Field label="Password baru">
            <input type="password" autoComplete="new-password" required value={next} onChange={(event) => setNext(event.target.value)} />
          </Field>
          <Field label="Ulangi password baru">
            <input type="password" autoComplete="new-password" required value={confirm} onChange={(event) => setConfirm(event.target.value)} />
          </Field>
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Ganti password'}
          </button>
        </form>
      </section>
    </>
  )
}
