import { svgDimensions } from './svg'
import { sanitizeSvg } from './svgSanitize'

export function svgDataUrl(svg: string) {
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`
}

/** SVG tersanitasi + ukuran, atau alasan gagal (SVG rusak/tanpa ukuran); dipakai tampilan utama dan thumbnail galeri. */
export function prepareSvg(source: string): { svg: string; width: number; height: number } | { error: string } {
  try {
    const sized = svgDimensions(sanitizeSvg(source))
    return sized ?? { error: 'SVG tidak punya viewBox atau width/height, jadi ukurannya tidak diketahui.' }
  } catch (e: unknown) {
    return { error: e instanceof Error ? e.message : String(e) }
  }
}

