import { useEffect, useState } from 'react'
import { errorMessage } from '../api/client'
import { deleteSavedAnswer, listCollection } from '../api/collection'
import type { SavedAnswer } from '../api/types'
import { Markdown } from './Markdown'

interface Props {
  /** Memilih sesi sumber dan kembali ke chat; sesi yang sudah hilang tidak bisa dibuka. */
  onOpenSession: (conversationId: string) => void
}

/** Koleksi jawaban tersimpan: snapshot tetap ada walau sesi sumbernya dihapus (§4.4). */
export function CollectionView({ onOpenSession }: Props) {
  const [items, setItems] = useState<SavedAnswer[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    listCollection()
      .then((rows) => {
        if (!cancelled) setItems(rows)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setItems([])
        setError(errorMessage(e))
      })
    return () => {
      cancelled = true
    }
  }, [])

  const remove = async (id: string) => {
    setError(null)
    try {
      await deleteSavedAnswer(id)
      setItems((prev) => prev?.filter((item) => item.id !== id) ?? prev)
    } catch (e: unknown) {
      setError(errorMessage(e))
    }
  }

  return (
    <main className="collection" aria-label="Koleksi">
      <h1 className="collection-title">Koleksi</h1>
      {error && <p className="error" role="alert">{error}</p>}
      {items === null && <p className="muted" role="status">Memuat koleksi…</p>}
      {items?.length === 0 && <p className="muted">Belum ada jawaban tersimpan.</p>}
      <ul className="collection-list">
        {items?.map((item) => {
          // `sourceAvailable` menyatakan ada tidaknya sesi sumber; idnya sekaligus nilai yang dibutuhkan tombol.
          const conversationId = item.conversationId
          return (
            <li key={item.id} className="collection-item">
              <div className="collection-head">
                <span className="collection-model">{item.modelAlias ?? 'NusaScope'}</span>
                <span className="collection-session">{item.sessionTitle ?? 'Tanpa judul'}</span>
                <time className="collection-date" dateTime={item.createdAt}>
                  {new Date(item.createdAt).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}
                </time>
              </div>
              <Markdown text={item.content} />
              <div className="collection-actions">
                {item.sourceAvailable && conversationId !== null ? (
                  <button type="button" className="ghost-button" onClick={() => onOpenSession(conversationId)}>Buka sesi</button>
                ) : (
                  <span className="muted">Sumber dihapus</span>
                )}
                <button type="button" className="text-button" onClick={() => void remove(item.id)}>Hapus dari koleksi</button>
              </div>
            </li>
          )
        })}
      </ul>
    </main>
  )
}
