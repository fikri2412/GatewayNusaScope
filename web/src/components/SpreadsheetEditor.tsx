import { Workbook } from '@fortune-sheet/react'
import type { WorkbookInstance } from '@fortune-sheet/react'
import type { Sheet } from '@fortune-sheet/core'
import '@fortune-sheet/react/dist/index.css'
import { useEffect, useRef, useState } from 'react'
import { artifactFileName } from '../canvas'
import { STARTER_SHEETS, parseSheets, serializeSheets, sheetToCsv } from '../spreadsheet'

/**
 * Editor spreadsheet (Fortune-sheet, MIT). Dimuat malas lewat `SpreadsheetView`, jadi bundle utama tidak ikut membesar.
 * `value` hanya dibaca saat dipasang; pemanggil me-remount (key) untuk memuat isi lain. Perubahan dilaporkan lewat
 * `onChange` sebagai teks kanonik, dan hanya bila isinya benar-benar berbeda (pindah sel/sheet tidak dihitung).
 */
export default function SpreadsheetEditor({ value, readOnly, title, onChange }: {
  value: string
  readOnly: boolean
  title: string
  onChange?: (next: string) => void
}) {
  const ref = useRef<WorkbookInstance | null>(null)
  // Isi rusak (bukan array sheet) jatuh ke contoh awal; tidak menimpa apa pun sampai pengguna mengedit.
  const [initial] = useState(() => parseSheets(value) ?? STARTER_SHEETS)
  const last = useRef<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const canonical = (sheets: Sheet[]) => {
    const api = ref.current
    return api ? serializeSheets(sheets, (data) => api.dataToCelldata(data)) : null
  }

  // Patokan "tidak berubah" = bentuk kanonik sheet setelah editor selesai menormalkannya sendiri.
  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      last.current = ref.current ? canonical(ref.current.getAllSheets()) : null
    })
    return () => cancelAnimationFrame(frame)
  }, [])

  // `onChange` membawa data yang sudah diterapkan; `onOp` terpanggil sebelum state berubah (snapshot-nya basi).
  const report = (sheets: Sheet[]) => {
    if (readOnly || !onChange) return
    const next = canonical(sheets)
    if (next === null || next === last.current) return
    last.current = next
    onChange(next)
  }

  const downloadCsv = () => {
    const api = ref.current
    if (!api) return
    setError(null)
    try {
      const sheet = api.getSheet()
      const csv = sheetToCsv(sheet.celldata)
      const url = URL.createObjectURL(new Blob(['﻿', csv], { type: 'text/csv;charset=utf-8' }))
      const link = document.createElement('a')
      link.href = url
      link.download = artifactFileName(`${title}-${sheet.name}`, 'spreadsheet', null)
      document.body.append(link)
      link.click()
      link.remove()
      URL.revokeObjectURL(url)
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <div className="canvas-sheet">
      <div className="canvas-diagram-bar">
        <button type="button" className="ghost-button" onClick={downloadCsv}>Unduh CSV (sheet aktif)</button>
        {readOnly && <span className="muted">Hanya baca.</span>}
        {error && <span className="error" role="alert">{error}</span>}
      </div>
      <div className="canvas-sheet-grid">
        <Workbook
          ref={ref}
          data={initial}
          lang="en"
          allowEdit={!readOnly}
          showToolbar={!readOnly}
          showFormulaBar
          showSheetTabs
          onChange={report}
        />
      </div>
    </div>
  )
}
