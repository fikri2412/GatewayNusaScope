import { useCallback, useRef, useState } from 'react'
import { api, errorMessage, type UploadOptions } from '../api/client'
import { compareAnswer, regenerateAnswer } from '../api/conversations'
import type { ActivityDto, JobDto, MessageDto, StreamEvent } from '../api/types'
import { applyStreamEvent, clearTerminalDrafts, closeCommittedStreams, markSyncing, type StreamDraft } from '../streamDrafts'
import { useChatHub } from './useChatHub'

/** Gabung berdasarkan id: pesan user datang dua kali (respons POST dan event SignalR), riwayat dimuat ulang saat sync. */
function mergeById<T extends { id: number }>(current: T[], incoming: T[]): T[] {
  const byId = new Map(current.map((x) => [x.id, x]))
  for (const x of incoming) byId.set(x.id, x)
  return [...byId.values()].sort((a, b) => a.id - b.id)
}

/**
 * Baris tiket optimistis: menutup celah sebelum snapshot server tiba, supaya event `started` tidak membuat
 * baris tanpa bendera alternatif. Snapshot GET/SignalR selalu menimpanya.
 */
const activityRow = (queueItemId: number, status: ActivityDto['status'], over: Partial<ActivityDto> = {}): ActivityDto => ({
  queueItemId,
  status,
  sourceMessageId: null,
  modelAlias: null,
  type: 'UserMessage',
  isAlternative: false,
  ...over,
})

