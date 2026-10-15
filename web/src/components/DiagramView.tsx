import { useEffect, useMemo, useState } from 'react'
import { normalizeMermaid, sizeSvg } from '../canvas'

/** Sisi terpanjang PNG hasil ekspor; melindungi memori untuk diagram yang sangat besar (≤ 16 MP per sisi 4096). */
const MAX_PNG_SIDE = 4096
const ZOOMS = [0.5, 0.75, 1, 1.5, 2, 3]

type Mermaid = (typeof import('mermaid'))['default']
let mermaidPromise: Promise<Mermaid> | null = null

/**
 * Mermaid dimuat malas (chunk terpisah, aset lokal; tanpa jaringan ke host lain). `securityLevel: 'strict'` +
 * `htmlLabels: false`: tanpa HTML/klik/skrip dari sumber diagram dan tanpa <foreignObject>, sehingga SVG-nya
 * tetap bisa digambar ke canvas untuk PNG.
 */
function loadMermaid(): Promise<Mermaid> {
  mermaidPromise ??= import('mermaid').then((m) => {
    m.default.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: 'neutral',
      htmlLabels: false,
      flowchart: { htmlLabels: false, useMaxWidth: false },
      class: { htmlLabels: false, useMaxWidth: false },
      er: { useMaxWidth: false },
    })
    return m.default
  })
  return mermaidPromise
}

let renderSeq = 0

type Rendered = { svg: string; width: number; height: number }
type State = { source: string; rendered: Rendered | null; error: string | null }

/** SVG → PNG di browser: gambar ke canvas putih skala 2 (dibatasi), tanpa layanan render eksternal. */
async function svgToPng(rendered: Rendered): Promise<Blob> {
  const scale = Math.min(2, MAX_PNG_SIDE / Math.max(rendered.width, rendered.height))
  const image = new Image()
  image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(rendered.svg)}`
  await image.decode()
  const canvas = document.createElement('canvas')
  canvas.width = Math.max(1, Math.round(rendered.width * scale))
  canvas.height = Math.max(1, Math.round(rendered.height * scale))
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('Canvas 2D tidak tersedia di browser ini.')
  ctx.fillStyle = '#ffffff'
  ctx.fillRect(0, 0, canvas.width, canvas.height)
  ctx.drawImage(image, 0, 0, canvas.width, canvas.height)
  return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('Gagal membuat PNG.'))), 'image/png'))
}

function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  document.body.append(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}

/**
 * Hasil render diagram Mermaid. Sumber yang tidak valid menampilkan galat Mermaid apa adanya (bukan diagram
 * kosong/palsu). Gambar disajikan lewat <img> data-URL, jadi isi SVG tidak pernah masuk DOM halaman.
 */
export function DiagramView({ source, pngName }: { source: string; pngName: string }) {
  const clean = useMemo(() => normalizeMermaid(source), [source])
  const [state, setState] = useState<State>({ source: '', rendered: null, error: null })
  const [zoom, setZoom] = useState(1)
  const [pngError, setPngError] = useState<string | null>(null)

  useEffect(() => {
    if (clean === '') return
    let cancelled = false
    // Debounce: render tiap ketukan akan menumpuk pekerjaan Mermaid saat mengetik.
    const timer = setTimeout(() => {
      loadMermaid()
        .then(async (mermaid) => {
          await mermaid.parse(clean) // galat sintaks dilempar di sini, tanpa meninggalkan simpul galat di DOM
          const { svg } = await mermaid.render(`diagram-${++renderSeq}`, clean)
          const sized = sizeSvg(svg)
          if (!sized) throw new Error('Mermaid tidak menghasilkan ukuran diagram yang valid.')
          if (!cancelled) setState({ source: clean, rendered: sized, error: null })
        })
        .catch((e: unknown) => {
          if (!cancelled) setState({ source: clean, rendered: null, error: e instanceof Error ? e.message : String(e) })
        })
    }, 250)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [clean])

  const current = state.source === clean ? state : null
  const rendered = current?.rendered ?? null
  const src = useMemo(() => (rendered ? `data:image/svg+xml;charset=utf-8,${encodeURIComponent(rendered.svg)}` : null), [rendered])

  const exportPng = async () => {
    if (!rendered) return
    setPngError(null)
    try {
      saveBlob(await svgToPng(rendered), pngName)
    } catch (e: unknown) {
      setPngError(e instanceof Error ? e.message : String(e))
    }
  }

  if (clean === '') return <p className="muted canvas-empty">Diagram kosong. Tulis sintaks Mermaid di tab Kode Mermaid.</p>

  return (
    <div className="canvas-diagram">
      <div className="canvas-diagram-bar" role="toolbar" aria-label="Kontrol diagram">
        <button type="button" className="ghost-button" onClick={() => setZoom(ZOOMS[Math.max(0, ZOOMS.indexOf(zoom) - 1)])} disabled={zoom === ZOOMS[0]} aria-label="Perkecil">−</button>
        <button type="button" className="ghost-button" onClick={() => setZoom(1)} aria-label="Zoom 100%">{Math.round(zoom * 100)}%</button>
        <button type="button" className="ghost-button" onClick={() => setZoom(ZOOMS[Math.min(ZOOMS.length - 1, ZOOMS.indexOf(zoom) + 1)])} disabled={zoom === ZOOMS[ZOOMS.length - 1]} aria-label="Perbesar">+</button>
        <button type="button" className="ghost-button" onClick={() => void exportPng()} disabled={!rendered}>Unduh PNG</button>
        {pngError && <span className="error" role="alert">{pngError}</span>}
      </div>
      <div className="canvas-diagram-stage">
        {current === null && <p className="muted" role="status">Merender diagram…</p>}
        {current?.error && <p className="error" role="alert">Sintaks Mermaid tidak valid: {current.error}</p>}
        {src && rendered && (
          <img className="canvas-diagram-img" src={src} alt="Hasil render diagram Mermaid" width={rendered.width * zoom} height={rendered.height * zoom} />
        )}
      </div>
    </div>
  )
}
