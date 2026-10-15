import { useRef, useState, type ChangeEvent, type ClipboardEvent, type DragEvent, type KeyboardEvent } from 'react'
import { attachmentLimitError, attachmentsPolicyError, isImageContentType, uploadError, visionBlockReason } from '../api/attachments'
import { errorMessage, type UploadOptions } from '../api/client'
import { isEligible } from '../api/models'
import type { AttachmentDto, AttachmentPolicyDto, ModelInfoDto } from '../api/types'
import type { ThinkingMode } from '../api/session'
import { AttachmentImage } from './AttachmentPreview'
import { CaptureButtons } from './CaptureDialog'
import { ModelSelect } from './ModelSelect'
import { Icon } from './NusaUi'
import './Composer.css'

const MAX_LENGTH = 8000
/** Chip saran beranda: mengisi draft, tidak pernah mengirim sendiri. */
const SUGGESTIONS = ['Ringkas dokumen', 'Bantu tulis kode', 'Brainstorm ide', 'Analisis data']

interface Props {
  models: ModelInfoDto[] | null
  modelsError: string | null
  onReloadModels: () => void
  /** Kebijakan lampiran efektif; null = belum termuat. Selama null atau error, lampiran diblokir. */
  policy: AttachmentPolicyDto | null
  policyError: string | null
  onReloadPolicy: () => void
  /** Baca kebijakan terbaru sebelum unggah/kirim; null = gagal baca, operasi lampiran dibatalkan. */
  onRefreshPolicy: () => Promise<AttachmentPolicyDto | null>
  /** Alias tersimpan percakapan; jadi pilihan awal hanya bila masih layak. */
  storedModelAlias: string | null
  thinkMode: ThinkingMode
  onThinkModeChange: (mode: ThinkingMode) => void
  onSend: (content: string, attachmentIds: string[], model: string, thinkMode: ThinkingMode) => Promise<void>
  onUpload: (file: File, options: UploadOptions) => Promise<AttachmentDto>
  /** Mode beranda (sesi baru): sapaan, kartu model, dan chip saran ikut tampil. */
  home?: boolean
}

