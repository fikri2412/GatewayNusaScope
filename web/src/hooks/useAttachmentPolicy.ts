import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { AttachmentPolicyDto } from '../api/types'

/**
 * Kebijakan lampiran efektif dari server (satu sumber yang sama dengan yang ditegakkan API), dimuat saat masuk,
 * disegarkan saat tab kembali aktif, dan dibaca ulang tepat sebelum unggah/kirim supaya nilai yang dipakai bukan
 * snapshot basi. Kegagalan baca berarti `null` dan pemanggil wajib memblokir operasi lampiran, bukan menebak
 * dengan default lama.
 */
export function useAttachmentPolicy() {
  const [policy, setPolicy] = useState<AttachmentPolicyDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api
      .getAttachmentPolicy()
      .then((next) => {
        setPolicy(next)
        setError(null)
      })
      .catch((e: unknown) => setError(errorMessage(e)))
  }, [])

  useEffect(load, [load])

  const reload = useCallback(() => {
    setPolicy(null)
    setError(null)
    load()
  }, [load])

  const refresh = useCallback(async () => {
    try {
      const next = await api.getAttachmentPolicy()
      setPolicy(next)
      setError(null)
      return next
    } catch (e) {
      setError(errorMessage(e))
      return null
    }
  }, [])

  // Batas yang tampil tidak boleh basi setelah gateway diubah di tempat lain sementara tab ini di belakang.
  useEffect(() => {
    const onFocus = () => void refresh()
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [refresh])

  return { policy, error, reload, refresh }
}
