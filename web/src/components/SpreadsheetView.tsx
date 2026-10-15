import { lazy, Suspense } from 'react'

// Fortune-sheet besar (±beberapa MB): hanya diunduh saat artifact spreadsheet pertama dibuka.
const SpreadsheetEditor = lazy(() => import('./SpreadsheetEditor'))

export function SpreadsheetView(props: { value: string; readOnly: boolean; title: string; onChange?: (next: string) => void }) {
  return (
    <Suspense fallback={<p className="muted canvas-empty" role="status">Memuat spreadsheet…</p>}>
      <SpreadsheetEditor {...props} />
    </Suspense>
  )
}
