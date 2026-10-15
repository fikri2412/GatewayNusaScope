import { useCallback, useEffect, useRef, useState } from 'react'
import { listArtifacts } from '../api/artifacts'
import { errorMessage } from '../api/client'
import type { ArtifactSummary } from '../api/types'

/**
 * Daftar artifact satu percakapan (tanpa isi). Dimuat ulang tiap `refreshKey` berubah — pemanggil memberi jumlah
 * pesan, karena AI membuat artifact sebelum jawabannya tersimpan — dan setelah pengguna mengubah artifact.
 * Respons yang datang terlambat tidak menimpa respons yang lebih baru.
 */
export function useArtifacts(conversationId: string, refreshKey: number) {
  const [artifacts, setArtifacts] = useState<ArtifactSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const latest = useRef(0)

  const reload = useCallback(
    () => {
      const ticket = ++latest.current
      return listArtifacts(conversationId)
        .then((rows) => {
          if (ticket !== latest.current) return
          setArtifacts(rows)
          setError(null)
        })
        .catch((e: unknown) => {
          if (ticket === latest.current) setError(errorMessage(e))
        })
    },
    [conversationId],
  )

  // Efek memakai fungsi sinkron (pola useModels); `reload` async tetap dipakai pemanggil yang perlu menunggu.
  const refresh = useCallback(() => {
    void reload()
  }, [reload])
  useEffect(refresh, [refresh, refreshKey])

  return { artifacts, error, reload }
}