/** onReply dipanggil tiap balasan assistant tiba: pemakaian token percakapan baru saja berubah dan tidak boleh tampil basi. */
export function useConversation(conversationId: string, onReply?: () => void) {
  const [messages, setMessages] = useState<MessageDto[]>([])
  const [drafts, setDrafts] = useState<StreamDraft[]>([])
  const [jobs, setJobs] = useState<JobDto[]>([])
  const [activity, setActivity] = useState<ActivityDto[]>([])
  const [error, setError] = useState<string | null>(null)
  // streamId yang sudah membuat draft atau sudah punya pesan canonical: identitasnya unik per panggilan AI,
  // jadi `started` yang telat tidak boleh menghidupkan kembali draft yang sudah selesai atau digantikan.
  const seenStreams = useRef(new Set<string>())
  // Hasil GET yang lebih tua tidak boleh menimpa event yang lebih baru (menghidupkan tiket terminal atau
  // membuang draft baru): event menaikkan revisi, tiap permintaan snapshot punya generasi sendiri.
  const activityRev = useRef(0)
  const activityGen = useRef(0)
  const syncGen = useRef(0)

  /** Pesan canonical juga menutup draft dengan streamId yang sama: draft digantikan, bukan ditumpuk. */
  const mergeMessages = useCallback((incoming: MessageDto[]) => {
    for (const m of incoming) if (m.streamId !== null) seenStreams.current.add(m.streamId)
    setMessages((prev) => mergeById(prev, incoming))
    setDrafts((prev) => closeCommittedStreams(prev, incoming))
  }, [])

  const mergeJobs = useCallback((incoming: JobDto[]) => setJobs((prev) => mergeById(prev, incoming)), [])

  /** Snapshot dari SignalR adalah sumber otoritatif: langsung dipakai dan menaikkan revisi. */
  const onActivity = useCallback((snapshot: ActivityDto[]) => {
    activityRev.current += 1
    setActivity(snapshot)
    setDrafts((prev) => clearTerminalDrafts(prev, snapshot))
  }, [])

  /** GET snapshot dengan guard: hanya dipakai bila tidak ada event lebih baru dan tidak digantikan permintaan lain. */
  const fetchActivity = useCallback(() => {
    const generation = ++activityGen.current
    const revision = activityRev.current
    api
      .listActivity(conversationId)
      .then((rows) => {
        if (generation !== activityGen.current || revision !== activityRev.current) return
        setActivity(rows)
        setDrafts((prev) => clearTerminalDrafts(prev, rows))
      })
      .catch((e: unknown) => setError(errorMessage(e)))
  }, [conversationId])

  const sync = useCallback(() => {
    const generation = ++syncGen.current
    const activityGeneration = ++activityGen.current
    const revision = activityRev.current
    // Reconnect: draft yang masih aktif ditandai butuh sinkronisasi; sisa teks menunggu canonical, bukan replay chunk.
    setDrafts(markSyncing)
    Promise.all([api.listMessages(conversationId), api.listJobs(conversationId), api.listActivity(conversationId)])
      .then(([rows, jobRows, activityRows]) => {
        if (generation !== syncGen.current) return
        mergeMessages(rows)
        mergeJobs(jobRows)
        if (activityGeneration === activityGen.current && revision === activityRev.current) {
          setActivity(activityRows)
          setDrafts((prev) => clearTerminalDrafts(prev, activityRows))
        }
        setError(null)
      })
      .catch((e: unknown) => {
        if (generation === syncGen.current) setError(errorMessage(e))
      })
  }, [conversationId, mergeMessages, mergeJobs])

  const onMessage = useCallback(
    (m: MessageDto) => {
      mergeMessages([m])
      if (m.role === 'assistant') onReply?.()
    },
    [mergeMessages, onReply],
  )

  const onStream = useCallback(
    (event: StreamEvent) => {
      if (event.conversationId !== conversationId) return // event dari conversation lain tidak pernah dipakai
      if (event.kind === 'started') {
        if (seenStreams.current.has(event.streamId)) return // stream ini sudah dibuat atau sudah canonical
        seenStreams.current.add(event.streamId)
        activityRev.current += 1
        setActivity((prev) =>
          prev.some((t) => t.queueItemId === event.queueItemId)
            ? prev
            : [...prev, activityRow(event.queueItemId, 'Processing')],
        )
      }
      setDrafts((prev) => applyStreamEvent(prev, event))
    },
    [conversationId],
  )

  useChatHub(conversationId, { onMessage, onJob: (j) => mergeJobs([j]), onStream, onActivity, onSync: sync })

  const send = useCallback(
    async (content: string, attachmentIds: string[], model: string, thinkMode?: string) => {
      const { message, queueItemId } = await api.sendMessage(conversationId, content, attachmentIds, model, thinkMode)
      mergeMessages([message])
      // Snapshot yang sudah memuat tiket dengan id lebih besar berarti dibaca sesudah tiket ini di-commit:
      // tidak adanya tiket ini di situ berarti sudah terminal (mis. balasan tiba sebelum respons POST).
      setActivity((prev) =>
        prev.some((t) => t.queueItemId === queueItemId) || prev.some((t) => t.queueItemId > queueItemId)
          ? prev
          : [...prev, activityRow(queueItemId, 'Pending')],
      )
      // Entri optimistis di atas tetap dikoreksi snapshot server, termasuk bila tiket sudah selesai.
      fetchActivity()
    },
    [conversationId, mergeMessages, fetchActivity],
  )

  /**
   * Jawab ulang satu jawaban: tiket alternatif, tanpa pesan user baru. Kunci idempotensi dibuat sekali per
   * panggilan di sini (bukan di MessageBubble), jadi satu klik = satu tiket; klik ulang sengaja jadi tiket baru.
   */
  const regenerate = useCallback(
    async (messageId: number) => {
      const turn = await regenerateAnswer(conversationId, messageId, crypto.randomUUID())
      setActivity((prev) =>
        prev.some((t) => t.queueItemId === turn.queueItemId)
          ? prev
          : [
              ...prev,
              activityRow(turn.queueItemId, 'Pending', {
                sourceMessageId: turn.sourceMessageId,
                modelAlias: turn.modelAlias,
                isAlternative: true,
              }),
            ],
      )
      fetchActivity()
    },
    [conversationId, fetchActivity],
  )

  /** Bandingkan satu jawaban dengan sampai tiga model: satu tiket alternatif per model. */
  const compare = useCallback(
    async (messageId: number, models: string[]) => {
      const { sourceMessageId, branches } = await compareAnswer(conversationId, messageId, models, crypto.randomUUID())
      setActivity((prev) => {
        const known = new Set(prev.map((t) => t.queueItemId))
        const fresh = branches
          .filter((b) => !known.has(b.queueItemId))
          .map((b) => activityRow(b.queueItemId, 'Pending', { sourceMessageId, modelAlias: b.modelAlias, isAlternative: true }))
        return fresh.length > 0 ? [...prev, ...fresh] : prev
      })
      fetchActivity()
    },
    [conversationId, fetchActivity],
  )

  const upload = useCallback((file: File, options?: UploadOptions) => api.uploadAttachment(conversationId, file, options), [conversationId])

  // Sumber status tunggu adalah tiket Pending/Processing; tiket alternatif tidak dihitung (jawaban utama tidak ditunggu).
  const waitingForReply = activity.some((t) => !t.isAlternative)

  return { messages, drafts, jobs, pendingItems: activity, error, waitingForReply, send, upload, regenerate, compare }
}
