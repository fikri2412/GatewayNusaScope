import { useState, type FormEvent } from 'react'
import { login, register, requestPasswordReset, resetPassword } from '../api/auth'
import { errorMessage } from '../api/client'
import type { Session } from '../api/session'
import { Brand, Icon, ModelGlyph } from './NusaUi'
import './LoginForm.css'

interface Props {
  onAuthenticated: (session: Session) => void
  /** Pesan di atas form, misalnya sesi berakhir. */
  notice?: string
}

type Mode = 'login' | 'register' | 'forgot' | 'reset'

const COPY: Record<Mode, { title: string; subtitle: string; action: string; busy: string }> = {
  login: { title: 'Selamat datang kembali', subtitle: 'Masuk untuk melanjutkan percakapanmu.', action: 'Masuk', busy: 'Memeriksa…' },
  register: { title: 'Buat akun baru', subtitle: 'Daftar untuk mulai memakai NusaScope.', action: 'Daftar', busy: 'Mendaftar…' },
  forgot: { title: 'Lupa password', subtitle: 'Masukkan email akunmu, kami kirim tautan atur ulang.', action: 'Kirim tautan', busy: 'Mengirim…' },
  reset: { title: 'Password baru', subtitle: 'Buat password baru untuk akunmu.', action: 'Simpan password', busy: 'Menyimpan…' },
}

// Tautan kebijakan hanya tampil bila alamatnya diisi saat build.
const privacyUrl = (import.meta.env.VITE_PRIVACY_URL as string | undefined)?.trim() ?? ''
const termsUrl = (import.meta.env.VITE_TERMS_URL as string | undefined)?.trim() ?? ''

