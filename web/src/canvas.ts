import type { ArtifactKind, ArtifactSummary, MessageDto } from './api/types'

/** Di atas ini diff baris tidak dihitung (memori O(n·m)); sorotan perubahan dimatikan, bukan dipalsukan. */
export const MAX_DIFF_LINES = 2000

/**
 * Nomor baris (mulai 1) di `next` yang baru atau berubah dibanding `prev`, lewat LCS per baris.
 * Mengembalikan null bila salah satu sisi melebihi {@link MAX_DIFF_LINES}.
 */
export function changedLines(prev: string, next: string): Set<number> | null {
  // Teks kosong = nol baris (bukan satu baris kosong), supaya dokumen yang baru dikosongkan tidak menyorot apa pun.
  const a = prev === '' ? [] : prev.split('\n')
  const b = next === '' ? [] : next.split('\n')
  if (a.length > MAX_DIFF_LINES || b.length > MAX_DIFF_LINES) return null
  const n = a.length
  const m = b.length
  const width = m + 1
  const lcs = new Uint16Array((n + 1) * width)
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lcs[i * width + j] = a[i] === b[j] ? lcs[(i + 1) * width + j + 1] + 1 : Math.max(lcs[(i + 1) * width + j], lcs[i * width + j + 1])
    }
  }
  const changed = new Set<number>()
  let i = 0
  let j = 0
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      i++
      j++
    } else if (lcs[(i + 1) * width + j] >= lcs[i * width + j + 1]) {
      i++ // baris lama dihapus
    } else {
      changed.add(j + 1) // baris baru
      j++
    }
  }
  while (j < m) changed.add(++j)
  return changed
}

const EXTENSIONS: Record<string, string> = {
  csharp: 'cs', python: 'py', javascript: 'js', typescript: 'ts', tsx: 'tsx', jsx: 'jsx', json: 'json', sql: 'sql',
  html: 'html', css: 'css', bash: 'sh', shell: 'sh', powershell: 'ps1', java: 'java', go: 'go', rust: 'rs',
  yaml: 'yml', xml: 'xml', markdown: 'md', text: 'txt',
}

/** Bahasa yang ditawarkan di pembuat artifact kode; nilainya lolos validasi server. */
export const CODE_LANGUAGES = ['csharp', 'python', 'javascript', 'typescript', 'sql', 'json', 'html', 'css', 'bash', 'text'] as const

/** Nama berkas unduhan: judul dibersihkan, ekstensi mengikuti jenis/bahasa. Tidak pernah kosong atau berisi path. */
export function artifactFileName(title: string, kind: ArtifactKind, language: string | null): string {
  const base = title
    .normalize('NFKC')
    .replace(/[^\p{L}\p{N}._-]+/gu, '-')
    .replace(/^[-.]+|[-.]+$/g, '')
    .slice(0, 60)
    .replace(/-+$/, '')
  const ext = kind === 'dokumen' ? 'md' : kind === 'diagram' ? 'mmd' : (EXTENSIONS[language ?? ''] ?? 'txt')
  return `${base || 'artifact'}.${ext}`
}

/**
 * Artifact buatan AI untuk jawaban ini: cocok lewat pesan user pemicu. Hanya jawaban utama yang menampilkan kartu
 * (jawaban alternatif tidak pernah membuat artifact), dan pesan user tidak pernah.
 */
export function artifactsForMessage(message: MessageDto, artifacts: ArtifactSummary[]): ArtifactSummary[] {
  if (message.role !== 'assistant' || !message.isPrimary || message.sourceMessageId === null) return []
  return artifacts.filter((a) => a.sourceMessageId === message.sourceMessageId)
}

/** Label sumber versi untuk riwayat. */
export const sourceLabel = (source: string) =>
  source === 'ai' ? 'AI' : source === 'restore' ? 'Dipulihkan' : 'Anda'

/** Label pendek jenis artifact untuk tab dan kartu. */
export const kindLabel = (kind: ArtifactKind) => (kind === 'dokumen' ? 'Dok' : kind === 'diagram' ? 'Diagram' : 'Kode')

/** Isi awal diagram baru; sintaks valid supaya hasilnya langsung terlihat. */
export const DIAGRAM_STARTER = 'flowchart TD\n  A[Mulai] --> B{Data lengkap?}\n  B -->|Ya| C[Selesai]\n  B -->|Tidak| A'

/** AI kadang membungkus Mermaid dengan pagar kode; renderer hanya butuh sintaksnya. */
export function normalizeMermaid(source: string): string {
  const text = source.trim()
  const fenced = /^```[\w-]*[ \t]*\r?\n([\s\S]*?)\r?\n?```$/.exec(text)
  return (fenced ? fenced[1] : text).trim()
}

/**
 * Beri `<svg>` hasil Mermaid ukuran piksel eksplisit dari viewBox (Mermaid memakai width="100%"), supaya <img> dan
 * ekspor PNG punya dimensi nyata. null bila viewBox tidak ada/tidak masuk akal.
 */
export function sizeSvg(svg: string): { svg: string; width: number; height: number } | null {
  const open = /<svg\b[^>]*>/.exec(svg)
  if (!open) return null
  const box = /viewBox="\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)\s*"/.exec(open[0])
  if (!box) return null
  const width = Math.ceil(Number(box[1]))
  const height = Math.ceil(Number(box[2]))
  if (!(width > 0 && height > 0)) return null
  const tag = open[0].replace(/\s(?:width|height)="[^"]*"/g, '').replace('<svg', `<svg width="${width}" height="${height}"`)
  return { svg: svg.replace(open[0], tag), width, height }
}

/**
 * CSP dokumen pratinjau: tanpa jaringan apa pun (connect/img/font/media hanya data:/blob:), tanpa form, tanpa base.
 * Skrip dan gaya inline tetap boleh supaya halaman HTML sederhana berfungsi.
 */
export const SANDBOX_CSP =
  "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; media-src data:; form-action 'none'; base-uri 'none'"

/** Atribut `sandbox` iframe pratinjau: skrip boleh, tetapi TANPA allow-same-origin (origin buram), popup, form, atau navigasi top. */
export const SANDBOX_FLAGS = 'allow-scripts'

/** Dokumen `srcdoc` pratinjau: meta CSP ditempatkan paling awal, sebelum sumber dari AI/user. */
export const buildSandboxDoc = (source: string) => `<meta http-equiv="Content-Security-Policy" content="${SANDBOX_CSP}">${source}`
