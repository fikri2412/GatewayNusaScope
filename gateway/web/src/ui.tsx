import { useEffect, useRef, useState, type ReactNode } from 'react'
import { errorMessage } from './api'

export interface Loaded<T> {
  data: T | null
  error: string | null
  loading: boolean
  reload: () => void
}

/** Small fetch-on-mount hook shared by all pages. */
export function useLoad<T>(loader: () => Promise<T>, deps: unknown[]): Loaded<T> {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [tick, setTick] = useState(0)
  useEffect(() => {
    let active = true
    setLoading(true)
    setError(null)
    loader()
      .then((value) => {
        if (active) setData(value)
      })
      .catch((err: unknown) => {
        if (active) setError(errorMessage(err))
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [...deps, tick])
  return { data, error, loading, reload: () => setTick((value) => value + 1) }
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
      <td colSpan={colSpan} className="muted">
        {label}
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
    if (dialog && !dialog.open) dialog.showModal()
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
      onClose={onClose}
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
  return (
    <div className="secret" role="alert">
      <p>
        <strong>{label}</strong> — salin sekarang; nilai ini hanya ditampilkan sekali.
      </p>
      <div className="secret-row">
        <code>{value}</code>
        <button type="button" onClick={() => void navigator.clipboard?.writeText(value).catch(() => undefined)}>
          Salin
        </button>
        {onDismiss ? (
          <button type="button" className="ghost" onClick={onDismiss}>
            Tutup
          </button>
        ) : null}
      </div>
    </div>
  )
}

export function Loading({ label = 'Memuat…' }: { label?: string }) {
  return (
    <p className="muted" role="status">
      {label}
    </p>
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

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

export function fmtDateTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : dateTime.format(date)
}

export function fmtDate(value: string | null | undefined): string {
  if (!value) return '—'
  const date = /^\d{4}-\d{2}-\d{2}$/.test(value) ? new Date(`${value}T00:00:00`) : new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleDateString()
}

export function fmtNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : value.toLocaleString()
}

export function fmtMoney(value: number | null | undefined, currency = 'USD'): string {
  if (value === null || value === undefined) return '—'
  return `${currency} ${value.toFixed(4)}`
}

export function today(): string {
  return new Date().toLocaleDateString('sv-SE')
}

export function daysAgo(days: number): string {
  const date = new Date()
  date.setDate(date.getDate() - days)
  return date.toLocaleDateString('sv-SE')
}

export function firstOfMonth(): string {
  const date = new Date()
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-01`
}

/** datetime-local value for `to` param: start of day, ISO with offset-free local format accepted by the API. */
export function startOfTodayIso(): string {
  return `${today()}T00:00`
}

export function endOfTodayIso(): string {
  return `${today()}T23:59:59`
}

export function isoOrNull(value: string): string | null {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

export function linesToArray(text: string): string[] {
  return text
    .split(/[\n,]/)
    .map((line) => line.trim())
    .filter(Boolean)
}
