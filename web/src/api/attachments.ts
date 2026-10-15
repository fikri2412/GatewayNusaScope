// Aturan lampiran sisi klien dari kebijakan efektif server (GET /api/attachment-policy); server tetap memvalidasi ulang.
// Tidak ada batas hardcoded di sini: nilai yang dipakai selalu berasal dari parameter kebijakan pemanggil.
// Fungsi di sini murni (tanpa DOM) supaya bisa diperiksa lewat `npm run check:attachments`.
import type { AttachmentPolicyDto } from './types'

const VIDEO_EXTENSIONS = ['.mp4', '.webm', '.mov', '.avi', '.mkv']
// Hanya tipe kanonik ini yang boleh dirender sebagai gambar; MIME lain (mis. svg/html) tidak pernah dipratinjau.
const PREVIEWABLE_IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/webp']

/** Ukuran dalam satuan yang tidak menyesatkan saat batasnya bukan kelipatan MB (mis. 64 KB dari gateway). */
export const formatBytes = (bytes: number) =>
  bytes >= 1024 * 1024 ? `${Math.round((bytes / (1024 * 1024)) * 10) / 10} MB`
    : bytes >= 1024 ? `${Math.round((bytes / 1024) * 10) / 10} KB` : `${bytes} byte`

/** true hanya untuk PNG/JPEG/WEBP kanonik; tipe kosong/asing (termasuk MIME kiriman user mentah) false. */
export const isImageContentType = (contentType: string | null | undefined) =>
  PREVIEWABLE_IMAGE_TYPES.includes((contentType ?? '').split(';')[0].trim().toLowerCase())

/** Alasan file tidak bisa diunggah; null = lanjutkan. Video ditolak eksplisit, bukan sekadar tidak ada di daftar. */
export function uploadError(file: { name: string; type: string; size: number }, policy: AttachmentPolicyDto): string | null {
  const extension = file.name.slice(file.name.lastIndexOf('.')).toLowerCase()
  if (file.type.startsWith('video/') || VIDEO_EXTENSIONS.includes(extension))
    return 'Video tidak didukung. Kirim dokumen (PDF, XLSX, CSV) atau gambar (PNG/JPEG/WEBP).'
  if (!policy.allowedExtensions.includes(extension))
    return `Format tidak didukung. Yang diizinkan: ${policy.allowedExtensions.join(', ')}.`
  if (file.size > policy.maxBytes) return `Ukuran file maksimal ${formatBytes(policy.maxBytes)}.`
  return null
}

/** Alasan lampiran baru tidak bisa ditambahkan lagi; null = masih muat. Lampiran yang sudah ada tidak pernah diubah. */
export function attachmentLimitError(count: number, policy: AttachmentPolicyDto): string | null {
  return count >= policy.maxAttachmentsPerMessage
    ? `Maksimal ${policy.maxAttachmentsPerMessage} lampiran per pesan; hapus salah satu untuk menambah.`
    : null
}

/**
 * Alasan lampiran yang sudah terpasang tidak bisa dikirim menurut kebijakan efektif terbaru; null = boleh kirim.
 * Dipakai tepat sebelum kirim: batas bisa menyempit setelah file diunggah, dan lampiran yang tidak lagi memenuhi
 * batas baru harus diblokir tanpa menghapus draft atau file lama.
 */
export function attachmentsPolicyError(
  attachments: { fileName: string; sizeBytes: number }[],
  policy: AttachmentPolicyDto,
): string | null {
  if (attachments.length > policy.maxAttachmentsPerMessage)
    return `Maksimal ${policy.maxAttachmentsPerMessage} lampiran per pesan.`
  for (const attachment of attachments) {
    const problem = uploadError({ name: attachment.fileName, type: '', size: attachment.sizeBytes }, policy)
    if (problem) return problem
  }
  return null
}

/**
 * Alasan kirim diblokir karena lampiran gambar (PNG/JPEG/WEBP) membutuhkan model vision; null = boleh kirim.
 * Model vision null (belum dikonfirmasi) sama-sama memblokir: tidak ada janji yang tidak bisa ditepati.
 * PDF tidak pernah diblokir di sini — teksnya bisa dibaca model mana pun, halaman scan dijelaskan terpisah.
 */
export function visionBlockReason(
  supportsVision: boolean | null | undefined,
  attachments: { contentType: string }[],
): string | null {
  if (!attachments.some((a) => isImageContentType(a.contentType))) return null
  if (supportsVision === true) return null
  return supportsVision === false
    ? 'Model ini tidak menerima gambar. Pilih model berbadge vision untuk mengirim PNG/JPEG/WEBP.'
    : 'Dukungan vision model ini belum dikonfirmasi. Pilih model berbadge vision untuk mengirim PNG/JPEG/WEBP.'
}

/** Catatan jujur untuk PDF di percakapan: teks bisa diproses model biasa, halaman scan butuh pemeriksaan visual. */
export function pdfVisionNote(supportsVision: boolean | null | undefined): string | null {
  if (supportsVision === true) return null
  return 'PDF teks bisa diproses model ini; PDF hasil scan atau yang perlu dilihat visual memerlukan model berbadge vision.'
}
