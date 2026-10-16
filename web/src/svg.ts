import { sizeSvg } from './canvas.ts'

/**
 * Gambar vektor (artifact `gambar` = dokumen SVG tulisan AI). Modul ini murni (tanpa DOM) supaya bisa diperiksa lewat
 * `npm run check:svg`; sanitasi berbasis DOM ada di `svgSanitize.ts`.
 */

/** Sisi terpanjang dan jumlah piksel maksimum PNG hasil ekspor; melindungi memori browser. */
export const MAX_PNG_SIDE = 8192
export const MAX_PNG_PIXELS = 33_554_432

export const UPSCALE_FACTORS = [1, 2, 4, 8] as const

/** Ukuran PNG untuk faktor skala yang diminta; faktor diturunkan bila melampaui batas (`capped` = true). */
export function pngDimensions(width: number, height: number, scale: number): { scale: number; width: number; height: number; capped: boolean } {
  let factor = scale
  const sideLimit = MAX_PNG_SIDE / Math.max(width, height)
  const pixelLimit = Math.sqrt(MAX_PNG_PIXELS / (width * height))
  const limit = Math.min(sideLimit, pixelLimit)
  const capped = factor > limit
  if (capped) factor = limit
  return { scale: factor, width: Math.max(1, Math.floor(width * factor)), height: Math.max(1, Math.floor(height * factor)), capped }
}

/** Ukuran dasar SVG (piksel): dari viewBox, atau dari atribut width/height numerik. null bila tidak diketahui. */
export function svgDimensions(svg: string): { svg: string; width: number; height: number } | null {
  const fromBox = sizeSvg(svg)
  if (fromBox) return fromBox
  const open = /<svg\b[^>]*>/.exec(svg)
  if (!open) return null
  const w = /\swidth="\s*([\d.]+)(?:px)?\s*"/.exec(open[0])
  const h = /\sheight="\s*([\d.]+)(?:px)?\s*"/.exec(open[0])
  const width = w ? Math.ceil(Number(w[1])) : 0
  const height = h ? Math.ceil(Number(h[1])) : 0
  if (!(width > 0 && height > 0)) return null
  // Tanpa viewBox, tambahkan agar penskalaan ekspor tidak memotong gambar.
  return { svg: svg.replace(open[0], open[0].replace('<svg', `<svg viewBox="0 0 ${width} ${height}"`)), width, height }
}

export const RATIOS = { '1:1': [1024, 1024], '4:3': [1024, 768], '3:4': [768, 1024], '16:9': [1280, 720], '9:16': [720, 1280] } as const
export type Ratio = keyof typeof RATIOS

export const STYLES = { datar: 'ilustrasi datar (flat) dengan bidang warna solid', garis: 'gambar garis (line art) satu warna', isometrik: 'ilustrasi isometrik sederhana', ikon: 'ikon bergaya minimalis dengan palet terbatas' } as const
export type Style = keyof typeof STYLES

export const STRENGTHS = { halus: 'perubahan halus: pertahankan hampir semuanya, ubah detail kecil', sedang: 'perubahan sedang: ubah beberapa elemen dan warna', kuat: 'perubahan kuat: tafsir ulang dengan bentuk dan warna yang jelas berbeda' } as const
export type Strength = keyof typeof STRENGTHS

export const MAX_VARIANTS = 4

const clampCount = (n: number) => Math.min(MAX_VARIANTS, Math.max(1, Math.floor(n) || 1))

/** Pesan chat untuk membuat gambar baru. Selalu menyebut batasnya: vektor SVG, bukan foto. */
export function buildGeneratePrompt(input: { prompt: string; style: Style; ratio: Ratio; count: number }): string {
  const [w, h] = RATIOS[input.ratio]
  const count = clampCount(input.count)
  return `[Canvas] Buat ${count} gambar vektor SVG (bukan foto atau gambar raster) dengan buat_artifact jenis gambar, satu panggilan per gambar.\n`
    + `Deskripsi: ${input.prompt.trim()}\n`
    + `Gaya: ${STYLES[input.style]}.\n`
    + `Rasio ${input.ratio}: pakai viewBox="0 0 ${w} ${h}" pada elemen <svg>.\n`
    + `Aturan SVG: dokumen lengkap dengan xmlns, tanpa script/foreignObject/atribut on*/tautan eksternal.\n`
    + (count > 1 ? 'Tiap gambar harus berbeda secara komposisi atau palet warna. ' : '')
    + 'Beri judul singkat tiap gambar dan rangkum hasilnya dalam 1-2 kalimat di chat.'
}

/** Pesan chat untuk membuat variasi dari satu gambar; setiap hasil mencatat asal-usulnya lewat turunan_dari. */
export function buildVariationPrompt(input: {
  title: string; artifactId: string; version: number; count: number; strength: Strength; keepComposition: boolean; keepColors: boolean
}): string {
  const count = clampCount(input.count)
  const keep = [input.keepComposition ? 'komposisi dan tata letak' : null, input.keepColors ? 'palet warna' : null].filter((x) => x !== null)
  return `[Canvas] Buat ${count} variasi dari gambar "${input.title}" (artifact_id=${input.artifactId}, versi=${input.version}).\n`
    + 'Baca dulu gambar sumber dengan baca_artifact. Untuk setiap variasi panggil buat_artifact jenis gambar dengan '
    + `turunan_dari=${input.artifactId} dan versi_sumber=${input.version}. Sumber tidak boleh diubah.\n`
    + `Kekuatan: ${STRENGTHS[input.strength]}.\n`
    + (keep.length > 0 ? `Pertahankan: ${keep.join(' dan ')}.\n` : 'Tidak ada elemen yang wajib dipertahankan.\n')
    + (count > 1 ? 'Tiap variasi harus berbeda satu sama lain. ' : '')
    + 'Gunakan viewBox yang sama dengan sumber. Rangkum hasilnya dalam 1-2 kalimat di chat.'
}

/** Kelompok "batch" riwayat gambar: gambar yang dibuat oleh pesan user pemicu yang sama; urut terbaru dulu. */
export function groupBatches<T extends { sourceMessageId: number | null; createdAt: string }>(items: T[]): { key: string; items: T[] }[] {
  const groups = new Map<string, T[]>()
  for (const item of items) {
    const key = item.sourceMessageId === null ? 'manual' : `pesan-${item.sourceMessageId}`
    groups.set(key, [...(groups.get(key) ?? []), item])
  }
  return [...groups.entries()]
    .map(([key, list]) => ({ key, items: [...list].sort((a, b) => a.createdAt.localeCompare(b.createdAt)) }))
    .sort((a, b) => b.items[0].createdAt.localeCompare(a.items[0].createdAt))
}
