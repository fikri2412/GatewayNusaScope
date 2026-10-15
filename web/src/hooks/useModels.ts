import { useCallback, useEffect, useState } from 'react'
import { api, errorMessage } from '../api/client'
import type { ModelInfoDto } from '../api/types'

/**
 * Katalog model dari server (kunci gateway hanya ada di server), dimuat sekali setelah masuk dan bisa dimuat ulang.
 * Muat ulang menutup pengiriman sampai katalog berhasil dibaca lagi; kegagalan bukan daftar kosong.
 */
export function useModels() {
  const [models, setModels] = useState<ModelInfoDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api
      .listModels()
      .then((next) => {
        setModels(next)
        setError(null)
      })
      .catch((e: unknown) => setError(errorMessage(e)))
  }, [])

  useEffect(load, [load])

  const reload = useCallback(() => {
    setModels(null)
    setError(null)
    load()
  }, [load])

  return { models, error, reload }
}
