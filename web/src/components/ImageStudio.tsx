import { useEffect, useState } from 'react'
import { getArtifact } from '../api/artifacts'
import type { ArtifactSummary } from '../api/types'
import { MAX_VARIANTS, RATIOS, STRENGTHS, STYLES, buildGeneratePrompt, buildVariationPrompt, groupBatches } from '../svg'
import type { Ratio, Strength, Style } from '../svg'
import { prepareSvg, svgDataUrl } from '../svgPrepare'

const COUNTS = Array.from({ length: MAX_VARIANTS }, (_, i) => i + 1)

/** Formulir "Generate gambar": prompt, gaya, rasio, jumlah. Hasilnya vektor SVG oleh AI, bukan foto. */
export function GenerateImageForm({ busy, onSubmit }: { busy: boolean; onSubmit: (prompt: string, count: number) => Promise<void> }) {
  const [prompt, setPrompt] = useState('')
  const [style, setStyle] = useState<Style>('datar')
  const [ratio, setRatio] = useState<Ratio>('1:1')
  const [count, setCount] = useState(1)
  const [error, setError] = useState<string | null>(null)

  const submit = async () => {
    setError(null)
    try {
      await onSubmit(buildGeneratePrompt({ prompt, style, ratio, count }), count)
      setPrompt('')
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <form className="canvas-studio" onSubmit={(e) => { e.preventDefault(); void submit() }} aria-label="Generate gambar">
      <p className="muted">AI membuat <strong>gambar vektor SVG</strong> (ilustrasi, ikon, bagan sederhana) — bukan foto atau gambar raster.</p>
      <textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} maxLength={600} rows={3} placeholder="Deskripsikan gambar yang diinginkan" aria-label="Deskripsi gambar" />
      <div className="canvas-studio-row">
        <label>Gaya <select value={style} onChange={(e) => setStyle(e.target.value as Style)}>{Object.keys(STYLES).map((s) => <option key={s} value={s}>{s}</option>)}</select></label>
        <label>Rasio <select value={ratio} onChange={(e) => setRatio(e.target.value as Ratio)}>{Object.keys(RATIOS).map((r) => <option key={r} value={r}>{r}</option>)}</select></label>
        <label>Jumlah <select value={count} onChange={(e) => setCount(Number(e.target.value))}>{COUNTS.map((n) => <option key={n} value={n}>{n}</option>)}</select></label>
        <button type="submit" className="primary-button" disabled={busy || prompt.trim() === ''}>{busy ? 'AI sedang menjawab…' : 'Buat gambar'}</button>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
    </form>
  )
}

/** Formulir "Variasi": jumlah, kekuatan, dan apa yang dipertahankan; hasil mencatat asal-usulnya. */
export function VariationForm({ artifact, version, busy, onSubmit }: {
  artifact: { id: string; title: string }
  version: number
  busy: boolean
  onSubmit: (prompt: string, count: number) => Promise<void>
}) {
  const [count, setCount] = useState(2)
  const [strength, setStrength] = useState<Strength>('sedang')
  const [keepComposition, setKeepComposition] = useState(true)
  const [keepColors, setKeepColors] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async () => {
    setError(null)
    try {
      await onSubmit(buildVariationPrompt({ title: artifact.title, artifactId: artifact.id, version, count, strength, keepComposition, keepColors }), count)
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <form className="canvas-studio" onSubmit={(e) => { e.preventDefault(); void submit() }} aria-label="Variasi gambar">
      <div className="canvas-studio-row">
        <label>Jumlah <select value={count} onChange={(e) => setCount(Number(e.target.value))}>{COUNTS.map((n) => <option key={n} value={n}>{n}</option>)}</select></label>
        <label>Kekuatan <select value={strength} onChange={(e) => setStrength(e.target.value as Strength)}>{Object.keys(STRENGTHS).map((s) => <option key={s} value={s}>{s}</option>)}</select></label>
        <label><input type="checkbox" checked={keepComposition} onChange={(e) => setKeepComposition(e.target.checked)} /> Pertahankan komposisi</label>
        <label><input type="checkbox" checked={keepColors} onChange={(e) => setKeepColors(e.target.checked)} /> Pertahankan warna</label>
        <button type="submit" className="primary-button" disabled={busy}>{busy ? 'AI sedang menjawab…' : `Buat ${count} variasi`}</button>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
    </form>
  )
}

function Thumb({ artifact, onOpen, sourceTitle }: { artifact: ArtifactSummary; onOpen: () => void; sourceTitle: string | null }) {
  const [src, setSrc] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    let cancelled = false
    getArtifact(artifact.id)
      .then((a) => {
        const prepared = prepareSvg(a.content)
        if (cancelled) return
        if ('error' in prepared) setFailed(true)
        else setSrc(svgDataUrl(prepared.svg))
      })
      .catch(() => !cancelled && setFailed(true))
    return () => {
      cancelled = true
    }
  }, [artifact.id, artifact.currentVersion])
  return (
    <button type="button" className="canvas-thumb" onClick={onOpen} title={artifact.title}>
      <span className="canvas-thumb-img">
        {src ? <img src={src} alt="" /> : <span className="muted">{failed ? 'Gagal dimuat' : 'Memuat…'}</span>}
      </span>
      <span className="canvas-thumb-title">{artifact.title}</span>
      <span className="muted canvas-thumb-meta">v{artifact.currentVersion}{sourceTitle ? ` · turunan dari ${sourceTitle}${artifact.derivedFromVersion ? ` v${artifact.derivedFromVersion}` : ''}` : ''}</span>
    </button>
  )
}

/**
 * Riwayat gambar per batch (gambar yang dibuat oleh pesan pemicu yang sama). `pending` > 0 = AI sedang membuat gambar:
 * kerangka (skeleton) ditampilkan selama tiket chat benar-benar berjalan, bukan animasi buatan.
 */
export function ImageGallery({ images, all, pending, onOpen }: {
  images: ArtifactSummary[]
  all: ArtifactSummary[]
  pending: number
  onOpen: (id: string) => void
}) {
  const batches = groupBatches(images)
  const titleOf = (id: string | null | undefined) => (id ? (all.find((a) => a.id === id)?.title ?? 'sumber yang sudah dihapus') : null)
  return (
    <div className="canvas-gallery" aria-label="Riwayat gambar" aria-busy={pending > 0}>
      {pending > 0 && (
        <section aria-label="Sedang dibuat">
          <h3>Sedang dibuat…</h3>
          <div className="canvas-thumb-grid" role="status">
            {Array.from({ length: pending }, (_, i) => <span key={i} className="canvas-thumb canvas-skeleton" aria-label="Menunggu hasil AI" />)}
          </div>
        </section>
      )}
      {batches.length === 0 && pending === 0 && <p className="muted canvas-empty">Belum ada gambar. Buat dengan tombol “+ Gambar (AI)”.</p>}
      {batches.map((batch, i) => (
        <section key={batch.key} aria-label={`Batch ${batches.length - i}`}>
          <h3>Batch {batches.length - i}{batch.key === 'manual' ? ' · dibuat manual' : ''} <span className="muted">· {batch.items.length} gambar</span></h3>
          <div className="canvas-thumb-grid">
            {batch.items.map((a) => <Thumb key={a.id} artifact={a} sourceTitle={titleOf(a.derivedFromId)} onOpen={() => onOpen(a.id)} />)}
          </div>
        </section>
      ))}
    </div>
  )
}
