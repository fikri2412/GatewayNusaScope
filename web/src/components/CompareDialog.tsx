import { useEffect, useId, useRef, useState } from 'react'
import { ApiError, errorMessage } from '../api/client'
import { disabledReason } from '../api/models'
import { useModels } from '../hooks/useModels'
import { ModelGlyph } from './NusaUi'
import './MessageBubble.css'

/** Batas cabang dari server: lebih dari ini pasti 400, jadi ditahan di UI lebih dulu. */
const MAX_BRANCHES = 3

/** Baris rincian 400 `compare_invalid`; bentuk lain diabaikan, bukan ditebak. */
const isBranchError = (value: unknown): value is { model: string; error: string } => {
  if (!value || typeof value !== 'object') return false
  if (!('model' in value) || !('error' in value)) return false
  return typeof value.model === 'string' && typeof value.error === 'string'
}

/** Rincian per model dari 400 compare_invalid; null = galat lain (satu pesan saja). */
function branchErrors(e: unknown) {
  if (!(e instanceof ApiError) || e.code !== 'compare_invalid') return null
  const body = e.body
  if (!body || typeof body !== 'object' || !('branches' in body) || !Array.isArray(body.branches)) return null
  const rows = body.branches.filter(isBranchError)
  return rows.length > 0 ? rows : null
}

/**
 * Pilih model pembanding untuk satu jawaban. Katalog datang dari server (bukan daftar karangan) dan
 * model yang tidak layak tetap tampil nonaktif beserta alasannya, sama seperti pemilih model lain.
 */
export function CompareDialog({ messageId, onCompare, onClose }: {
  messageId: number
  onCompare: (messageId: number, models: string[]) => Promise<void>
  onClose: () => void
}) {
  const dialog = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  const { models, error: catalogError, reload } = useModels()
  const [picked, setPicked] = useState<string[]>([])
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [branches, setBranches] = useState<{ model: string; error: string }[]>([])

  // showModal(): tanpa ini <dialog> hanya kotak biasa — latar tidak terblokir dan Esc tidak menutupnya.
  useEffect(() => {
    dialog.current?.showModal()
  }, [])

  const toggle = (alias: string) => {
    setBranches([])
    setPicked((prev) =>
      prev.includes(alias) ? prev.filter((x) => x !== alias) : prev.length < MAX_BRANCHES ? [...prev, alias] : prev,
    )
  }

  const submit = async () => {
    setSubmitting(true)
    setError(null)
    setBranches([])
    try {
      await onCompare(messageId, picked)
      dialog.current?.close() // tiket sudah diantrekan; hasilnya menyusul sebagai jawaban alternatif
    } catch (e: unknown) {
      const rows = branchErrors(e)
      if (rows) setBranches(rows)
      else setError(errorMessage(e))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <dialog
      ref={dialog}
      className="compare-dialog"
      aria-labelledby={titleId}
      onClose={onClose}
      onClick={(e) => {
        if (e.target === dialog.current) dialog.current?.close() // klik backdrop = batal
      }}
    >
      <h2 className="compare-title" id={titleId}>Bandingkan dengan model lain</h2>
      <p className="muted">
        Pilih sampai {MAX_BRANCHES} model. Tiap model menjawab sebagai alternatif; jawaban yang sudah ada tidak diganti.
      </p>
      {catalogError && (
        <p className="error" role="alert">
          {catalogError}{' '}
          <button type="button" className="text-button" onClick={reload}>Muat ulang</button>
        </p>
      )}
      {!models && !catalogError && <p className="muted">Memuat daftar model…</p>}
      {models?.length === 0 && <p className="muted">Tidak ada model yang tersedia untuk akun ini.</p>}
      {models && models.length > 0 && (
        <ul className="compare-models">
          {models.map((m) => {
            const reason = disabledReason(m)
            const checked = picked.includes(m.id)
            const note = reason ?? (!checked && picked.length >= MAX_BRANCHES ? `maksimal ${MAX_BRANCHES}` : null)
            return (
              <li key={m.id}>
                <label className={`compare-model${reason ? ' compare-model-disabled' : ''}`}>
                  <input
                    type="checkbox"
                    checked={checked}
                    disabled={reason !== null || submitting || note !== null}
                    onChange={() => toggle(m.id)}
                  />
                  <ModelGlyph alias={m.id} size={18} />
                  <span className="compare-model-name">{m.id}</span>
                  {note && <span className="compare-model-reason">{note}</span>}
                </label>
              </li>
            )
          })}
        </ul>
      )}
      {branches.length > 0 && (
        <ul className="compare-branch-errors" role="alert">
          {branches.map((b) => (
            <li key={b.model}>
              <strong>{b.model}</strong>: {b.error}
            </li>
          ))}
        </ul>
      )}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="compare-actions">
        <button type="button" className="ghost-button" onClick={() => dialog.current?.close()} disabled={submitting}>
          Batal
        </button>
        <button type="button" className="primary-button" onClick={() => void submit()} disabled={submitting || picked.length === 0}>
          {submitting ? 'Mengantrekan…' : `Bandingkan (${picked.length})`}
        </button>
      </div>
    </dialog>
  )
}
