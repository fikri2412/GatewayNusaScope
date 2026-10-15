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
  const ext = kind === 'dokumen' ? 'md' : (EXTENSIONS[language ?? ''] ?? 'txt')
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
