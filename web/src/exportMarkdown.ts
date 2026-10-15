import type { MessageDto } from './api/types'

export interface ConversationExport {
  title: string
  messages: MessageDto[]
  /** Waktu unduhan; ikut ditulis supaya berkas ekspor lama tetap bisa dibedakan. */
  exportedAt: Date
}

/**
 * Markdown satu sesi: judul, waktu ekspor, lalu tiap pesan urut seperti di chat (label peran, alias model
 * hanya untuk asisten, isi apa adanya). Lampiran hanya nama berkasnya — URL unduhan ber-token tidak boleh
 * ikut tersimpan di berkas yang bisa dibagikan.
 */
export function buildConversationMarkdown({ title, messages, exportedAt }: ConversationExport): string {
  const parts = [`# ${title}`, '', `_Diekspor ${exportedAt.toISOString()}_`]
  for (const message of messages) {
    const names = message.attachments.map((a) => a.fileName).join(', ')
    parts.push(
      '',
      message.role === 'assistant' ? `**Asisten${message.modelAlias ? ` · ${message.modelAlias}` : ''}**` : '**Pengguna**',
      '',
      message.content ?? '',
    )
    if (names) parts.push('', `Lampiran: ${names}`)
  }
  return `${parts.join('\n')}\n`
}

/** Nama berkas `{slug}-{yyyymmdd-hhmm}.md` dari jam lokal; slug dibatasi karakter ASCII supaya aman di semua sistem berkas. */
export function markdownFileName(title: string, at: Date): string {
  const slug = title
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[^a-z0-9]+/g, '-')
    .slice(0, 40)
    .replace(/^-+|-+$/g, '') || 'sesi'
  const date = `${at.getFullYear()}${String(at.getMonth() + 1).padStart(2, '0')}${String(at.getDate()).padStart(2, '0')}`
  const time = `${String(at.getHours()).padStart(2, '0')}${String(at.getMinutes()).padStart(2, '0')}`
  return `${slug}-${date}-${time}.md`
}

/** Unduhan sisi klien: Blob + object URL, dicabut lagi sesudah klik (pola `downloadAttachment`). */
export function downloadText(filename: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  document.body.append(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}
