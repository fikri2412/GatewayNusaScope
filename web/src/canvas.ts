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
  const ext = kind === 'dokumen' ? 'md' : kind === 'diagram' ? 'mmd' : kind === 'spreadsheet' ? 'csv' : kind === 'gambar' ? 'svg' : (EXTENSIONS[language ?? ''] ?? 'txt')
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
export const kindLabel = (kind: ArtifactKind) =>
  kind === 'dokumen' ? 'Dok' : kind === 'diagram' ? 'Diagram' : kind === 'spreadsheet' ? 'Sheet' : kind === 'gambar' ? 'Gambar' : 'Kode'

/** Isi awal diagram baru; sintaks valid supaya hasilnya langsung terlihat. */
export const DIAGRAM_STARTER = 'flowchart TD\n  A[Mulai] --> B{Data lengkap?}\n  B -->|Ya| C[Selesai]\n  B -->|Tidak| A'

/** Jenis diagram dari baris pertama sintaks Mermaid (`erDiagram`, `flowchart`, ...), untuk keterangan di bar tampilan. */
export function mermaidType(source: string): string {
  let inFrontMatter = false
  for (const raw of source.split('\n')) {
    const line = raw.trim()
    if (line === '---') {
      inFrontMatter = !inFrontMatter
      continue
    }
    if (inFrontMatter || line === '' || line.startsWith('%%') || line.startsWith('```')) continue
    return /^[A-Za-z][\w-]*/.exec(line)?.[0] ?? 'mermaid'
  }
  return 'mermaid'
}

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

/** Batas teks terpilih yang dikirim ke AI; pesan chat dibatasi 8.000 karakter, sisanya untuk instruksi. */
export const MAX_SELECTION_CHARS = 3000

export type SelectionAction = { key: string; label: string; instruction: string; edits: boolean; needsQuestion?: boolean }

/** Aksi toolbar teks terpilih per jenis artifact. `edits` = AI diminta menyimpan versi baru; selain itu hanya menjawab di chat. */
export const SELECTION_ACTIONS: Partial<Record<ArtifactKind, SelectionAction[]>> = {
  dokumen: [
    { key: 'perpendek', label: 'Perpendek', instruction: 'Perpendek bagian terpilih dengan makna tetap.', edits: true },
    { key: 'formal', label: 'Lebih formal', instruction: 'Ubah bagian terpilih menjadi lebih formal tanpa mengubah maknanya.', edits: true },
    { key: 'tanya', label: 'Tanya…', instruction: 'Jawab pertanyaan berikut tentang bagian terpilih', edits: false, needsQuestion: true },
  ],
  kode: [
    { key: 'jelaskan', label: 'Jelaskan', instruction: 'Jelaskan apa yang dilakukan bagian terpilih.', edits: false },
    { key: 'perbaiki', label: 'Perbaiki', instruction: 'Perbaiki bug atau masalah pada bagian terpilih tanpa mengubah perilaku lain.', edits: true },
    { key: 'tanya', label: 'Tanya…', instruction: 'Jawab pertanyaan berikut tentang bagian terpilih', edits: false, needsQuestion: true },
  ],
}

/**
 * Pesan chat untuk satu aksi pada teks terpilih. Teks terpilih dan artifact adalah data pengguna; AI dibimbing memakai
 * baca_artifact + perbarui_artifact(versi_dasar) sehingga edit pengguna di antaranya tidak tertimpa (konflik versi).
 * `question` hanya dipakai aksi `needsQuestion`.
 */
export function buildSelectionPrompt(input: {
  action: SelectionAction
  title: string
  artifactId: string
  version: number
  text: string
  question?: string
}): string {
  const { action, title, artifactId, version, text } = input
  const task = action.needsQuestion ? `${action.instruction}: ${(input.question ?? '').trim()}` : action.instruction
  const how = action.edits
    ? 'Baca dulu isi terbaru dengan baca_artifact, ubah hanya bagian terpilih, lalu simpan lewat perbarui_artifact dengan versi_dasar sesuai versi yang kamu baca. Jangan menyalin seluruh isi ke chat; cukup rangkum perubahan dalam 1-2 kalimat.'
    : 'Jawab di chat dan jangan mengubah artifact.'
  return `[Canvas] Artifact "${title}" (artifact_id=${artifactId}, versi=${version}). Bagian terpilih:\n"""\n${text}\n"""\nTugas: ${task}\n${how}`
}
