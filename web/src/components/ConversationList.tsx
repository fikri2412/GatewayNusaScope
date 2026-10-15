import { useEffect, useRef, useState } from 'react'
import type { ConversationDto } from '../api/types'
import { DeleteSessionDialog } from './DeleteSessionDialog'
import { Brand, Icon } from './NusaUi'

interface Props {
  conversations: ConversationDto[]
  selectedId: string | null
  onSelect: (id: string) => void
  onNew: () => void
  /** Baris "Koleksi" di atas daftar sesi: daftar sesi ini bukan tab, Koleksi hanya menggantikan panel utama. */
  onOpenCollection: () => void
  collectionOpen: boolean
  /** 204 DELETE: baris sudah hilang di server; `nextId` = sesi yang menggantikan pilihan (null = tidak ada sisa). */
  onDeleted: (id: string, nextId: string | null) => void
  username: string
  onLogout: () => void
  loading: boolean
  error: string | null
  onRetry: () => void
}

export function ConversationList({ conversations, selectedId, onSelect, onNew, onOpenCollection, collectionOpen, onDeleted, username, onLogout, loading, error, onRetry }: Props) {
  const [confirm, setConfirm] = useState<ConversationDto | null>(null)
  const rowDelete = useRef(new Map<string, HTMLButtonElement>())
  const newButton = useRef<HTMLButtonElement>(null)
  // Sesudah dialog tertutup: pemicu kalau masih ada (Batal/gagal), tetangga kalau barisnya sudah terhapus.
  const focusAfter = useRef<{ kind: 'row'; id: string } | { kind: 'new' } | null>(null)

  useEffect(() => {
    if (confirm || !focusAfter.current) return
    const target = focusAfter.current
    focusAfter.current = null
    const el = target.kind === 'new' ? newButton.current : rowDelete.current.get(target.id)
    el?.focus()
  }, [confirm, conversations])

  const handleClosed = (deleted: boolean) => {
    const current = confirm
    setConfirm(null)
    if (!current) return
    if (!deleted) {
      focusAfter.current = { kind: 'row', id: current.id }
      return
    }
    const index = conversations.findIndex((c) => c.id === current.id)
    const nextId = index >= 0 ? (conversations[index + 1] ?? conversations[index - 1])?.id ?? null : null
    focusAfter.current = nextId === null ? { kind: 'new' } : { kind: 'row', id: nextId }
    onDeleted(current.id, nextId)
  }

  return (
    <aside className="sidebar">
      <div className="sidebar-brand">
        <Brand />
        <button type="button" ref={newButton} className="icon-button bordered" aria-label="Chat baru" onClick={onNew}><Icon name="plus" /></button>
      </div>
      <ul className="sidebar-views">
        <li>
          <button
            type="button"
            className={collectionOpen ? 'conversation selected' : 'conversation'}
            aria-current={collectionOpen ? 'page' : undefined}
            onClick={onOpenCollection}
          >
            Koleksi
          </button>
        </li>
      </ul>
      <div className="recent">
        <div className="recent-title">Terbaru</div>
        {loading && <p className="muted" role="status">Memuat riwayat…</p>}
        {error && <p className="error" role="alert">{error} <button type="button" className="text-button" onClick={onRetry}>Coba lagi</button></p>}
        {!loading && !error && conversations.length === 0 && <p className="muted history-empty">Belum ada percakapan.</p>}
        <ul>
          {conversations.map((c) => {
            const title = c.title ?? 'Sesi baru'
            return (
              <li key={c.id} className="conversation-row">
                <button
                  type="button"
                  className={c.id === selectedId ? 'conversation selected' : 'conversation'}
                  onClick={() => onSelect(c.id)}
                  title={title}
                >
                  {title}
                </button>
                {/* Tombol bersaudara, bukan bersarang di tombol baris: klik hapus tidak boleh ikut membuka sesi. */}
                <button
                  type="button"
                  className="icon-button conversation-delete"
                  aria-label={`Hapus sesi ${title}`}
                  title={`Hapus sesi ${title}`}
                  ref={(el) => {
                    if (el) rowDelete.current.set(c.id, el)
                    else rowDelete.current.delete(c.id)
                  }}
                  onClick={() => setConfirm(c)}
                >
                  <Icon name="close" size={16} />
                </button>
              </li>
            )
          })}
        </ul>
      </div>
      <div className="sidebar-footer">
        <span className="account-avatar" aria-hidden="true">{username.slice(0, 1).toUpperCase()}</span>
        <span title={username}>{username}</span>
        <button type="button" className="icon-button" onClick={onLogout} aria-label="Keluar"><Icon name="logout" /></button>
      </div>
      {confirm && <DeleteSessionDialog conversation={confirm} onClosed={handleClosed} />}
    </aside>
  )
}
