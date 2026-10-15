import { HubConnectionBuilder } from '@microsoft/signalr'
import { useEffect, useRef } from 'react'
import { API_URL } from '../api/client'
import { getToken } from '../api/session'
import type { ActivityDto, JobDto, MessageDto, StreamEvent } from '../api/types'

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
      .withUrl(`${API_URL}/hubs/chat`, { accessTokenFactory: () => getToken() ?? '' })
      .withAutomaticReconnect()
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

    connection
      .start()
      .then(join)
      .catch((e: unknown) => {
        // start() ditolak bila koneksi ditutup saat negosiasi (StrictMode, pindah percakapan cepat): bukan error.
        if (active) console.error('Koneksi SignalR gagal', e)
      })

    return () => {
      active = false
      void connection.stop()
    }
  }, [conversationId])
}
