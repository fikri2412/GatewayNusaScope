import type { AttachmentDto, MessageDto } from './api/types.ts'
import { buildConversationMarkdown, markdownFileName } from './exportMarkdown.ts'

// Jalankan: npm run check:export. Node menjalankan .ts ini langsung; tanpa framework/dependency.
// Perbandingan deterministik: nilai dibandingkan setelah JSON.stringify.

const deep = (actual: unknown, expected: unknown, label: string) => {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(`${label}: ${JSON.stringify(actual)} !== ${JSON.stringify(expected)}`)
}

const ok = (condition: boolean, label: string) => {
  if (!condition) throw new Error(label)
}

const attachment = (fileName: string): AttachmentDto => ({
  id: 'att-1',
  fileName,
  kind: 'document',
  sizeBytes: 2048,
  downloadUrl: `/api/attachments/9f8b2c?access_token=rahasia`,
  contentType: 'application/pdf',
})

/** Fixture lengkap supaya tetap cocok walau DTO pesan bertambah kolom baru (§4.2). */
const message = (over: Partial<MessageDto> & Pick<MessageDto, 'role'>): MessageDto => ({
  id: 1,
  content: null,
  createdAt: '2026-10-14T03:00:00Z',
  attachments: [],
  finishReason: null,
  modelAlias: null,
  streamId: null,
  queueItemId: null,
  sourceMessageId: null,
  isPrimary: true,
  tokensUsed: null,
  queueWaitMs: null,
  firstTokenMs: null,
  totalMs: null,
  ...over,
})

const md = buildConversationMarkdown({
  title: 'Analisis Q3',
  exportedAt: new Date(Date.UTC(2026, 9, 14, 3, 30)),
  messages: [
    message({ id: 1, role: 'user', content: 'Rangkum laporan ini', attachments: [attachment('laporan-q3.pdf'), attachment('data.csv')] }),
    message({ id: 2, role: 'assistant', content: 'Ringkasannya singkat.', modelAlias: 'glm-4.6' }),
    message({ id: 3, role: 'user', content: 'Terima kasih' }),
  ],
})

// Judul dan waktu ekspor.
ok(md.startsWith('# Analisis Q3\n'), 'judul tidak di awal')
ok(md.includes('_Diekspor 2026-10-14T03:30:00.000Z_'), 'waktu ekspor tidak ada')

// Urutan pesan sama dengan riwayat; label peran menandai pemilik teks.
const first = md.indexOf('**Pengguna**')
const answer = md.indexOf('**Asisten · glm-4.6**')
const last = md.indexOf('**Pengguna**', first + 1)
ok(first >= 0 && first < answer && answer < last, 'urutan peran salah')
ok(md.indexOf('Rangkum laporan ini') > first && md.indexOf('Rangkum laporan ini') < answer, 'isi pesan user salah tempat')
ok(md.indexOf('Ringkasannya singkat.') > answer && md.indexOf('Ringkasannya singkat.') < last, 'isi jawaban salah tempat')

// Alias model hanya pada jawaban asisten; pesan user tidak pernah dapat label model.
deep(md.split('**Asisten').length - 1, 1, 'jumlah label asisten')
deep(md.split('glm-4.6').length - 1, 1, 'alias model muncul bukan sekali')

// Lampiran: nama berkas saja, tanpa URL unduhan ber-token.
ok(md.includes('Lampiran: laporan-q3.pdf, data.csv'), 'nama lampiran tidak lengkap')
ok(!/api\/attachments|rahasia|https?:/i.test(md), 'URL API ikut terekspor')

// Nama berkas `{slug}-{yyyymmdd-hhmm}.md` dari waktu lokal pemanggil.
deep(markdownFileName('Analisis Q3: Laporan Bulanan!', new Date(2026, 9, 14, 9, 5)), 'analisis-q3-laporan-bulanan-20261014-0905.md', 'nama berkas')
deep(markdownFileName('??', new Date(2026, 0, 2, 3, 4)), 'sesi-20260102-0304.md', 'slug kosong')

// Judul panjang dipotong di batas 40 karakter tanpa menyisakan tanda hubung di ujung.
deep(markdownFileName('a'.repeat(60), new Date(2026, 0, 2, 3, 4)), `${'a'.repeat(40)}-20260102-0304.md`, 'potongan slug')
deep(markdownFileName('unggahan  -  ganda', new Date(2026, 0, 2, 3, 4)), 'unggahan-ganda-20260102-0304.md', 'tanda hubung ganda')

console.log('exportMarkdown.check: semua pemeriksaan lulus')
