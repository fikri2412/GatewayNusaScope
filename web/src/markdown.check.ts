import { renderToStaticMarkup } from 'react-dom/server'
import { renderMarkdown } from './markdown.ts'

// Jalankan: npm run check:markdown. Node menjalankan .ts ini langsung; tanpa framework/dependency.
// Markdown.tsx tidak bisa diimpor Node (ekstensi .tsx tidak dikenal), jadi yang diperiksa renderMarkdown —
// fungsi yang persis dipakai Markdown.tsx untuk setiap jawaban assistant.

const html = (text: string) => renderToStaticMarkup(renderMarkdown(text))

const present = (markup: string, needle: string, label: string) => {
  if (!markup.includes(needle)) throw new Error(`${label}: "${needle}" tidak ada di ${markup}`)
}

const absent = (markup: string, needle: string, label: string) => {
  if (markup.includes(needle)) throw new Error(`${label}: "${needle}" seharusnya tidak ada di ${markup}`)
}

// HTML mentah dibuang seluruhnya, termasuk isi <script>: tidak ada tag yang bisa lolos ke DOM.
const raw = html('<script>alert(1)</script>\n\nhalo <b>tebal</b> <img src=x onerror=alert(2)>')
absent(raw, '<script', 'tag script')
absent(raw, 'alert(1)', 'isi script')
absent(raw, '<b>', 'tag b')
absent(raw, '<img', 'tag img')
present(raw, 'halo', 'teks di sekitar HTML tetap ada')
present(raw, 'tebal', 'teks di dalam tag tetap ada')

// Tautan berbahaya tidak punya href sama sekali; tautan aman dibuka di tab baru tanpa akses window.opener.
absent(html('[x](javascript:alert(1))'), 'href', 'href javascript')
const link = html('[x](https://contoh.id/halaman)')
present(link, 'href="https://contoh.id/halaman"', 'href https')
present(link, 'target="_blank"', 'target tautan')
present(link, 'rel="noopener noreferrer"', 'rel tautan')

// Gambar tidak dirender (tidak ada permintaan ke host asing); alt-nya tetap terbaca sebagai teks.
const image = html('![grafik penjualan](https://contoh.id/g.png)')
absent(image, '<img', 'tag img markdown')
present(image, 'grafik penjualan', 'alt gambar')

// GFM: tabel benar-benar jadi <table>, dibungkus wadah yang bisa digeser mendatar.
const table = html('| a | b |\n| - | - |\n| 1 | 2 |')
present(table, '<div class="md-table-scroll">', 'wadah tabel')
present(table, '<table>', 'tabel GFM')

// Baris baru tunggal tetap jadi baris baru: jawaban teks biasa tidak menempel jadi satu paragraf.
present(html('baris satu\nbaris dua'), '<br/>', 'baris baru tunggal')

// Blok kode berbahasa dapat kelas highlight dari prism; blok tanpa bahasa tetap polos.
const highlighted = html('```ts\nconst a: number = 1\n```')
present(highlighted, 'prism-code', 'kelas prism')
present(highlighted, 'language-ts', 'kelas bahasa')
present(highlighted, 'class="token', 'token prism')
absent(html('```\nfoo bar\n```'), 'prism-code', 'blok tanpa bahasa')

console.log('markdown.check: semua pemeriksaan lulus')
