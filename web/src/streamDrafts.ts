import type { ActivityDto, MessageDto, StreamEvent } from './api/types'

/**
 * Draft teks sementara per streamId. Tidak pernah disimpan per-token di SQL: hilang saat balasan canonical
 * dengan streamId sama tiba, atau saat tiketnya tidak lagi Pending/Processing.
 */
export interface StreamDraft {
  streamId: string
  queueItemId: number
  runId: string
  round: number
  /** Sequence terakhir yang diterapkan (0 setelah `started`); event lama/duplikat tidak menempel. */
  sequence: number
  text: string
  /**
   * sync = gap/offset tidak cocok atau draft ditandai saat reconnect; kembali `streaming` hanya bila chunk
   * berikutnya benar-benar bersambung. aborted = stream berhenti, menunggu canonical atau percobaan baru.
   */
  state: 'streaming' | 'sync' | 'aborted'
}

const newDraft = (event: StreamEvent): StreamDraft => ({
  streamId: event.streamId,
  queueItemId: event.queueItemId,
  runId: event.runId,
  round: event.round,
  sequence: 0,
  text: '',
  state: 'streaming',
})

const replaceAt = (drafts: StreamDraft[], index: number, draft: StreamDraft): StreamDraft[] =>
  drafts.map((d, i) => (i === index ? draft : d))

/**
 * Terapkan satu event `streamEvent`. Dipisah dari React supaya urutannya bisa diperiksa headless
 * (lihat streamDrafts.check.ts). Delta/aborted tanpa `started` tidak pernah membuat draft: chunk telat
 * tidak boleh membangkitkan balasan yang sudah canonical.
 */
export function applyStreamEvent(drafts: StreamDraft[], event: StreamEvent): StreamDraft[] {
  const index = drafts.findIndex((d) => d.streamId === event.streamId)

  if (event.kind === 'started') {
    // Identitas stream unik per panggilan AI: `started` untuk draft yang sudah ada tidak me-reset apa pun,
    // termasuk bila run lain memakai ulang streamId yang sama.
    if (index >= 0) return drafts
    // Putaran lama tidak boleh muncul setelah putaran yang lebih baru untuk tiket yang sama (mis. susulan sesudah retry).
    if (drafts.some((d) => d.queueItemId === event.queueItemId && d.round > event.round)) return drafts
    // Retry memakai streamId baru untuk tiket+round yang sama: draft lama dibuang, teks baru tidak ditumpuk di atasnya.
    const kept = drafts.filter(
      (d) => d.queueItemId !== event.queueItemId || (d.round !== event.round && d.state !== 'aborted'),
    )
    return [...kept, newDraft(event)]
  }

  if (index < 0) return drafts
  const draft = drafts[index]
  // Run/round lama tidak menempel ke draft yang lebih baru (mis. event nyasar sesudah tiket diklaim ulang).
  if (draft.runId !== event.runId || draft.round !== event.round) return drafts

  if (event.kind === 'aborted') {
    if (draft.state === 'aborted') return drafts
    return replaceAt(drafts, index, { ...draft, state: 'aborted', sequence: Math.max(draft.sequence, event.sequence) })
  }

  // Duplikat, urutan lama, atau stream yang sudah berhenti: jangan menambah teks.
  if (draft.state === 'aborted' || event.sequence <= draft.sequence) return drafts
  if (event.sequence !== draft.sequence + 1 || event.offset !== draft.text.length) {
    // Gap/offset tidak cocok: potongan ini tidak di-append supaya tidak terlihat seperti jawaban utuh.
    return replaceAt(drafts, index, { ...draft, state: 'sync' })
  }
  // Chunk yang benar-benar bersambung membuktikan tidak ada yang terlewat (mis. sesudah ditandai sync saat reconnect).
  return replaceAt(drafts, index, {
    ...draft,
    sequence: event.sequence,
    text: draft.text + (event.text ?? ''),
    state: 'streaming',
  })
}

/**
 * Reconnect: draft yang masih aktif ditandai butuh sinkronisasi — teks lama tidak diteruskan seolah utuh,
 * sisa jawaban menyusul lewat balasan canonical (tidak ada replay chunk yang hilang).
 */
export const markSyncing = (drafts: StreamDraft[]): StreamDraft[] =>
  drafts.some((d) => d.state === 'streaming')
    ? drafts.map((d) => (d.state === 'streaming' ? { ...d, state: 'sync' } : d))
    : drafts

/** Balasan canonical membawa streamId draft yang digantikannya; pesan lain (dan pesan giliran lain) tidak menutup draft. */
export function closeCommittedStreams(drafts: StreamDraft[], messages: Pick<MessageDto, 'streamId'>[]): StreamDraft[] {
  const committed = new Set(messages.flatMap((m) => (m.streamId ? [m.streamId] : [])))
  return committed.size === 0 ? drafts : drafts.filter((d) => !committed.has(d.streamId))
}

/**
 * Snapshot activity — event SignalR, atau hasil GET yang tidak digantikan event lebih baru (guard di hook) — adalah
 * daftar tiket aktif: draft yang tiketnya tidak ada lagi berarti selesai/gagal dan dibuang.
 */
export function clearTerminalDrafts(drafts: StreamDraft[], activity: ActivityDto[]): StreamDraft[] {
  const active = new Set(activity.map((a) => a.queueItemId))
  return drafts.some((d) => !active.has(d.queueItemId)) ? drafts.filter((d) => active.has(d.queueItemId)) : drafts
}
