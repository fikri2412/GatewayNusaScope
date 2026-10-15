import type { StreamEvent } from './api/types'
import { applyStreamEvent, clearTerminalDrafts, closeCommittedStreams, markSyncing } from './streamDrafts.ts'

// Jalankan: npm run check:drafts. Node menjalankan .ts ini langsung; tanpa framework/dependency.
// Perbandingan deterministik: nilai dibandingkan setelah JSON.stringify, referensi dengan ===.

const deep = (actual: unknown, expected: unknown, label: string) => {
  const a = JSON.stringify(actual)
  const e = JSON.stringify(expected)
  if (a !== e) throw new Error(`${label}: ${a} ≠ ${e}`)
}

const refSame = (actual: unknown, expected: unknown, label: string) => {
  if (actual !== expected) throw new Error(`${label}: reducer membuat salinan padahal tidak ada perubahan`)
}

const started = (streamId: string, runId = 'run-1', round = 0, queueItemId = 7): StreamEvent => ({
  conversationId: 'c1',
  queueItemId,
  runId,
  streamId,
  round,
  kind: 'started',
  sequence: 0,
  offset: 0,
  text: null,
})

const delta = (
  streamId: string,
  sequence: number,
  offset: number,
  text: string,
  runId = 'run-1',
  round = 0,
): StreamEvent => ({ conversationId: 'c1', queueItemId: 7, runId, streamId, round, kind: 'delta', sequence, offset, text })

const aborted = (streamId: string, sequence: number, offset: number): StreamEvent => ({
  conversationId: 'c1',
  queueItemId: 7,
  runId: 'run-1',
  streamId,
  round: 0,
  kind: 'aborted',
  sequence,
  offset,
  text: null,
})

// Delta pertama langsung membentuk teks yang bisa dirender.
let drafts = applyStreamEvent(applyStreamEvent([], started('s1')), delta('s1', 1, 0, 'Halo'))
deep(
  drafts.map((d) => [d.text, d.state]),
  [['Halo', 'streaming']],
  'delta pertama',
)

// Duplikat sequence diabaikan tanpa menyalin ulang teks (array yang sama = React tidak render ulang).
const duplicated = applyStreamEvent(drafts, delta('s1', 1, 0, 'Halo'))
refSame(duplicated, drafts, 'duplikat sequence')

// Lanjutan normal menempel sesuai offset.
drafts = applyStreamEvent(drafts, delta('s1', 2, 4, ' dunia'))
deep(drafts[0].text, 'Halo dunia', 'lanjutan normal')

// Gap sequence: status sinkronisasi, tanpa potongan yang membuat jawaban tampak utuh.
const gap = applyStreamEvent(drafts, delta('s1', 4, 10, '!!'))
deep([gap[0].state, gap[0].text, gap[0].sequence], ['sync', 'Halo dunia', 2], 'gap sequence')

// Offset tidak cocok (tanpa lompatan sequence) juga sync, bukan append.
const mismatch = applyStreamEvent(
  applyStreamEvent(applyStreamEvent([], started('s9')), delta('s9', 1, 0, 'ab')),
  delta('s9', 2, 5, 'c'),
)
deep([mismatch[0].state, mismatch[0].text], ['sync', 'ab'], 'offset tidak cocok')

// Run lama / round lain tidak menempel ke draft yang lebih baru.
deep(applyStreamEvent(drafts, delta('s1', 3, 10, 'x', 'run-lama'))[0].text, 'Halo dunia', 'run lama')
deep(applyStreamEvent(drafts, delta('s1', 3, 10, 'x', 'run-1', 9))[0].text, 'Halo dunia', 'round lain')

// Delta tanpa `started` tidak menghidupkan draft (mis. sesudah canonical menggantikannya).
deep(applyStreamEvent([], delta('s1', 1, 0, 'bangkit')), [], 'delta tanpa started')

// Canonical hanya menutup draft dengan streamId yang sama; giliran lain dibiarkan dan penutupan idempoten.
const twoDrafts = [...applyStreamEvent([], started('s1')), ...applyStreamEvent([], started('s2', 'run-1', 1))]
deep(closeCommittedStreams(twoDrafts, [{ streamId: 's1' }, { streamId: null }]).map((d) => d.streamId), ['s2'], 'canonical s1')
deep(closeCommittedStreams(closeCommittedStreams(twoDrafts, [{ streamId: 's1' }]), [{ streamId: 's1' }]), [twoDrafts[1]], 'canonical ulang')

