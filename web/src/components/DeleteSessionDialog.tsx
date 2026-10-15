import { useEffect, useRef, useState } from 'react'
import { ApiError, errorMessage } from '../api/client'
import { deleteConversation } from '../api/conversations'
import type { ConversationDto } from '../api/types'

interface Props {
  conversation: ConversationDto
  /** Dipanggil tepat sekali saat dialog tertutup; `deleted` = server sudah menerima 204. */
  onClosed: (deleted: boolean) => void
}

/**
 * Konfirmasi hapus sesi permanen. Native `<dialog>` + `showModal()`: Escape, focus trap, dan klik backdrop
 * ditangani browser, tanpa dependency tambahan.
 */
export function DeleteSessionDialog({ conversation, onClosed }: Props) {
  const dialog = useRef<HTMLDialogElement>(null)
  const cancelButton = useRef<HTMLButtonElement>(null)
  const announced = useRef(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    dialog.current?.showModal()
    // Fokus awal di "Batal": Enter refleks tidak boleh menghapus.
    cancelButton.current?.focus()
  }, [])

  // Tutup lewat Escape/backdrop dan tutup lewat 204 sama-sama satu peristiwa: pemanggil tidak boleh diberi tahu dua kali.
  const announce = (deleted: boolean) => {
    if (announced.current) return
    announced.current = true
    onClosed(deleted)
  }

  const remove = async () => {
    setBusy(true)
    setError(null)
    try {
      await deleteConversation(conversation.id)
      announce(true) // sebelum close(): event `close` yang menyusul sudah tidak berpengaruh
      dialog.current?.close()
    } catch (e: unknown) {
      setError(
        e instanceof ApiError && e.code === 'conversation_busy'
          ? 'Sesi masih memproses jawaban. Coba lagi setelah selesai.'
          : errorMessage(e),
      )
      setBusy(false)
    }
  }

  return (
    <dialog
      ref={dialog}
      className="confirm-dialog"
      aria-labelledby="delete-session-title"
      onClose={() => announce(false)}
      onClick={(e) => {
        const el = dialog.current
        if (el && e.target === el && !busy) el.close()
      }}
    >
      <h2 id="delete-session-title">Hapus sesi ini?</h2>
      <p>Pesan, lampiran, dan file hasil di sesi ini dihapus permanen. Jawaban yang sudah disimpan ke koleksi tetap ada.</p>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="confirm-actions">
        <button type="button" ref={cancelButton} className="ghost-button" disabled={busy} onClick={() => dialog.current?.close()}>
          Batal
        </button>
        <button type="button" className="danger-button" disabled={busy} onClick={() => void remove()}>
          {busy ? 'Menghapus…' : 'Hapus'}
        </button>
      </div>
    </dialog>
  )
}
