import { HubConnectionBuilder } from '@microsoft/signalr'
import { useEffect, useRef } from 'react'
import { API_URL, freshToken } from '../api/client'
import type { ActivityDto, JobDto, MessageDto, StreamEvent } from '../api/types'

/** Jeda antar percobaan: 0, 2, 4, 8, 16 detik, lalu 30 detik selamanya (bawaan SignalR berhenti setelah ~40 detik). */
const retryDelayMs = (attempt: number) => (attempt === 0 ? 0 : Math.min(30_000, 1_000 * 2 ** Math.min(attempt, 5)))

interface Handlers {
  onMessage: (message: MessageDto) => void
  onJob: (job: JobDto) => void
  /** Batch draft streaming; urutan/duplikat ditangani reducer, bukan di sini. */
  onStream: (event: StreamEvent) => void
  /** Snapshot penuh tiket aktif (bukan delta): menggantikan daftar sebelumnya. */
  onActivity: (activity: ActivityDto[]) => void
  /** Dipanggil sesudah join dan sesudah tiap reconnect: event bisa terlewat, jadi muat ulang riwayat, job, dan tiket aktif. */
  onSync: () => void
}

/** Satu koneksi per percakapan yang terbuka; pindah percakapan = koneksi lama ditutup (keluar dari group-nya). */
export function useChatHub(conversationId: string, handlers: Handlers) {
  const ref = useRef(handlers)
  useEffect(() => {
    ref.current = handlers
  })

  useEffect(() => {
    let active = true
    const connection = new HubConnectionBuilder()
      // Token diambil ulang tiap (re)connect: access token pendek umurnya, token kedaluwarsa membuat negosiasi 401 terus.
      .withUrl(`${API_URL}/hubs/chat`, { accessTokenFactory: freshToken })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => retryDelayMs(ctx.previousRetryCount) })
      .build()

    const join = async () => {
      await connection.invoke('JoinConversation', conversationId)
      if (active) ref.current.onSync()
    }
    connection.on('messageCreated', (m: MessageDto) => ref.current.onMessage(m))
    connection.on('jobUpdated', (j: JobDto) => ref.current.onJob(j))
    connection.on('streamEvent', (e: StreamEvent) => ref.current.onStream(e))
    connection.on('activityChanged', (a: ActivityDto[]) => ref.current.onActivity(a))
    connection.onreconnected(() => void join().catch(console.error))

    let retryTimer: number | undefined
    // start() yang gagal (API belum hidup, jaringan putus) tidak dicoba otomatis oleh SignalR: ulangi sampai berhasil.
    // Hanya kegagalan start yang diulang; join yang gagal pada koneksi yang sudah tersambung bukan alasan memanggil start lagi.
    const start = (attempt: number) => {
      connection.start().then(
        () => join().catch((e: unknown) => { if (active) console.error('Gabung percakapan gagal', e) }),
        (e: unknown) => {
          // start() ditolak bila koneksi ditutup saat negosiasi (StrictMode, pindah percakapan cepat): bukan error.
          if (!active) return
          console.error('Koneksi SignalR gagal', e)
          retryTimer = window.setTimeout(() => start(attempt + 1), retryDelayMs(attempt + 1))
        },
      )
    }
    start(0)

    return () => {
      active = false
      clearTimeout(retryTimer)
      void connection.stop()
    }
  }, [conversationId])
}