export function Composer({
  models, modelsError, onReloadModels, policy, policyError, onReloadPolicy, onRefreshPolicy, storedModelAlias, thinkMode, onThinkModeChange, onSend, onUpload,
  home = false,
}: Props) {
  const [text, setText] = useState('')
  const [attachments, setAttachments] = useState<AttachmentDto[]>([])
  const [model, setModel] = useState('')
  const [uploading, setUploading] = useState(false)
  // Progres byte nyata dari unggahan yang sedang berjalan; percent null = ukuran total belum diketahui.
  const [progress, setProgress] = useState<{ name: string; percent: number | null } | null>(null)
  const uploadAbort = useRef<AbortController | null>(null)
  const [sending, setSending] = useState(false)
  const [dragging, setDragging] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [webSearch, setWebSearch] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)
  const textarea = useRef<HTMLTextAreaElement>(null)
  // Jam disimpan sekali saat composer dibuka, bukan membaca waktu selama render.
  const [hour] = useState(() => new Date().getHours())
  const greeting = hour < 11 ? 'Selamat pagi.' : hour < 15 ? 'Selamat siang.' : hour < 19 ? 'Selamat sore.' : 'Selamat malam.'

  // Mengganti pilihan hanya memengaruhi kiriman berikutnya; tanpa pilih-otomatis model pertama.
  const isAuto = model === 'auto' || (!model && storedModelAlias === 'auto')
  const modelValue = isAuto ? 'auto' : model || (storedModelAlias && models?.some((m) => m.id === storedModelAlias && isEligible(m)) ? storedModelAlias : '')
  const selectedModel = isAuto ? null : models?.find((m) => m.id === modelValue && isEligible(m)) ?? null
  const sendModel = isAuto ? 'auto' : selectedModel?.id ?? null
  // PNG/JPEG butuh model vision terkonfirmasi; mode Otomatis akan memilihkan model vision di server.
  const visionBlock = isAuto ? null : modelValue ? visionBlockReason(selectedModel?.supportsVision, attachments) : null

  // Tanpa kebijakan efektif, batas unggah/kirim tidak diketahui: lampiran diblokir, bukan ditebak dari angka lama.
  const policyReady = policy !== null && policyError === null
  const canSend =
    text.trim().length > 0 && !sending && !uploading && modelsError === null && sendModel !== null && visionBlock === null &&
    (attachments.length === 0 || policyReady)
  // Batas lampiran per pesan: lampiran yang sudah terpasang tetap apa adanya, hanya penambahan yang dihentikan.
  const limitNote = policy ? attachmentLimitError(attachments.length, policy) : null
  const attachBlocked = uploading || sending || limitNote !== null || !policyReady

  const submit = async () => {
    if (!canSend || sendModel === null) return
    setSending(true)
    setError(null)
    try {
      if (attachments.length > 0) {
        // Batas bisa menyempit setelah file diunggah: periksa ulang terhadap kebijakan terbaru sebelum antre.
        const fresh = await onRefreshPolicy()
        if (!fresh) return
        const problem = attachmentsPolicyError(attachments, fresh)
        if (problem) return setError(problem)
      }
      const contentToSend = webSearch ? `[Pencarian web aktif] ${text}` : text
      await onSend(contentToSend, attachments.map((a) => a.id), sendModel, thinkMode)
      setText('')
      setAttachments([])
    } catch (e) {
      setError(errorMessage(e)) // teks, lampiran, dan pilihan dipertahankan supaya bisa dikirim ulang
      onReloadModels() // katalog bisa berubah di server; segarkan kelayakan alias
      if (attachments.length > 0) void onRefreshPolicy() // kebijakan lampiran juga bisa bergerak di server
    } finally {
      setSending(false)
    }
  }

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      void submit()
    }
  }

  /**
   * Satu jalur unggah untuk pemilih file, drag & drop, dan tempel: semua lewat pemeriksaan kebijakan
   * yang sama, jadi tidak ada pintu masuk file yang melewati validasi server.
   * Selama unggah/kirim berjalan berkas baru ditolak, sama seperti tombol lampirkan yang nonaktif.
   */
  const addFiles = async (files: File[]) => {
    if (files.length === 0 || sending || uploading) return
    setUploading(true)
    setError(null)
    try {
      // Kebijakan gateway bisa berubah kapan saja: nilai untuk pemeriksaan diambil ulang, bukan snapshot di layar.
      const fresh = await onRefreshPolicy()
      if (!fresh) return
      let count = attachments.length
      for (const file of files) {
        const limit = attachmentLimitError(count, fresh)
        if (limit) return setError(limit)
        const problem = uploadError(file, fresh)
        if (problem) return setError(problem)
        const ctrl = new AbortController()
        uploadAbort.current = ctrl
        setProgress({ name: file.name, percent: 0 })
        const uploaded = await onUpload(file, {
          signal: ctrl.signal,
          onProgress: (loaded, total) => setProgress({ name: file.name, percent: total > 0 ? Math.round((loaded / total) * 100) : null }),
        })
        setAttachments((prev) => [...prev, uploaded])
        count += 1
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return setError('Unggahan dibatalkan.')
      setError(errorMessage(err)) // berkas yang sudah lolos unggahan tetap terpasang sebagai draft
      void onRefreshPolicy() // server bisa menolak karena kebijakan berubah; tampilkan nilai terbaru
    } finally {
      uploadAbort.current = null
      setProgress(null)
      setUploading(false)
    }
  }

  const pickFile = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    e.target.value = '' // pilih ulang file yang sama tetap memicu onChange
    if (file) void addFiles([file])
  }

  const onPaste = (e: ClipboardEvent<HTMLTextAreaElement | HTMLInputElement>) => {
    const files = Array.from(e.clipboardData.files)
    if (files.length === 0) return // tempel teks biasa dibiarkan apa adanya
    e.preventDefault()
    void addFiles(files)
  }

  const onDragOver = (e: DragEvent<HTMLDivElement>) => {
    if (!Array.from(e.dataTransfer.types).includes('Files')) return
    e.preventDefault() // seret di luar area ini tidak boleh dianggap drop
    e.dataTransfer.dropEffect = 'copy'
    setDragging(true)
  }

  const onDragLeave = (e: DragEvent<HTMLDivElement>) => {
    // relatedTarget bisa null (kursor keluar jendela); currentTarget.contains menanganinya sebagai "keluar".
    if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDragging(false)
  }

  const onDrop = (e: DragEvent<HTMLDivElement>) => {
    if (!Array.from(e.dataTransfer.types).includes('Files')) return
    e.preventDefault()
    setDragging(false)
    void addFiles(Array.from(e.dataTransfer.files))
  }

  const suggest = (label: string) => {
    // Draft yang sudah ada tidak pernah dibuang: chip menambahkan awalan, bukan menimpa ketikan.
    setText((prev) => (prev.trim() ? `${prev.trimEnd()} ${label} ` : `${label} `))
    requestAnimationFrame(() => {
      const el = textarea.current
      if (!el) return
      el.focus()
      el.setSelectionRange(el.value.length, el.value.length)
    })
  }

  return (
    <div className={home ? 'ns-composer ns-home' : 'ns-composer'}>
      {home && (
        <header className="ns-home-head">
          <h1 className="ns-home-title">{`${greeting} Mulai dari mana?`}</h1>
          <p className="ns-home-sub">Pilih model AI, lalu atur mode berpikirnya.</p>
        </header>
      )}

      {home && (
        <ModelSelect variant="cards" models={models} error={modelsError} value={modelValue} onChange={setModel} onReload={onReloadModels} />
      )}

      <div
        className={`ns-card${attachments.length > 0 ? ' ns-card-files' : ''}${dragging ? ' ns-card-drop' : ''} ${!home ? 'ns-card-chat' : ''}`}
        style={!home ? {
          background: '#FFFFFF',
          border: '1px solid #E4E2DC',
          borderRadius: 18,
          padding: '8px 10px',
          display: 'flex',
          flexDirection: attachments.length > 0 ? 'column' : 'row',
          alignItems: attachments.length > 0 ? 'stretch' : 'center',
          gap: 8,
          boxShadow: '0 1px 2px rgba(26,26,29,.04), 0 8px 24px rgba(26,26,29,.05)',
          position: 'relative',
        } : {
          boxSizing: 'border-box',
          width: '100%',
          maxWidth: 760,
          background: '#FFFFFF',
          border: '1px solid #E4E2DC',
          borderRadius: 20,
          padding: '16px 14px 12px 18px',
          display: 'flex',
          flexDirection: 'column',
          gap: 10,
          boxShadow: '0 1px 2px rgba(26,26,29,.04), 0 8px 24px rgba(26,26,29,.05)',
          position: 'relative',
        }}
        onDragOver={onDragOver}
        onDragLeave={onDragLeave}
        onDrop={onDrop}
      >
        {dragging && (
          <div className="ns-drop" aria-hidden="true">
            <Icon name="upload" size={36} />
            <span className="ns-drop-title">Lepas file di sini</span>
            <span className="ns-drop-sub">
              {policy ? policy.allowedExtensions.join(', ') : 'Format mengikuti kebijakan server'}
            </span>
          </div>
        )}

        {attachments.length > 0 && (
          <ul className="ns-attachments">
            {attachments.map((a) => (
              <li key={a.id} className="ns-attach">
                <div className="ns-attach-tile">
                  {isImageContentType(a.contentType) ? (
                    <AttachmentImage attachment={a} className="ns-attach-img" />
                  ) : (
                    <span className="ns-attach-doc" aria-hidden="true">
                      <Icon name="file" size={22} />
                    </span>
                  )}
                  <button
                    type="button"
                    className="ns-attach-x"
                    disabled={uploading || sending}
                    aria-label={`Hapus ${a.fileName}`}
                    onClick={() => setAttachments((prev) => prev.filter((x) => x.id !== a.id))}
                  >
                    <Icon name="close" size={10} />
                  </button>
                </div>
                <span className="ns-attach-name" title={a.fileName}>{a.fileName}</span>
              </li>
            ))}
          </ul>
        )}
        {attachments.some((a) => isImageContentType(a.contentType)) && (
          <div className="ns-image-actions" role="toolbar" aria-label="Aksi gambar">
            <span className="ns-image-actions-label">Aksi gambar:</span>
            <button
              type="button"
              className="ns-image-chip"
              disabled={sending}
              onClick={() => {
                setText((prev) => (prev.trim() ? `${prev.trimEnd()} Deskripsikan gambar ini.` : 'Deskripsikan gambar ini.'))
                textarea.current?.focus()
              }}
            >
              Deskripsikan
            </button>
            <button
              type="button"
              className="ns-image-chip"
              disabled={sending}
              onClick={() => {
                setText((prev) => (prev.trim() ? `${prev.trimEnd()} Ambil dan salin semua teks yang terlihat pada gambar ini (OCR).` : 'Ambil dan salin semua teks yang terlihat pada gambar ini (OCR).'))
                textarea.current?.focus()
              }}
            >
              Ambil teks (OCR)
            </button>
          </div>
        )}

        {home ? (
          <>
            <textarea
              ref={textarea}
              className="ns-input"
              aria-label="Tulis pesan"
              value={text}
              maxLength={MAX_LENGTH}
              disabled={sending}
              rows={2}
              style={{ border: 'none', outline: 'none', background: 'transparent', resize: 'none', fontFamily: 'inherit', fontSize: 16, color: '#1A1A1D', padding: 0 }}
              placeholder="Tulis pertanyaan atau tempel teks..."
              onChange={(e) => setText(e.target.value)}
              onKeyDown={onKeyDown}
              onPaste={onPaste}
            />

            <div className="ns-bar">
              <input ref={fileInput} type="file" accept={policy?.allowedExtensions.join(',')} hidden onChange={(e) => void pickFile(e)} />
              <button
                type="button"
                className="ns-icon-btn"
                style={{ width: 40, height: 40, borderRadius: 10, border: 'none', outline: 'none', background: 'transparent', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
                aria-label={uploading ? 'Mengunggah…' : 'Lampirkan file'}
                title="Lampirkan file"
                disabled={attachBlocked}
                onClick={() => fileInput.current?.click()}
              >
                {uploading ? <span className="ns-busy" aria-hidden="true" /> : <Icon name="clip" size={19} />}
              </button>
              <CaptureButtons disabled={attachBlocked} onCapture={(file) => void addFiles([file])} />
              <button
                type="button"
                className="ns-icon-btn"
                style={{
                  width: 40,
                  height: 40,
                  borderRadius: 10,
                  border: 'none',
                  outline: 'none',
                  background: webSearch ? 'var(--ns-accent-tint, #eaf3f1)' : 'transparent',
                  color: webSearch ? 'var(--ns-accent, #0f5f57)' : '#55545A',
                  cursor: 'pointer',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
                aria-label="Cari di web"
                title={webSearch ? 'Pencarian web aktif (klik untuk matikan)' : 'Cari di web'}
                aria-pressed={webSearch}
                onClick={() => setWebSearch((prev) => !prev)}
              >
                <Icon name="globe" size={19} />
              </button>

              <div role="tablist" aria-label="Mode berpikir" className="ns-think-toggle" style={{ display: 'flex', gap: 2, background: '#F2F1ED', borderRadius: 10, padding: 3, marginLeft: 4 }}>
                <button
                  type="button"
                  role="tab"
                  aria-selected={thinkMode === 'fast'}
                  className={`ns-think-tab ${thinkMode === 'fast' ? 'active' : ''}`}
                  style={{
                    height: 32,
                    padding: '0 12px',
                    border: 'none',
                    outline: 'none',
                    borderRadius: 8,
                    background: thinkMode === 'fast' ? '#FFFFFF' : 'transparent',
                    boxShadow: thinkMode === 'fast' ? '0 1px 2px rgba(26,26,29,.08)' : 'none',
                    fontFamily: 'inherit',
                    fontSize: 13,
                    fontWeight: 500,
                    color: thinkMode === 'fast' ? '#1A1A1D' : '#6B6A70',
                    cursor: 'pointer',
                  }}
                  onClick={() => onThinkModeChange('fast')}
                >
                  Cepat
                </button>
                <button
                  type="button"
                  role="tab"
                  aria-selected={thinkMode === 'deep'}
                  className={`ns-think-tab ${thinkMode === 'deep' ? 'active' : ''}`}
                  style={{
                    height: 32,
                    padding: '0 12px',
                    border: 'none',
                    outline: 'none',
                    borderRadius: 8,
                    background: thinkMode === 'deep' ? '#FFFFFF' : 'transparent',
                    boxShadow: thinkMode === 'deep' ? '0 1px 2px rgba(26,26,29,.08)' : 'none',
                    fontFamily: 'inherit',
                    fontSize: 13,
                    fontWeight: 500,
                    color: thinkMode === 'deep' ? '#1A1A1D' : '#6B6A70',
                    cursor: 'pointer',
                  }}
                  onClick={() => onThinkModeChange('deep')}
                >
                  Mikir dalam
                </button>
              </div>

              <span className="ns-grow" />
              <span className="ns-model-name-label" style={{ fontSize: 13, color: '#6B6A70' }}>
                {modelValue.toLowerCase().includes('glm') ? 'GLM' : modelValue.toLowerCase().includes('minimax') ? 'MiniMax' : 'DeepSeek'}
              </span>
              <button
                type="button"
                className="ns-send"
                style={{ width: 40, height: 40, borderRadius: 12, border: 'none', outline: 'none', background: '#1A1A1D', color: '#FFFFFF', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
                aria-label="Kirim"
                disabled={!canSend}
                onClick={() => void submit()}
              >
                <Icon name="arrow" size={18} />
              </button>
            </div>
          </>
        ) : (
          <div className="ns-chat-bar">
            <div className="ns-chat-tools">
            <ModelSelect variant="picker" models={models} error={modelsError} value={modelValue} onChange={setModel} onReload={onReloadModels} />
            <input ref={fileInput} type="file" accept={policy?.allowedExtensions.join(',')} hidden onChange={(e) => void pickFile(e)} />
            <button
              type="button"
              className="ns-icon-btn"
              style={{ width: 40, height: 40, borderRadius: 10, border: 'none', outline: 'none', background: 'transparent', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
              aria-label={uploading ? 'Mengunggah…' : 'Lampirkan file'}
              title="Lampirkan file"
              disabled={attachBlocked}
              onClick={() => fileInput.current?.click()}
            >
              {uploading ? <span className="ns-busy" aria-hidden="true" /> : <Icon name="clip" size={19} />}
            </button>
            <CaptureButtons disabled={attachBlocked} onCapture={(file) => void addFiles([file])} />
            <button
              type="button"
              className="ns-icon-btn"
              style={{
                width: 36,
                height: 36,
                borderRadius: 8,
                border: 'none',
                outline: 'none',
                background: webSearch ? 'var(--ns-accent-tint, #eaf3f1)' : 'transparent',
                color: webSearch ? 'var(--ns-accent, #0f5f57)' : '#55545A',
                cursor: 'pointer',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                flex: 'none',
              }}
              aria-label="Cari di web"
              title={webSearch ? 'Pencarian web aktif (klik untuk matikan)' : 'Cari di web'}
              aria-pressed={webSearch}
              onClick={() => setWebSearch((prev) => !prev)}
            >
              <Icon name="globe" size={18} />
            </button>
            <div role="tablist" aria-label="Mode berpikir" className="ns-think-toggle" style={{ display: 'flex', gap: 2, background: '#F2F1ED', borderRadius: 8, padding: 2, flex: 'none' }}>
              <button
                type="button"
                role="tab"
                aria-selected={thinkMode === 'fast'}
                className={`ns-think-tab ${thinkMode === 'fast' ? 'active' : ''}`}
                style={{
                  height: 28,
                  padding: '0 8px',
                  border: 'none',
                  outline: 'none',
                  borderRadius: 6,
                  background: thinkMode === 'fast' ? '#FFFFFF' : 'transparent',
                  boxShadow: thinkMode === 'fast' ? '0 1px 2px rgba(26,26,29,.08)' : 'none',
                  fontFamily: 'inherit',
                  fontSize: 12,
                  fontWeight: 500,
                  color: thinkMode === 'fast' ? '#1A1A1D' : '#6B6A70',
                  cursor: 'pointer',
                }}
                onClick={() => onThinkModeChange('fast')}
              >
                Cepat
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={thinkMode === 'deep'}
                className={`ns-think-tab ${thinkMode === 'deep' ? 'active' : ''}`}
                style={{
                  height: 28,
                  padding: '0 8px',
                  border: 'none',
                  outline: 'none',
                  borderRadius: 6,
                  background: thinkMode === 'deep' ? '#FFFFFF' : 'transparent',
                  boxShadow: thinkMode === 'deep' ? '0 1px 2px rgba(26,26,29,.08)' : 'none',
                  fontFamily: 'inherit',
                  fontSize: 12,
                  fontWeight: 500,
                  color: thinkMode === 'deep' ? '#1A1A1D' : '#6B6A70',
                  cursor: 'pointer',
                }}
                onClick={() => onThinkModeChange('deep')}
              >
                Mikir dalam
              </button>
            </div>
            </div>
            <div className="ns-chat-input-wrap">
            <input
              className="ns-chat-input"
              style={{
                flex: 1,
                minWidth: 0,
                border: 'none',
                outline: 'none',
                background: 'transparent',
                fontFamily: 'inherit',
                fontSize: 15,
                color: '#1A1A1D',
                paddingLeft: 4,
              }}
              placeholder="Balas..."
              value={text}
              disabled={sending}
              onChange={(e) => setText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault()
                  void submit()
                }
              }}
              onPaste={onPaste}
            />
            <button
              type="button"
              className="ns-send"
              style={{
                width: 40,
                height: 40,
                borderRadius: 12,
                border: 'none',
                background: '#1A1A1D',
                color: '#FFFFFF',
                cursor: 'pointer',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                flex: 'none',
              }}
              aria-label="Kirim"
              disabled={!canSend}
              onClick={() => void submit()}
            >
              <Icon name="arrow" size={18} />
            </button>
            </div>
          </div>
        )}
      </div>

      {progress && (
        <p className="ns-note ns-upload" role="status">
          <progress max={100} value={progress.percent ?? undefined} aria-label={`Mengunggah ${progress.name}`} />
          <span className="ns-upload-name" title={progress.name}>
            {progress.percent === null ? 'Mengunggah' : progress.percent >= 100 ? 'Memeriksa' : `${progress.percent}%`} {progress.name}
          </span>
          <button type="button" className="ns-link" onClick={() => uploadAbort.current?.abort()}>
            Batal
          </button>
        </p>
      )}
      {error && <p className="error ns-note" role="alert">{error}</p>}
      {policyError && (
        <p className="error ns-note" role="alert">
          Kebijakan lampiran tidak tersedia: {policyError}{' '}
          <button type="button" className="ns-link" onClick={onReloadPolicy}>
            Coba lagi
          </button>
        </p>
      )}

      {home && (
        <div className="ns-chips">
          {SUGGESTIONS.map((label) => (
            <button key={label} type="button" className="ns-chip" disabled={sending} onClick={() => suggest(label)}>
              {label}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