// Retry memakai streamId baru untuk tiket+round yang sama: draft lama dibuang, teks tidak ditumpuk.
const partial = applyStreamEvent(applyStreamEvent([], started('s1')), delta('s1', 1, 0, 'parsial'))
const retried = applyStreamEvent(partial, started('s2', 'run-2'))
deep(
  retried.map((d) => [d.streamId, d.text]),
  [['s2', '']],
  'retry membuang draft lama',
)
deep(applyStreamEvent(retried, delta('s2', 1, 0, 'ulang', 'run-2'))[0].text, 'ulang', 'draft retry')

// Aborted menandai draft tanpa mengaku selesai; delta sesudahnya tidak menempel dan aborted duplikat diabaikan.
let cut = applyStreamEvent(applyStreamEvent([], started('s3')), delta('s3', 1, 0, 'sebagian'))
cut = applyStreamEvent(cut, aborted('s3', 2, 8))
deep(
  cut.map((d) => [d.state, d.text]),
  [['aborted', 'sebagian']],
  'aborted menandai',
)
refSame(applyStreamEvent(cut, aborted('s3', 2, 8)), cut, 'aborted duplikat')
deep(applyStreamEvent(cut, delta('s3', 3, 8, ' lanjut'))[0].text, 'sebagian', 'delta sesudah aborted')

// Baris activity lengkap seperti ActivityDto dari server; clearTerminalDrafts hanya membaca queueItemId.
const activity = (queueItemId: number, status: 'Pending' | 'Processing') => ({
  queueItemId, status, sourceMessageId: null, modelAlias: null, type: 'UserMessage', isAlternative: false,
})
// Snapshot activity: tiket yang tidak lagi Pending/Processing membuang draft sisa tiket itu.
const forTickets = [...applyStreamEvent([], started('sA', 'run-1', 0, 7)), ...applyStreamEvent([], started('sB', 'run-1', 0, 8))]
deep(
  clearTerminalDrafts(forTickets, [activity(8, 'Processing')]).map((d) => d.streamId),
  ['sB'],
  'tiket terminal dibuang',
)
deep(clearTerminalDrafts(forTickets, []), [], 'semua tiket selesai')
refSame(
  clearTerminalDrafts(forTickets, [activity(7, 'Pending'), activity(8, 'Processing')]),
  forTickets,
  'semua tiket masih aktif',
)

// `started` untuk draft yang sudah ada diabaikan: identitas stream unik per panggilan AI, termasuk saat run lain memakainya.
const existing = applyStreamEvent([], started('s4'))
refSame(applyStreamEvent(existing, started('s4')), existing, 'started duplikat')
refSame(applyStreamEvent(existing, started('s4', 'run-2')), existing, 'started run lain')

// Putaran lama tidak muncul lagi setelah putaran yang lebih baru untuk tiket yang sama.
const roundOne = applyStreamEvent([], started('s5', 'run-1', 1))
refSame(applyStreamEvent(roundOne, started('s6', 'run-1', 0)), roundOne, 'started putaran lama')

// MarkSyncing hanya menandai draft yang masih aktif; aborted tetap aborted dan array tidak berubah bila tak ada draft aktif.
const synced = markSyncing([...applyStreamEvent([], started('s7')), ...cut])
deep(
  synced.map((d) => d.state),
  ['sync', 'aborted'],
  'markSyncing',
)
refSame(markSyncing(cut), cut, 'markSyncing tanpa draft aktif')

// Chunk yang benar-benar bersambung sesudah markSyncing mengembalikan status streaming; gap tidak pernah pulih.
const resumed = applyStreamEvent(markSyncing(applyStreamEvent([], started('s10'))), delta('s10', 1, 0, 'lanjut'))
deep(
  resumed.map((d) => [d.state, d.text]),
  [['streaming', 'lanjut']],
  'lanjut sesudah markSyncing',
)
deep(applyStreamEvent(gap, delta('s1', 5, 12, ' lagi'))[0].state, 'sync', 'gap tetap sync')

console.log('streamDrafts.check: semua pemeriksaan lulus')
