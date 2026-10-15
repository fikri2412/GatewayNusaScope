import { renderMarkdown } from '../markdown'
import './Markdown.css'

/**
 * Jawaban assistant dirender sebagai markdown. Draft streaming tetap teks biasa: potongan yang belum utuh
 * tidak boleh terlihat seperti jawaban final yang sudah diformat.
 */
export function Markdown({ text }: { text: string }) {
  return <div className="md-body">{renderMarkdown(text)}</div>
}
