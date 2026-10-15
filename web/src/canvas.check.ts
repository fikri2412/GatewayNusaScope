import type { ArtifactSummary, MessageDto } from './api/types.ts'
import { MAX_DIFF_LINES, artifactFileName, artifactsForMessage, changedLines } from './canvas.ts'

// Jalankan: npm run check:canvas. Node menjalankan .ts ini langsung; tanpa framework/dependency.

const deep = (actual: unknown, expected: unknown, label: string) => {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(`${label}: ${JSON.stringify(actual)} !== ${JSON.stringify(expected)}`)
}

const lines = (prev: string, next: string) => {
  const result = changedLines(prev, next)
  return result === null ? null : [...result].sort((x, y) => x - y)
}

// Diff: hanya baris yang benar-benar baru/berubah yang disorot.
deep(lines('a\nb\nc', 'a\nb\nc'), [], 'identik tidak menyorot apa pun')
deep(lines('a\nb\nc', 'a\nX\nc'), [2], 'baris diganti')
deep(lines('a\nc', 'a\nb\nc'), [2], 'baris disisipkan')
deep(lines('a\nb\nc', 'a\nc'), [], 'baris dihapus tidak menyorot baris tersisa')
deep(lines('', 'a\nb'), [1, 2], 'dari kosong, semua baris baru')
deep(lines('a\nb', ''), [], 'dikosongkan: tidak ada baris untuk disorot')
deep(lines('x\ny\nz', 'p\nq'), [1, 2], 'diganti seluruhnya')
deep(lines('a\nb\na\nb', 'a\nb\nc\na\nb'), [3], 'pengulangan: hanya sisipan yang disorot')

// Batas: terlalu besar → null (tidak memalsukan sorotan).
deep(changedLines('x', Array.from({ length: MAX_DIFF_LINES + 1 }, (_, i) => String(i)).join('\n')), null, 'melebihi batas diff')
deep(changedLines(Array.from({ length: MAX_DIFF_LINES }, (_, i) => String(i)).join('\n'), 'baru') !== null, true, 'tepat di batas masih dihitung')

// Nama berkas.
deep(artifactFileName('Laporan Q3: Anggaran/Proyeksi', 'dokumen', null), 'Laporan-Q3-Anggaran-Proyeksi.md', 'judul dibersihkan, dokumen .md')
deep(artifactFileName('Skrip impor', 'kode', 'python'), 'Skrip-impor.py', 'kode mengikuti bahasa')
deep(artifactFileName('x', 'kode', 'csharp'), 'x.cs', 'csharp → .cs')
deep(artifactFileName('x', 'kode', 'tidak-dikenal'), 'x.txt', 'bahasa asing → .txt')
deep(artifactFileName('../../etc/passwd', 'dokumen', null), 'etc-passwd.md', 'path traversal tidak lolos')
deep(artifactFileName('///', 'dokumen', null), 'artifact.md', 'judul tanpa karakter valid → fallback')
deep(artifactFileName('a'.repeat(200), 'dokumen', null).length, 63, 'nama dipotong 60 + ekstensi')

// Kartu referensi: hanya jawaban utama yang cocok dengan pesan pemicu.
const message = (over: Partial<MessageDto>): MessageDto => ({
  id: 10, role: 'assistant', content: 'ok', createdAt: '2026-01-01T00:00:00Z', attachments: [], finishReason: null,
  modelAlias: null, streamId: null, queueItemId: 1, sourceMessageId: 5, isPrimary: true, tokensUsed: null,
  queueWaitMs: null, firstTokenMs: null, totalMs: null, ...over,
})
const artifact = (id: string, sourceMessageId: number | null): ArtifactSummary => ({
  id, kind: 'dokumen', title: id, language: null, currentVersion: 1, sourceMessageId, createdAt: '', updatedAt: '',
})
const all = [artifact('a', 5), artifact('b', 6), artifact('c', null)]
deep(artifactsForMessage(message({}), all).map((a) => a.id), ['a'], 'cocok lewat pesan pemicu')
deep(artifactsForMessage(message({ isPrimary: false }), all), [], 'alternatif tanpa kartu')
deep(artifactsForMessage(message({ role: 'user', sourceMessageId: null }), all), [], 'pesan user tanpa kartu')
deep(artifactsForMessage(message({ sourceMessageId: null }), all), [], 'tanpa sumber tanpa kartu')

console.log('canvas.check: semua pemeriksaan lulus')
