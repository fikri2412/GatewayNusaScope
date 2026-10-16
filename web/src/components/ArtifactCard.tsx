import type { ArtifactKind, ArtifactSummary } from '../api/types'
import { Icon } from './NusaUi'

const ICONS: Record<ArtifactKind, 'file' | 'diagram' | 'grid' | 'image' | 'code'> = { dokumen: 'file', diagram: 'diagram', spreadsheet: 'grid', gambar: 'image', kode: 'code' }

const kindText = (a: ArtifactSummary) =>
  a.kind === 'dokumen' ? 'Dokumen' : a.kind === 'diagram' ? 'Diagram' : a.kind === 'spreadsheet' ? 'Spreadsheet' : a.kind === 'gambar' ? 'Gambar' : `Kode · ${a.language ?? 'text'}`

/** Kartu artifact di bawah jawaban AI: ikon jenis, judul, jenis (+ versi bila sudah diubah). Mengklik membuka Canvas. */
export function ArtifactCard({ artifact, active, onOpen }: { artifact: ArtifactSummary; active: boolean; onOpen: (id: string) => void }) {
  return (
    <button type="button" className={`artifact-card kind-${artifact.kind}`} aria-current={active ? 'true' : undefined} onClick={() => onOpen(artifact.id)}>
      <span className="artifact-card-tile" aria-hidden="true"><Icon name={ICONS[artifact.kind]} size={22} /></span>
      <span className="artifact-card-text">
        <span className="artifact-card-title">{artifact.title}</span>
        <span className="artifact-card-meta">{kindText(artifact)}{artifact.currentVersion > 1 ? ` · v${artifact.currentVersion}` : ''}</span>
      </span>
    </button>
  )
}
