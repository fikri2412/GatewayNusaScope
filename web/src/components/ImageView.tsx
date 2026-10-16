import { useMemo, useState } from 'react'
import { prepareSvg, svgDataUrl } from '../svgPrepare'
import { saveBlob, svgToPng } from '../svgExport'
import { UPSCALE_FACTORS, pngDimensions } from '../svg'

const ZOOMS = [0.25, 0.5, 0.75, 1, 1.5, 2]

/**
 * Hasil gambar vektor. Disajikan lewat <img> data-URL (inert: skrip dan sumber luar tidak jalan). Unduh PNG dengan
 * faktor skala (upscale tanpa kehilangan detail karena sumbernya vektor, dibatasi sisi/piksel maksimum) dan unduh SVG
 * yang sudah disanitasi.
 */
export function ImageView({ source, baseName }: { source: string; baseName: string }) {
  const prepared = useMemo(() => prepareSvg(source), [source])
  const [zoom, setZoom] = useState(1)
  const [factor, setFactor] = useState<number>(2)
  const [error, setError] = useState<string | null>(null)

  if ('error' in prepared) return <p className="error canvas-note" role="alert">Gambar tidak dapat ditampilkan: {prepared.error}</p>

  const size = pngDimensions(prepared.width, prepared.height, factor)

  const exportPng = async () => {
    setError(null)
    try {
      saveBlob((await svgToPng(prepared, factor)).blob, `${baseName}-${size.width}x${size.height}.png`)
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <div className="canvas-diagram">
      <div className="canvas-diagram-bar" role="toolbar" aria-label="Kontrol gambar">
        <button type="button" className="ghost-button" aria-label="Perkecil" disabled={zoom === ZOOMS[0]} onClick={() => setZoom(ZOOMS[Math.max(0, ZOOMS.indexOf(zoom) - 1)])}>−</button>
        <button type="button" className="ghost-button" aria-label="Zoom 100%" onClick={() => setZoom(1)}>{Math.round(zoom * 100)}%</button>
        <button type="button" className="ghost-button" aria-label="Perbesar" disabled={zoom === ZOOMS[ZOOMS.length - 1]} onClick={() => setZoom(ZOOMS[Math.min(ZOOMS.length - 1, ZOOMS.indexOf(zoom) + 1)])}>+</button>
        <label className="canvas-scale">
          Upscale
          <select value={factor} onChange={(e) => setFactor(Number(e.target.value))} aria-label="Faktor upscale PNG">
            {UPSCALE_FACTORS.map((f) => <option key={f} value={f}>{f}×</option>)}
          </select>
        </label>
        <button type="button" className="ghost-button" onClick={() => void exportPng()}>Unduh PNG {size.width}×{size.height}</button>
        <button type="button" className="ghost-button" onClick={() => saveBlob(new Blob([prepared.svg], { type: 'image/svg+xml;charset=utf-8' }), `${baseName}.svg`)}>Unduh SVG</button>
        {size.capped && <span className="muted" role="status">Skala dibatasi ke {size.scale.toFixed(2)}× (maks 8192 px / 33,5 MP).</span>}
        {error && <span className="error" role="alert">{error}</span>}
      </div>
      <div className="canvas-diagram-stage">
        <img className="canvas-diagram-img" src={svgDataUrl(prepared.svg)} alt="Gambar vektor SVG" width={prepared.width * zoom} height={prepared.height * zoom} />
      </div>
    </div>
  )
}
