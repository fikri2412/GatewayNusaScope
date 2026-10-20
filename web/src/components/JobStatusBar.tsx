import type { JobDto } from '../api/types'

const LABELS: Record<JobDto['jobType'], string> = {
  ExcelRead: 'Membaca file…',
  Analyze: 'Menganalisis…',
  Pdf: 'Membuat PDF…',
  DocumentRead: 'Membaca dokumen…',
  DocumentAnalyze: 'Menganalisis dokumen…',
  DiagramPlan: 'Menyusun alur logika…',
  MermaidRender: 'Menggambar diagram…',
}

/** Chip untuk job yang belum selesai; job Done/Failed hilang (hasilnya datang sebagai pesan operator). */
export function JobStatusBar({ jobs }: { jobs: JobDto[] }) {
  const active = jobs.filter((j) => j.status === 'Pending' || j.status === 'Processing')
  if (active.length === 0) return null
  return (
    <div className="job-bar" role="status">
      {active.map((j) => (
        <span key={j.id} className={`job-chip job-${j.status.toLowerCase()}`}>
          {LABELS[j.jobType]}
        </span>
      ))}
    </div>
  )
}
