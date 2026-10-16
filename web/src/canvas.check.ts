import type { ArtifactSummary, MessageDto } from './api/types.ts'
import { MAX_DIFF_LINES, artifactFileName, artifactsForMessage, changedLines, mermaidType, normalizeMermaid, sizeSvg, SANDBOX_CSP, SANDBOX_FLAGS, buildSandboxDoc, SELECTION_ACTIONS, buildSelectionPrompt } from './canvas.ts'

// Jalankan: npm run check:canvas. Node menjalankan .ts ini langsung; tanpa framework/dependency.

const deep = (actual: unknown, expected: unknown, label: string) => {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(`${label}: ${JSON.stringify(actual)} !== ${JSON.stringify(expected)}`)
}

const ok = (condition: boolean, label: string) => {
  if (!condition) throw new Error(label)
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

// Diagram: ekstensi, pagar kode AI, ukuran SVG.
deep(artifactFileName('Alur persetujuan', 'diagram', null), 'Alur-persetujuan.mmd', 'diagram .mmd')
deep(normalizeMermaid('flowchart TD\n A-->B'), 'flowchart TD\n A-->B', 'tanpa pagar tidak berubah')
deep(normalizeMermaid('```mermaid\nflowchart TD\n A-->B\n```'), 'flowchart TD\n A-->B', 'pagar mermaid dibuang')
deep(normalizeMermaid('```\nerDiagram\n A ||--o{ B : has\n```\n'), 'erDiagram\n A ||--o{ B : has', 'pagar tanpa bahasa dibuang')
const sized = sizeSvg('<svg id="d1" width="100%" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 312.5 180.2" style="max-width: 312.5px;"><g/></svg>')
deep(sized && [sized.width, sized.height], [313, 181], 'ukuran dari viewBox dibulatkan ke atas')
ok(sized !== null && !sized.svg.includes('width="100%"') && sized.svg.includes('width="313" height="181"') && sized.svg.endsWith('<g/></svg>'), 'width 100% diganti ukuran piksel, isi utuh')
deep(sizeSvg('<svg viewBox="0 0 0 0"></svg>'), null, 'viewBox nol ditolak')
deep(sizeSvg('<svg></svg>'), null, 'tanpa viewBox ditolak')
deep(sizeSvg('bukan svg'), null, 'bukan svg ditolak')

// Sandbox pratinjau.
ok(buildSandboxDoc('<h1>x</h1>').startsWith('<meta http-equiv="Content-Security-Policy"'), 'CSP paling awal sebelum sumber')
ok(buildSandboxDoc('<h1>x</h1>').endsWith('<h1>x</h1>'), 'sumber utuh setelah CSP')
ok(SANDBOX_CSP.includes("default-src 'none'") && !/connect-src|https?:/.test(SANDBOX_CSP), 'CSP menutup jaringan (default none, tanpa connect-src/host)')
deep(SANDBOX_FLAGS, 'allow-scripts', 'sandbox hanya skrip: tanpa same-origin/popup/form/top-navigation')

// Toolbar teks terpilih.
const perpendek = SELECTION_ACTIONS.dokumen![0]
const tanya = SELECTION_ACTIONS.dokumen![2]
const ask = buildSelectionPrompt({ action: perpendek, title: 'Laporan', artifactId: 'abc-1', version: 3, text: 'kalimat panjang sekali' })
ok(ask.includes('artifact_id=abc-1') && ask.includes('versi=3') && ask.includes('kalimat panjang sekali'), 'prompt memuat id, versi, dan teks terpilih')
ok(ask.includes('baca_artifact') && ask.includes('perbarui_artifact') && ask.includes('versi_dasar'), 'aksi yang mengubah memandu baca lalu simpan dengan versi_dasar')
const q = buildSelectionPrompt({ action: tanya, title: 'L', artifactId: 'x', version: 1, text: 'abc', question: ' apa maksudnya? ' })
ok(q.includes('apa maksudnya?') && q.includes('jangan mengubah artifact') && !q.includes('perbarui_artifact'), 'Tanya hanya menjawab, tidak mengubah, dan memuat pertanyaan')
deep(SELECTION_ACTIONS.kode!.map((a) => a.key), ['jelaskan', 'perbaiki', 'tanya'], 'aksi kode')
deep(SELECTION_ACTIONS.diagram, undefined, 'diagram tidak punya toolbar teks')
deep(SELECTION_ACTIONS.dokumen!.filter((a) => a.edits).map((a) => a.key), ['perpendek', 'formal'], 'hanya Perpendek/Formal yang mengubah dokumen')

deep(mermaidType('erDiagram\n A ||--o{ B : has'), 'erDiagram', 'jenis ERD')
deep(mermaidType('%% komentar\n\nflowchart TD\n A-->B'), 'flowchart', 'komentar dan baris kosong dilewati')
deep(mermaidType('---\ntitle: x\n---\nclassDiagram\n A <|-- B'), 'classDiagram', 'front matter dilewati')
deep(mermaidType('```mermaid\nsequenceDiagram\n A->>B: hi\n```'), 'sequenceDiagram', 'pagar kode dilewati')
deep(mermaidType(''), 'mermaid', 'kosong jatuh ke mermaid')

console.log('canvas.check: semua pemeriksaan lulus')
