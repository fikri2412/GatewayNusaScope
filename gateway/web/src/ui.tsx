import { useEffect, useRef, useState, type ReactNode } from 'react'
import { navigate } from './router'

export function Brand() {
  return (
    <span className="brand-logo" role="img" aria-label="GateWay NusaScope">
      <span className="brand-lockup">
        <img className="logo-dark" src="/branding/logo-dark.png" alt="" width="520" height="139" />
        <img className="logo-light" src="/branding/logo-light.png" alt="" width="520" height="139" />
      </span>
      <img className="brand-symbol" src="/branding/logo-mark.png" alt="" width="128" height="128" />
    </span>
  )
}

type Theme = 'dark' | 'light' | 'system'

function storedTheme(): Theme {
  try {
    const value = localStorage.getItem('gw-theme')
    if (value === 'light' || value === 'system') return value
  } catch {
    // Appearance preferences are optional; authentication never uses storage.
  }
  return 'dark'
}

export function ThemeControl() {
  const [theme, setTheme] = useState<Theme>(storedTheme)
  useEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const apply = () => {
      document.documentElement.dataset.theme = theme === 'system' ? (media.matches ? 'dark' : 'light') : theme
    }
    apply()
    media.addEventListener('change', apply)
    try {
      localStorage.setItem('gw-theme', theme)
    } catch {
      // A blocked preference store must not prevent the selected theme.
    }
    return () => media.removeEventListener('change', apply)
  }, [theme])
  return (
    <label className="field theme-control">
      <span className="field-label">Tema</span>
      <select value={theme} onChange={(event) => setTheme(event.target.value as Theme)}>
        <option value="dark">Dark</option>
        <option value="light">Light</option>
        <option value="system">System</option>
      </select>
    </label>
  )
}

export function Alert({ kind = 'error', children }: { kind?: 'error' | 'success' | 'info' | 'warn'; children: ReactNode }) {
  return (
    <div className={`alert alert-${kind}`} role={kind === 'error' ? 'alert' : 'status'}>
      {children}
    </div>
  )
}

export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {children}
      {hint ? <span className="hint">{hint}</span> : null}
    </label>
  )
}

export function Fieldset({ legend, hint, children }: { legend: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <fieldset className="fieldset">
      <legend>{legend}</legend>
      {hint ? <p className="hint">{hint}</p> : null}
      {children}
    </fieldset>
  )
}

export function DataTable({ head, children }: { head: ReactNode[]; children: ReactNode }) {
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            {head.map((cell, index) => (
              <th key={index} scope="col">
                {cell}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  )
}

export function EmptyRow({ colSpan, label = 'Belum ada data.' }: { colSpan: number; label?: string }) {
  return (
    <tr>
      <td colSpan={colSpan} className="empty-row">
        <div className="empty-state">
          <svg className="empty-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
            <path d="M4 4h16v16H4z M8 9h8 M8 13h5" />
          </svg>
          <span>{label}</span>
        </div>
      </td>
    </tr>
  )
}

export function Pager({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (page: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  return (
    <nav className="pager" aria-label="Navigasi halaman">
      <button type="button" onClick={() => onPage(page - 1)} disabled={page <= 1}>
        Sebelumnya
      </button>
      <span>
        Halaman {page} dari {pages} ({total} baris)
      </span>
      <button type="button" onClick={() => onPage(page + 1)} disabled={page >= pages}>
        Berikutnya
      </button>
    </nav>
  )
}

/** Native <dialog> wrapper: focus trap, Esc close, and modal semantics from the platform. */
export function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const dialog = ref.current
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
    if (dialog && !dialog.open) dialog.showModal()
    return () => {
      dialog?.close()
      if (opener?.isConnected) opener.focus()
    }
  }, [])
  return (
    <dialog
      ref={ref}
      className={wide ? 'modal modal-wide' : 'modal'}
      aria-label={title}
      onCancel={(event) => {
        event.preventDefault()
        onClose()
      }}
    >
      <div className="modal-head">
        <h2>{title}</h2>
        <button type="button" className="ghost" onClick={onClose} aria-label="Tutup">
          ✕
        </button>
      </div>
      <div className="modal-body">{children}</div>
    </dialog>
  )
}

/** One-time secret (API key, invite token, reset token) — shown once, never stored. */
export function SecretPanel({ label, value, onDismiss }: { label: string; value: string; onDismiss?: () => void }) {
  const [feedback, setFeedback] = useState<string | null>(null)
  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      setFeedback('Disalin.')
    } catch {
      setFeedback('Gagal menyalin. Pilih dan salin nilai secara manual.')
    }
  }
  return (
    <div className="secret" role="alert">
      <p>
        <strong>{label}</strong> — salin sekarang; nilai ini hanya ditampilkan sekali.
      </p>
      <div className="secret-row">
        <code>{value}</code>
        <button type="button" onClick={() => void copy()}>
          Salin
        </button>
        {onDismiss ? (
          <button type="button" className="ghost" onClick={onDismiss}>
            Tutup
          </button>
        ) : null}
      </div>
      {feedback ? <p className="hint" role="status">{feedback}</p> : null}
    </div>
  )
}

export function Loading({ label = 'Memuat…' }: { label?: string }) {
  return (
    <div className="loading-state" role="status" aria-label={label}>
      <span className="sr-only">{label}</span>
      <div aria-hidden="true">
        <span className="skeleton" />
        <span className="skeleton" />
        <span className="skeleton" />
      </div>
    </div>
  )
}

export function PageHeader({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  return (
    <header className="page-head">
      <div>
        <h1>{title}</h1>
        {description ? <p className="muted">{description}</p> : null}
      </div>
      {actions ? <div className="page-actions">{actions}</div> : null}
    </header>
  )
}

export function Toolbar({ children }: { children: ReactNode }) {
  return <div className="toolbar">{children}</div>
}

export function Link({ to, children, className, current, label }: { to: string; children: ReactNode; className?: string; current?: boolean; label?: string }) {
  return (
    <a
      href={`#${to}`}
      className={className}
      aria-current={current ? 'page' : undefined}
      aria-label={label}
      title={label}
      onClick={(event) => {
        event.preventDefault()
        navigate(to)
      }}
    >
      {children}
    </a>
  )
}
