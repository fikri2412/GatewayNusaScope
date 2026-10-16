import { pngDimensions } from './svg'

type Sized = { svg: string; width: number; height: number }

/**
 * SVG -> PNG di browser (kanvas putih), tanpa layanan render eksternal. Karena sumbernya vektor, skala besar tidak
 * kehilangan detail; skala diturunkan otomatis bila melewati batas sisi/piksel (lihat `pngDimensions`).
 */
export async function svgToPng(rendered: Sized, scale = 2): Promise<{ blob: Blob; width: number; height: number; capped: boolean }> {
  const size = pngDimensions(rendered.width, rendered.height, scale)
  const image = new Image()
  image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(rendered.svg)}`
  await image.decode()
  const canvas = document.createElement('canvas')
  canvas.width = size.width
  canvas.height = size.height
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('Canvas 2D tidak tersedia di browser ini.')
  ctx.fillStyle = '#ffffff'
  ctx.fillRect(0, 0, canvas.width, canvas.height)
  ctx.drawImage(image, 0, 0, canvas.width, canvas.height)
  const blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('Gagal membuat PNG (gambar terlalu besar untuk browser ini).'))), 'image/png'))
  return { blob, width: size.width, height: size.height, capped: size.capped }
}

export function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  document.body.append(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}
