import { useEffect, useRef, useState } from 'react'
import { isImageContentType } from '../api/attachments'
import { errorMessage, fetchAttachmentBlob } from '../api/client'
import type { AttachmentDto } from '../api/types'

interface Props {
  attachment: AttachmentDto
  /** Kelas tambahan dari pemanggil (mis. thumbnail rapat di chip composer). */
  className?: string
}

/**
 * Pratinjau PNG/JPEG ber-JWT: blob diambil lewat fetch ber-Authorization, bukan `<img src>` ke endpoint
 * (tag img tidak membawa header). URL objek dicabut saat unmount/ganti lampiran dan fetch dibatalkan,
 * supaya gambar milik sesi lama tidak pernah muncul di akun baru. Tipe selain PNG/JPEG kanonik → tidak ada pratinjau.
 */
export function AttachmentImage({ attachment, className }: Props) {
  const [preview, setPreview] = useState<{ downloadUrl: string; src: string | null; error: string | null } | null>(null)
  const dialog = useRef<HTMLDialogElement>(null)
  const thumb = useRef<HTMLButtonElement>(null)
  const previewable = isImageContentType(attachment.contentType)
  // URL identitas berbeda langsung menjadi loading; respons/foto lama tidak ditampilkan sambil fetch baru berjalan.
  const current = preview?.downloadUrl === attachment.downloadUrl ? preview : null
  const src = current?.src ?? null
  const error = current?.error ?? null

  useEffect(() => {
    if (!previewable) return
    const ctrl = new AbortController()
    let url: string | null = null
    fetchAttachmentBlob(attachment.downloadUrl, ctrl.signal)
      .then((blob) => {
        if (ctrl.signal.aborted) return
        // Header respons bisa berbeda dari yang diklaim: hanya tipe kanonik yang dirender sebagai gambar.
        if (!isImageContentType(blob.type)) throw new Error('Lampiran bukan gambar PNG/JPEG/WEBP yang bisa ditampilkan.')
        url = URL.createObjectURL(blob)
        setPreview({ downloadUrl: attachment.downloadUrl, src: url, error: null })
      })
      .catch((e: unknown) => {
        if (!ctrl.signal.aborted) setPreview({ downloadUrl: attachment.downloadUrl, src: null, error: errorMessage(e) })
      })
    return () => {
      ctrl.abort()
      if (url) URL.revokeObjectURL(url)
    }
  }, [previewable, attachment.downloadUrl])

  if (!previewable) return null

  return (
    <div className={`attachment-image ${className ?? ''}`}>
      {error && <p className="error">Pratinjau gagal: {error}</p>}
      {!error && src === null && <p className="muted">Memuat pratinjau…</p>}
      {src && (
        <>
          <button
            type="button"
            ref={thumb}
            className="attachment-thumb"
            onClick={() => dialog.current?.showModal()}
            aria-label={`Perbesar ${attachment.fileName}`}
          >
            <img src={src} alt={`Pratinjau ${attachment.fileName}`} />
          </button>
          {/* <dialog> native: Esc dan klik backdrop menutup sendiri, tanpa dependency. */}
          <dialog
            ref={dialog}
            className="attachment-dialog"
            aria-label={`Pratinjau besar ${attachment.fileName}`}
            onClose={() => thumb.current?.focus()} // fokus kembali ke thumbnail, termasuk di browser yang tidak memfokuskan tombol saat klik
            onClick={(e) => {
              if (e.target === dialog.current) dialog.current?.close()
            }}
          >
            <img src={src} alt={`Pratinjau besar ${attachment.fileName}`} />
            <button type="button" onClick={() => dialog.current?.close()}>
              Tutup
            </button>
          </dialog>
        </>
      )}
    </div>
  )
}
