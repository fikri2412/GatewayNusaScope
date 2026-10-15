import { useRef, useState } from 'react'
import { downloadAttachment, errorMessage } from '../api/client'
import { saveAnswer } from '../api/collection'
import type { MessageDto } from '../api/types'
import type { StreamDraft } from '../streamDrafts'
import { AttachmentImage } from './AttachmentPreview'
import { CompareDialog } from './CompareDialog'
import { Markdown } from './Markdown'
import { Icon, ModelGlyph } from './NusaUi'
import './MessageBubble.css'

type MessageBubbleProps = {
  message: MessageDto
  conversationId: string
  /** Ada tiket utama yang berjalan di percakapan ini: aksi yang mengantrekan tiket baru ditahan dulu. */
  turnPending: boolean
  onRegenerate: (messageId: number) => Promise<void>
  onCompare: (messageId: number, models: string[]) => Promise<void>
}

/** Detik gaya Indonesia ("1,2 dtk"), deterministik tanpa locale. */
const formatSeconds = (ms: number) => `${(ms / 1000).toFixed(1).replace('.', ',')} dtk`

/** Rincian waktu untuk tooltip; bagian yang tidak diketahui (mis. token pertama non-stream) tidak diklaim. */
function timingTitle(message: MessageDto) {
  const parts: string[] = []
  if (message.queueWaitMs !== null) parts.push(`Antre ${formatSeconds(message.queueWaitMs)}`)
  if (message.firstTokenMs !== null) parts.push(`Token pertama ${formatSeconds(message.firstTokenMs)}`)
  if (message.totalMs !== null) parts.push(`Total ${formatSeconds(message.totalMs)}`)
  return parts.length > 0 ? parts.join(' · ') : undefined
}

export function MessageBubble(props: MessageBubbleProps) {
  const { message, turnPending, onRegenerate, onCompare } = props
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)
  const [comparing, setComparing] = useState(false)
  const compareTrigger = useRef<HTMLButtonElement>(null)
  const isAnswer = message.role === 'assistant'
  // Tiket baru hanya masuk akal untuk jawaban yang ada isinya; pesan user tidak punya aksi ini.
  const hasAnswer = isAnswer && Boolean(message.content)
  const blocked = turnPending || busy

  /** Galat aksi tampil di bubble ini (bukan di header percakapan): jawaban mana yang gagal harus jelas. */
  const run = async (action: () => Promise<unknown>) => {
    setError(null)
    setBusy(true)
    try {
      await action()
    } catch (e: unknown) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const copy = async () => {
    setError(null)
    try {
      await navigator.clipboard.writeText(message.content ?? '')
      setCopied(true)
    } catch {
      setError('Gagal menyalin. Pilih teks jawaban lalu salin secara manual.')
    }
  }

  return (
    <div className={`bubble bubble-${message.role}`}>
      {isAnswer && (
        <div className="assistant-heading">
          <ModelGlyph alias={message.modelAlias} />
          {/* Alias selalu milik jawaban ini: badge jawaban lama tidak pernah diganti model yang dipilih sekarang. */}
          <span className="bubble-model">{message.modelAlias ?? 'NusaScope'}</span>
          {!message.isPrimary && <span className="alt-badge">Alternatif</span>}
          {message.totalMs !== null && (
            <span className="bubble-timing" title={timingTitle(message)}>
              {formatSeconds(message.totalMs)}
            </span>
          )}
          {message.tokensUsed !== null && (
            <span className="bubble-tokens">{message.tokensUsed.toLocaleString('id-ID')} token</span>
          )}
        </div>
      )}
      {/* Jawaban assistant sebagai markdown; pesan user tetap teks biasa. */}
      {isAnswer ? <Markdown text={message.content ?? ''} /> : <div className="bubble-text">{message.content}</div>}
      {message.finishReason === 'length' && (
        // Terpotong oleh batas panjang model: ditandai jelas, tanpa permintaan ulang otomatis (biaya diulang tanpa izin user).
        <p className="bubble-truncated">Jawaban terpotong oleh batas panjang model — minta lanjutannya lewat pesan baru bila perlu.</p>
      )}
      {message.attachments.map((a) => (
        <div key={a.id} className="bubble-attachment-row">
          <AttachmentImage attachment={a} />
          <button
            type="button"
            className="bubble-attachment"
            onClick={() => void downloadAttachment(a.downloadUrl, a.fileName).catch((e: unknown) => setError(errorMessage(e)))}
          >
            {a.fileName}
          </button>
        </div>
      ))}
      {hasAnswer && (
        <div className="message-actions">
          <button type="button" className="icon-button" aria-label="Salin jawaban" title="Salin jawaban"
            onClick={() => void copy()}>
            <Icon name={copied ? 'check' : 'copy'} size={17} />
          </button>
          {copied && <span className="copy-status" role="status">Disalin.</span>}
          <button type="button" className="action-button" disabled={blocked}
            onClick={() => void run(() => onRegenerate(message.id))}>
            Jawab ulang
          </button>
          <button type="button" className="action-button" ref={compareTrigger} disabled={blocked}
            onClick={() => setComparing(true)}>
            Bandingkan dengan model lain
          </button>
          <button type="button" className="action-button" disabled={blocked || saved}
            onClick={() => void run(async () => {
              await saveAnswer(message.id)
              setSaved(true)
            })}>
            {saved && <Icon name="check" size={16} />}
            {saved ? 'Tersimpan' : 'Simpan ke koleksi'}
          </button>
        </div>
      )}
      {error && <p className="error" role="alert">{error}</p>}
      <time className="bubble-time" dateTime={message.createdAt}>
        {new Date(message.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
      </time>
      {comparing && (
        <CompareDialog
          messageId={message.id}
          onCompare={onCompare}
          onClose={() => {
            setComparing(false)
            compareTrigger.current?.focus() // fokus kembali ke tombol pemicu, termasuk saat Esc menutup dialog
          }}
        />
      )}
    </div>
  )
}

/**
 * Draft sementara dari `streamEvent`: teks tambahan dirender saat itu juga, bukan menunggu seluruh jawaban.
 * Status sinkronisasi/terputus ditulis eksplisit supaya draft tidak terbaca sebagai jawaban yang sudah utuh.
 */
export function DraftBubble({ draft }: { draft: StreamDraft }) {
  return (
    <div className="bubble bubble-assistant bubble-draft">
      <div className="assistant-heading"><ModelGlyph /><span>NusaScope</span><span className="draft-label">Menjawab…</span></div>
      <div className="bubble-text">
        {(draft.text.match(/\s+|\S+\s*/g) ?? []).map((word, index) => <span className="ns-word" key={index}>{word}</span>)}
        {draft.state === 'streaming' && <span className="ns-caret" aria-hidden="true" />}
      </div>
      {draft.state === 'sync' && (
        <p className="draft-note" role="status">
          Menyinkronkan dengan server — sisa jawaban menyusul setelah tersimpan.
        </p>
      )}
      {draft.state === 'aborted' && (
        <p className="draft-note" role="status">
          Stream terputus: ini belum jawaban lengkap, menunggu kelanjutan dari server.
        </p>
      )}
    </div>
  )
}
