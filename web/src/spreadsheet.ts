import type { CellWithRowAndCol, Sheet } from '@fortune-sheet/core'

/**
 * Isi artifact spreadsheet = JSON array sheet milik Fortune-sheet, tetapi hanya bagian yang bermakna disimpan
 * (nama, urutan, sel, konfigurasi kolom/baris, beku, tautan, validasi). Status tampilan seperti seleksi atau sheet
 * aktif sengaja dibuang supaya berpindah sel/sheet tidak membuat versi baru. Modul ini murni (tanpa React/DOM)
 * agar bisa diperiksa lewat `npm run check:spreadsheet`.
 */
const KEEP = ['name', 'id', 'order', 'color', 'config', 'celldata', 'frozen', 'row', 'column', 'hide', 'hyperlink', 'dataVerification'] as const

type Cell = NonNullable<CellWithRowAndCol['v']>

const cell = (r: number, c: number, v: string | number, f?: string): CellWithRowAndCol => ({
  r,
  c,
  v: (f ? { v, m: String(v), f } : { v, m: String(v) }) as Cell,
})

/** Isi awal spreadsheet baru: contoh kecil dengan rumus supaya langsung terlihat hidup. */
export const STARTER_SHEETS: Sheet[] = [
  {
    name: 'Sheet1',
    id: 'sheet-1',
    order: 0,
    celldata: [
      cell(0, 0, 'Item'), cell(0, 1, 'Jumlah'), cell(0, 2, 'Harga'), cell(0, 3, 'Total'),
      cell(1, 0, 'Pensil'), cell(1, 1, 10), cell(1, 2, 2000), cell(1, 3, 20000, '=B2*C2'),
      cell(2, 0, 'Buku'), cell(2, 1, 5), cell(2, 2, 15000), cell(2, 3, 75000, '=B3*C3'),
      cell(3, 0, 'Jumlah'), cell(3, 3, 95000, '=SUM(D2:D3)'),
    ],
  },
]

export const STARTER_CONTENT = JSON.stringify(STARTER_SHEETS)

/** Array sheet bernama dari teks; null bila bukan JSON array objek bernama (isi rusak tidak boleh meruntuhkan editor). */
export function parseSheets(content: string): Sheet[] | null {
  try {
    const value: unknown = JSON.parse(content)
    if (!Array.isArray(value) || value.length === 0) return null
    if (!value.every((s) => s && typeof s === 'object' && typeof (s as { name?: unknown }).name === 'string')) return null
    return value as Sheet[]
  } catch {
    return null
  }
}

/** Sheet → teks kanonik. `toCelldata` mengubah matriks `data` milik editor menjadi daftar sel padat (tanpa sel kosong). */
export function serializeSheets(sheets: Sheet[], toCelldata: (data: NonNullable<Sheet['data']>) => CellWithRowAndCol[]): string {
  return JSON.stringify(
    sheets.map((sheet) => {
      const out: Record<string, unknown> = {}
      const celldata = sheet.data ? toCelldata(sheet.data) : (sheet.celldata ?? [])
      for (const key of KEEP) {
        const value = key === 'celldata' ? celldata : sheet[key]
        if (value !== undefined && value !== null) out[key] = value
      }
      return out
    }),
  )
}

/** Sel CSV aman: kutip bila perlu, dan awalan rumus (= + @ tab CR, atau '-' non-angka) dinetralkan dengan tanda petik. */
export function csvCell(value: string | number | boolean | null | undefined): string {
  let text = value === null || value === undefined ? '' : String(value)
  if (typeof value === 'string' && /^(?:[=+@\t\r]|-(?!\d|\.\d))/.test(text)) text = `'${text}`
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

/** CSV satu sheet dari celldata: nilai tampilan (`m`) bila ada, jika tidak nilai mentah (`v`). */
export function sheetToCsv(celldata: CellWithRowAndCol[]): string {
  if (celldata.length === 0) return ''
  const rows = Math.max(...celldata.map((c) => c.r)) + 1
  const cols = Math.max(...celldata.map((c) => c.c)) + 1
  const grid: string[][] = Array.from({ length: rows }, () => Array.from({ length: cols }, () => ''))
  for (const { r, c, v } of celldata) {
    const shown = v?.m ?? v?.v
    grid[r][c] = csvCell(typeof shown === 'object' ? '' : (shown as string | number | boolean | null | undefined))
  }
  return grid.map((row) => row.join(',')).join('\r\n')
}
