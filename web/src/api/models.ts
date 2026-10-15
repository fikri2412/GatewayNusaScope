import type { ModelInfoDto } from './types'

/** Layak dikirim: siap DAN tool sudah dikonfirmasi; tools null/false tetap tampil sebagai pilihan nonaktif. */
export const isEligible = (m: ModelInfoDto) => m.ready && m.supportsTools === true

/** Sebab alias tidak bisa dipakai; null berarti layak. */
export function disabledReason(m: ModelInfoDto): string | null {
  if (!m.ready) return m.unavailableReason ?? 'model belum siap'
  if (m.supportsTools === false) return 'tool tidak didukung'
  if (m.supportsTools === null) return 'dukungan tool belum dikonfirmasi'
  return null
}
