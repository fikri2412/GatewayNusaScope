import { request } from './client'
import type { ArtifactDto, ArtifactKind, ArtifactSummary, ArtifactVersionContent, ArtifactVersionInfo } from './types'

// Galat dilempar sebagai ApiError: 409 `version_conflict` membawa `currentVersion` di `body`,
// 400 `invalid_artifact`, 409 `artifact_limit`/`version_limit`.

export const listArtifacts = (conversationId: string) =>
  request<ArtifactSummary[]>(`/api/conversations/${conversationId}/artifacts`)

export const createArtifact = (conversationId: string, input: { kind: ArtifactKind; title: string; language?: string | null; content: string }) =>
  request<ArtifactDto>(`/api/conversations/${conversationId}/artifacts`, { method: 'POST', body: JSON.stringify(input) })

export const getArtifact = (id: string) => request<ArtifactDto>(`/api/artifacts/${id}`)

/** Versi baru dari `baseVersion` (versi aktif yang dilihat klien). Isi sama → 200 tanpa versi baru. */
export const saveArtifact = (id: string, baseVersion: number, content: string, title?: string) =>
  request<ArtifactDto>(`/api/artifacts/${id}`, { method: 'PUT', body: JSON.stringify({ baseVersion, content, title }) })

/** 204; versi ikut terhapus. */
export const deleteArtifact = (id: string) => request<void>(`/api/artifacts/${id}`, { method: 'DELETE' })

export const listArtifactVersions = (id: string) => request<ArtifactVersionInfo[]>(`/api/artifacts/${id}/versions`)

export const getArtifactVersion = (id: string, version: number) =>
  request<ArtifactVersionContent>(`/api/artifacts/${id}/versions/${version}`)

/** Urungkan/pulihkan: isi versi lama disalin sebagai versi BARU; riwayat tidak berubah. */
export const restoreArtifact = (id: string, version: number, baseVersion: number) =>
  request<ArtifactDto>(`/api/artifacts/${id}/restore`, { method: 'POST', body: JSON.stringify({ version, baseVersion }) })
