import type { ActivityDto } from '../api/types'
import { ModelGlyph } from './NusaUi'
import './MessageBubble.css'

/**
 * Tiket alternatif (Jawab ulang/Bandingkan) belum punya pesan apa pun: ditampilkan sebagai baris tunggu
 * supaya tiket yang sudah diantrekan tidak hilang dari layar, tanpa berpura-pura sudah jadi jawaban.
 */
export function PendingAnswer({ item }: { item: ActivityDto }) {
  const model = item.modelAlias ?? 'model'
  return (
    <div className="bubble bubble-assistant pending-answer">
      <div className="assistant-heading">
        <ModelGlyph alias={item.modelAlias} />
        <span className="bubble-model">{model}</span>
        <span className="alt-badge">Alternatif</span>
      </div>
      <p className="pending-note" role="status">
        Menunggu {model} — {item.status === 'Pending' ? 'dalam antrean' : 'sedang menjawab'}…
      </p>
    </div>
  )
}
