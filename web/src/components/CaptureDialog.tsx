import { useEffect, useRef, useState } from 'react'
import { Icon } from './NusaUi'

type CaptureSource = 'camera' | 'screen'

/** Pesan jujur per penyebab; izin ditolak dan pembatalan pemilih layar sama-sama NotAllowedError di browser. */
function captureError(source: CaptureSource, e: unknown): string {
  const name = e instanceof DOMException ? e.name : ''
  const what = source === 'camera' ? 'kamera' : 'layar'
  if (name === 'NotAllowedError') return `Akses ${what} ditolak atau dibatalkan.`
  if (name === 'NotFoundError') return 'Kamera tidak ditemukan di perangkat ini.'
  if (name === 'NotReadableError') return `${what[0].toUpperCase()}${what.slice(1)} sedang dipakai aplikasi lain.`
  return e instanceof Error ? e.message : `Gagal membuka ${what}.`
}

/**
 * Pratinjau langsung lalu satu frame diambil jadi berkas. Foto = JPEG, screenshot = PNG (teks layar tetap tajam).
 * Berkas hasil diserahkan ke jalur unggah biasa, jadi format/ukuran/jumlah tetap mengikuti kebijakan server.
 * Track media selalu dihentikan saat dialog tertutup atau komponen dilepas.
 */
function CaptureDialog({ source, onCapture, onClosed }: { source: CaptureSource; onCapture: (file: File) => void; onClosed: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const video = useRef<HTMLVideoElement>(null)
  const [error, setError] = useState<string | null>(null)
  const [ready, setReady] = useState(false)

  useEffect(() => {
    dialog.current?.showModal()
    let cancelled = false
    let stream: MediaStream | null = null
    const request = source === 'camera'
      ? navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false })
      : navigator.mediaDevices.getDisplayMedia({ video: true, audio: false })
    request
      .then(async (s) => {
        if (cancelled) return s.getTracks().forEach((t) => t.stop())
        stream = s
        // Pengguna menghentikan berbagi dari bilah browser: dialog tidak boleh menampilkan bingkai beku.
        s.getVideoTracks()[0]?.addEventListener('ended', () => dialog.current?.close())
        const el = video.current
        if (!el) return
        el.srcObject = s
        await el.play()
        setReady(true)
      })
      .catch((e: unknown) => { if (!cancelled) setError(captureError(source, e)) })
    return () => {
      cancelled = true
      stream?.getTracks().forEach((t) => t.stop())
    }
  }, [source])

  const capture = () => {
    const el = video.current
    if (!el || !ready || !el.videoWidth) return
    const canvas = document.createElement('canvas')
    canvas.width = el.videoWidth
    canvas.height = el.videoHeight
    canvas.getContext('2d')?.drawImage(el, 0, 0)
    const png = source === 'screen'
    canvas.toBlob((blob) => {
      if (!blob) return setError('Gagal mengambil gambar.')
      const stamp = new Date().toISOString().replace(/\D/g, '').slice(0, 14)
      onCapture(new File([blob], `${png ? 'screenshot' : 'foto'}-${stamp}.${png ? 'png' : 'jpg'}`, { type: blob.type }))
      dialog.current?.close()
    }, png ? 'image/png' : 'image/jpeg', 0.92)
  }

  const label = source === 'camera' ? 'Ambil foto' : 'Ambil screenshot'
  return (
    <dialog
      ref={dialog}
      className="capture-dialog"
      aria-label={label}
      onClose={onClosed}
      onClick={(e) => { if (e.target === dialog.current) dialog.current?.close() }}
    >
      <h2 className="compare-title">{label}</h2>
      {error ? (
        <p className="error" role="alert">{error}</p>
      ) : (
        <video ref={video} className="capture-video" muted playsInline aria-label="Pratinjau langsung" />
      )}
      <div className="compare-actions">
        <button type="button" className="ghost-button" onClick={() => dialog.current?.close()}>Batal</button>
        <button type="button" className="primary-button" disabled={!ready || error !== null} onClick={capture}>Ambil gambar</button>
      </div>
    </dialog>
  )
}

/**
 * Tombol kamera dan screenshot. Hanya tampil bila browser menyediakan API-nya (konteks aman; getDisplayMedia
 * umumnya tidak ada di ponsel): tombol yang tidak mungkin bekerja tidak ditampilkan.
 */
export function CaptureButtons({ disabled, onCapture }: { disabled: boolean; onCapture: (file: File) => void }) {
  const [source, setSource] = useState<CaptureSource | null>(null)
  const media = typeof navigator === 'undefined' ? undefined : navigator.mediaDevices
  return (
    <>
      {media?.getUserMedia && (
        <button type="button" className="ns-icon-btn" aria-label="Ambil foto" title="Ambil foto" disabled={disabled} onClick={() => setSource('camera')}>
          <Icon name="camera" size={19} />
        </button>
      )}
      {media?.getDisplayMedia && (
        <button type="button" className="ns-icon-btn" aria-label="Ambil screenshot" title="Ambil screenshot" disabled={disabled} onClick={() => setSource('screen')}>
          <Icon name="screen" size={19} />
        </button>
      )}
      {source && <CaptureDialog source={source} onCapture={onCapture} onClosed={() => setSource(null)} />}
    </>
  )
}
