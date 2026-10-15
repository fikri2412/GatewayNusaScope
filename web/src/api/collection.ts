import { request } from './client'
import type { SavedAnswer } from './types'

/** Koleksi jawaban tersimpan milik user, terbaru lebih dulu. */
export const listCollection = () => request<SavedAnswer[]>('/api/collection')

/** 201 baru, 200 bila jawaban itu sudah tersimpan (snapshot lama tidak ditimpa) — dua-duanya sama-sama berhasil. */
export const saveAnswer = (messageId: number) =>
  request<SavedAnswer>('/api/collection', { method: 'POST', body: JSON.stringify({ messageId }) })

/** 204: entri dihapus. 404 bila bukan milik user. */
export const deleteSavedAnswer = (id: string) => request<void>(`/api/collection/${id}`, { method: 'DELETE' })
