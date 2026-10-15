import { request } from './client'
import type { CompareEnqueued, TurnEnqueued } from './types'

// Galat server dilempar sebagai ApiError (status/code/body tersedia), jadi pemanggil bisa membedakan
// 409 conversation_busy, 400 not_regenerable, atau 400 compare_invalid tanpa membaca teks pesan.

/** 204: sesi dihapus. 404 bila bukan milik user, 409 `conversation_busy` bila masih ada tiket/job berjalan. */
export const deleteConversation = (id: string) => request<void>(`/api/conversations/${id}`, { method: 'DELETE' })

/** Jawab ulang satu jawaban. `clientRequestId` = kunci idempotensi; dibuat sekali per panggilan pemanggil. */
export const regenerateAnswer = (conversationId: string, messageId: number, clientRequestId: string) =>
  request<TurnEnqueued>(`/api/conversations/${conversationId}/messages/${messageId}/regenerate`, {
    method: 'POST',
    body: JSON.stringify({ clientRequestId }),
  })

/** 202 tiket baru, 200 bila kunci itu sudah pernah diantrekan. 400 `compare_invalid` membawa `branches`. */
export const compareAnswer = (conversationId: string, messageId: number, models: string[], clientRequestId: string) =>
  request<CompareEnqueued>(`/api/conversations/${conversationId}/messages/${messageId}/compare`, {
    method: 'POST',
    body: JSON.stringify({ models, clientRequestId }),
  })