export function LoginForm({ onAuthenticated, notice }: Props) {
  // Token dari surat atur ulang: ?reset=<token> membuka form password baru, bukan form masuk.
  const [resetToken] = useState(() => new URLSearchParams(window.location.search).get('reset') ?? '')
  const [mode, setMode] = useState<Mode>(resetToken ? 'reset' : 'login')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [email, setEmail] = useState('')
  const [remember, setRemember] = useState(false)
  const [shown, setShown] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    setSuccess(null)
    try {
      if (mode === 'login' || mode === 'register') {
        // Daftar sekaligus masuk: respons register sudah berisi token.
        const session =
          mode === 'register' ? await register(username, password, email, remember) : await login(username, password, remember)
        onAuthenticated(session) // form dilepas App, jadi tidak perlu setBusy(false)
        return
      }
      if (mode === 'forgot') {
        await requestPasswordReset(email)
        setSuccess('Jika email terdaftar, tautan atur ulang akan dikirim.')
      } else {
        await resetPassword(resetToken, password)
        // Token sekali pakai: buang dari URL supaya muat ulang tidak mengulang permintaan.
        window.history.replaceState(null, '', window.location.pathname)
        setPassword('')
        setMode('login')
        setSuccess('Password diubah. Silakan masuk.')
      }
    } catch (err) {
      setError(errorMessage(err))
    }
    setBusy(false)
  }

  const goTo = (next: Mode) => {
    setMode(next)
    setError(null)
    setSuccess(null)
  }

  const isLogin = mode === 'login'
  const isRegister = mode === 'register'
  const copy = COPY[mode]

  const emailField = (
    <div className="login-group">
      <label className="login-label" htmlFor="email">
        Email
      </label>
      <div className="login-field">
        <Icon name="user" size={18} />
        <input
          className="login-input"
          id="email"
          type="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          placeholder="nama@contoh.com"
          autoComplete="email"
          required
          maxLength={254}
        />
      </div>
    </div>
  )

  const passwordField = (
    <div className="login-group">
      <label className="login-label" htmlFor="password">
        Password
      </label>
      <div className="login-field">
        <Icon name="lock" size={18} />
        <input
          className="login-input"
          id="password"
          type={shown ? 'text' : 'password'}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          placeholder="••••••••"
          autoComplete={isLogin ? 'current-password' : 'new-password'}
          // Cerminan aturan server; server tetap memvalidasi ulang.
          required
          minLength={8}
          maxLength={128}
        />
        <button
          className="login-ghost"
          type="button"
          aria-label={shown ? 'Sembunyikan password' : 'Tampilkan password'}
          aria-pressed={shown}
          onClick={() => setShown(!shown)}
        >
          <Icon name={shown ? 'eyeOff' : 'eye'} size={18} />
        </button>
      </div>
    </div>
  )

  const rememberBox = (
    <div className="login-remember">
      <input
        className="login-check"
        id="remember"
        type="checkbox"
        checked={remember}
        onChange={(e) => setRemember(e.target.checked)}
      />
      <label className="login-label" htmlFor="remember">
        Ingat saya di perangkat ini
      </label>
    </div>
  )

  return (
    <div className="login login-page">
      <section className="login-pane">
        <header className="login-brand">
          <Brand />
        </header>

        <div className="login-body">
          <form className="login-form" aria-busy={busy} onSubmit={(e) => void submit(e)}>
            <div className="login-head">
              <h1 className="login-title">{copy.title}</h1>
              <p className="login-subtitle">{copy.subtitle}</p>
            </div>

            {notice && (
              <p className="login-alert login-alert-notice" role="status">
                {notice}
              </p>
            )}
            {success && (
              <p className="login-alert login-alert-notice" role="status">
                {success}
              </p>
            )}
            {error && (
              <p className="login-alert login-alert-error" role="alert">
                {error}
              </p>
            )}

            {(isLogin || isRegister) && (
              <div className="login-group">
                <label className="login-label" htmlFor="username">
                  Username
                </label>
                <div className="login-field">
                  <Icon name="user" size={18} />
                  <input
                    className="login-input"
                    id="username"
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                    placeholder="nama pengguna"
                    autoComplete="username"
                    // Cerminan aturan server; server tetap memvalidasi ulang.
                    required
                    minLength={3}
                    maxLength={50}
                    pattern="[A-Za-z0-9._\-]+"
                  />
                </div>
              </div>
            )}

            {(isRegister || mode === 'forgot') && emailField}
            {(isLogin || isRegister || mode === 'reset') && passwordField}
            {(isLogin || isRegister) && rememberBox}

            <button className="login-primary" type="submit" disabled={busy}>
              {busy && <span className="login-spinner" aria-hidden="true" />}
              {busy ? copy.busy : copy.action}
            </button>

            <p className="login-switch-row">
              {isRegister ? (
                <>
                  {'Sudah punya akun? '}
                  <button className="login-switch" type="button" disabled={busy} onClick={() => goTo('login')}>
                    Masuk
                  </button>
                </>
              ) : isLogin ? (
                <>
                  {'Belum punya akun? '}
                  <button className="login-switch" type="button" disabled={busy} onClick={() => goTo('register')}>
                    Daftar
                  </button>
                  {' · '}
                  <button className="login-switch" type="button" disabled={busy} onClick={() => goTo('forgot')}>
                    Lupa password?
                  </button>
                </>
              ) : (
                <button className="login-switch" type="button" disabled={busy} onClick={() => goTo('login')}>
                  Kembali ke masuk
                </button>
              )}
            </p>
          </form>
        </div>

        <footer className="login-footer">
          <span>© NusaScope</span>
          {(privacyUrl || termsUrl) && (
            <span className="login-footer-links">
              {privacyUrl && (
                <a href={privacyUrl} target="_blank" rel="noopener noreferrer">
                  Privasi
                </a>
              )}
              {termsUrl && (
                <a href={termsUrl} target="_blank" rel="noopener noreferrer">
                  Ketentuan
                </a>
              )}
            </span>
          )}
        </footer>
      </section>

      <aside className="login-art" aria-hidden="true">
        <div className="login-art-stack">
          <div className="login-art-card login-bob-1" style={{ left: '6%', top: 20 }}>
            <ModelGlyph alias="deepseek" size={30} />
            <div className="login-art-lines">
              <span className="login-art-name">DeepSeek</span>
              <span className="login-art-cap">Tanya jawab</span>
            </div>
          </div>
          <div className="login-art-card login-bob-2" style={{ right: '4%', top: 130 }}>
            <ModelGlyph alias="glm" size={30} />
            <div className="login-art-lines">
              <span className="login-art-name">GLM</span>
              <span className="login-art-cap">Analisis dokumen</span>
            </div>
          </div>
          <div className="login-art-card login-bob-3" style={{ left: '18%', top: 250 }}>
            <ModelGlyph alias="minimax" size={30} />
            <div className="login-art-lines">
              <span className="login-art-name">MiniMax</span>
              <span className="login-art-cap">Ringkas lampiran</span>
            </div>
          </div>
        </div>

        <div className="login-art-copy">
          <p className="login-art-title">
            Banyak model AI.
            <br />
            Satu ruang kerja.
          </p>
          <p className="login-art-text">
            Ngobrol dengan jawaban yang muncul mengalir, lampirkan spreadsheet, PDF, atau gambar supaya bisa dianalisis. Model
            yang bisa dipakai mengikuti katalog gateway.
          </p>
        </div>
      </aside>
    </div>
  )
}
