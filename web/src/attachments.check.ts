import { attachmentLimitError, attachmentsPolicyError, isImageContentType, pdfVisionNote, uploadError, visionBlockReason } from './api/attachments.ts'
import type { AttachmentPolicyDto } from './api/types.ts'

// Jalankan: npm run check:attachments. Node menjalankan .ts ini langsung; tanpa framework/dependency.
// Perbandingan deterministik: nilai dibandingkan setelah JSON.stringify.

const deep = (actual: unknown, expected: unknown, label: string) => {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(`${label}: ${JSON.stringify(actual)} !== ${JSON.stringify(expected)}`)
}

const rejects = (value: string | null, label: string) => deep(value === null, false, label)

const file = (name: string, type = '', size = 1024) => ({ name, type, size })

/** Kebijakan contoh seperti jawaban GET /api/attachment-policy; tiap pemeriksaan menyatakan batasnya sendiri. */
const policy = (over: Partial<AttachmentPolicyDto> = {}): AttachmentPolicyDto => ({
  maxBytes: 10 * 1024 * 1024,
  allowedExtensions: ['.csv', '.jpeg', '.jpg', '.pdf', '.png', '.webp', '.xlsx'],
  maxAttachmentsPerMessage: 6,
  maxImagePixels: 16_000_000,
  sourceSettingsVersion: 'v1',
  policyHash: 'contoh',
  ...over,
})

const chips = (count: number) => Array.from({ length: count }, (_, i) => ({ fileName: `berkas${i}.pdf`, sizeBytes: 1024 }))

// Ekstensi yang diterima: dokumen lama tetap, dokumen/gambar 6C masuk; video dan tipe asing di bawah.
for (const name of ['laporan.xlsx', 'data.csv', 'dokumen.pdf', 'gambar.png', 'foto.JPG', 'foto.jpeg', 'foto.webp']) {
  deep(uploadError(file(name), policy()), null, `terima ${name}`)
}

// Video ditolak eksplisit, baik dari MIME maupun ekstensi.
rejects(uploadError(file('klip.mp4', 'video/mp4'), policy()), 'video dari MIME')
rejects(uploadError(file('klip.mp4'), policy()), 'video dari ekstensi')
rejects(uploadError(file('catatan.txt'), policy()), 'ekstensi asing')

// Allowlist datang dari kebijakan, bukan konstanta: himpunan bagian yang lebih sempit ditegakkan apa adanya.
const onlyPdf = policy({ allowedExtensions: ['.pdf'] })
deep(uploadError(file('dokumen.pdf'), onlyPdf), null, 'pdf di allowlist sempit')
rejects(uploadError(file('gambar.png'), onlyPdf), 'png di luar allowlist sempit')

// Batas ukuran dari kebijakan: tepat di batas boleh, satu byte lebih besar ditolak.
deep(uploadError(file('besar.csv', 'text/csv', 10 * 1024 * 1024), policy()), null, 'tepat 10 MB')
rejects(uploadError(file('besar.csv', 'text/csv', 10 * 1024 * 1024 + 1), policy()), 'lebih 10 MB')
deep(uploadError(file('kecil.csv', 'text/csv', 2048), policy({ maxBytes: 2048 })), null, 'tepat batas kebijakan 2 KB')
rejects(uploadError(file('kecil.csv', 'text/csv', 2049), policy({ maxBytes: 2048 })), 'lebih batas kebijakan 2 KB')

// Batas lampiran per pesan: tambahan dihentikan saat batas tercapai, bukan dengan mengubah lampiran lama.
deep(attachmentLimitError(5, policy()), null, 'lima lampiran')
rejects(attachmentLimitError(6, policy()), 'lampiran keenam ditolak')
rejects(attachmentLimitError(7, policy()), 'lampiran ketujuh ditolak')
rejects(attachmentLimitError(2, policy({ maxAttachmentsPerMessage: 2 })), 'batas kebijakan 2')

// Kirim: lampiran terpasang diperiksa ulang terhadap kebijakan terbaru; batas menyempit memblokir kirim, bukan menghapus lampiran.
deep(attachmentsPolicyError([], policy()), null, 'tanpa lampiran')
deep(attachmentsPolicyError(chips(6), policy()), null, 'enam lampiran masih boleh kirim')
rejects(attachmentsPolicyError(chips(7), policy()), 'tujuh lampiran tidak bisa kirim')
rejects(attachmentsPolicyError([{ fileName: 'besar.pdf', sizeBytes: 2049 }], policy({ maxBytes: 2048 })), 'lampiran lama di atas ukuran baru')
rejects(attachmentsPolicyError([{ fileName: 'grafik.png', sizeBytes: 1024 }], onlyPdf), 'lampiran lama di luar allowlist baru')
rejects(attachmentsPolicyError([{ fileName: 'klip.mp4', sizeBytes: 1024 }], policy()), 'video terpasang')

// Hanya PNG/JPEG/WEBP kanonik yang dipratinjau; MIME kiriman user (svg/html) tidak pernah lolos.
deep(['image/png', 'image/jpeg', 'image/webp', 'IMAGE/PNG', 'image/png; charset=binary'].map(isImageContentType), [true, true, true, true, true], 'tipe gambar')
deep(['image/svg+xml', 'text/html', 'application/pdf', '', null, undefined].map(isImageContentType), [false, false, false, false, false, false], 'tipe bukan gambar')

// Gate vision: hanya lampiran gambar yang menuntut model vision terkonfirmasi.
const png = [{ contentType: 'image/png' }]
const jpeg = [{ contentType: 'image/jpeg' }]
const pdf = [{ contentType: 'application/pdf' }]
const xlsx = [{ contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }]
deep(visionBlockReason(true, png), null, 'vision terkonfirmasi')
deep(visionBlockReason(true, [...png, ...xlsx]), null, 'vision dengan beberapa lampiran')
rejects(visionBlockReason(false, jpeg), 'vision false')
rejects(visionBlockReason(null, png), 'vision belum dikonfirmasi')
rejects(visionBlockReason(undefined, png), 'vision tidak tersedia')
deep(visionBlockReason(false, pdf), null, 'PDF tidak diblokir')
deep(visionBlockReason(null, []), null, 'tanpa lampiran')
deep(visionBlockReason(false, xlsx), null, 'xlsx tidak diblokir')

// Catatan PDF: model vision tidak perlu catatan; model lain diarahkan jujur ke model vision.
deep(pdfVisionNote(true), null, 'catatan PDF model vision')
rejects(pdfVisionNote(false), 'catatan PDF tanpa vision')
rejects(pdfVisionNote(null), 'catatan PDF belum dikonfirmasi')

console.log('attachments.check: semua pemeriksaan lulus')
