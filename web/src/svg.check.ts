import { MAX_PNG_PIXELS, MAX_PNG_SIDE, buildGeneratePrompt, buildVariationPrompt, groupBatches, pngDimensions, svgDimensions } from './svg.ts'

// Jalankan: npm run check:svg. Node menjalankan .ts ini langsung; tanpa framework/dependency.

const deep = (actual: unknown, expected: unknown, label: string) => {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(`${label}: ${JSON.stringify(actual)} !== ${JSON.stringify(expected)}`)
}
const ok = (condition: boolean, label: string) => {
  if (!condition) throw new Error(label)
}

// Upscale: skala diturunkan bila melampaui sisi/piksel maksimum.
deep(pngDimensions(1024, 768, 1), { scale: 1, width: 1024, height: 768, capped: false }, '1x apa adanya')
deep(pngDimensions(1024, 768, 4), { scale: 4, width: 4096, height: 3072, capped: false }, '4x dalam batas')
const eight = pngDimensions(1024, 1024, 8)
deep([eight.width, eight.height, eight.capped], [5792, 5792, true], '8x 1024² dibatasi 33,5 MP (5792²)')
const wide = pngDimensions(4000, 100, 8)
deep([wide.width, wide.capped], [MAX_PNG_SIDE, true], 'sisi terpanjang dibatasi 8192')
ok(pngDimensions(3000, 3000, 8).width * pngDimensions(3000, 3000, 8).height <= MAX_PNG_PIXELS, 'piksel tak pernah melebihi batas')
deep(pngDimensions(1, 1, 1).width, 1, 'minimal 1 px')

// Ukuran dasar SVG.
deep(svgDimensions('<svg viewBox="0 0 320.5 200"></svg>')?.width, 321, 'dari viewBox')
const noBox = svgDimensions('<svg width="300px" height="150"><rect/></svg>')
deep([noBox?.width, noBox?.height, noBox?.svg.includes('viewBox="0 0 300 150"')], [300, 150, true], 'tanpa viewBox: dari width/height, viewBox ditambahkan')
deep(svgDimensions('<svg><rect/></svg>'), null, 'tanpa ukuran sama sekali')
deep(svgDimensions('bukan svg'), null, 'bukan svg')

// Prompt generate.
const gen = buildGeneratePrompt({ prompt: '  kucing di atas bulan ', style: 'datar', ratio: '16:9', count: 9 })
ok(gen.includes('Buat 4 gambar'), 'jumlah dibatasi 4')
ok(gen.includes('kucing di atas bulan') && gen.includes('viewBox="0 0 1280 720"') && gen.includes('bukan foto'), 'memuat deskripsi, viewBox rasio, dan batas vektor')
ok(buildGeneratePrompt({ prompt: 'x', style: 'garis', ratio: '1:1', count: 0 }).includes('Buat 1 gambar'), 'minimal 1 gambar')

// Prompt variasi.
const variation = buildVariationPrompt({ title: 'Logo', artifactId: 'abc-1', version: 3, count: 3, strength: 'kuat', keepComposition: true, keepColors: false })
ok(variation.includes('artifact_id=abc-1') && variation.includes('versi=3') && variation.includes('turunan_dari=abc-1') && variation.includes('versi_sumber=3'), 'variasi menyebut sumber, versi, dan turunan_dari')
ok(variation.includes('Pertahankan: komposisi dan tata letak.') && !variation.includes('palet warna'), 'elemen yang dipertahankan sesuai pilihan')
ok(variation.includes('Sumber tidak boleh diubah'), 'sumber tidak diubah')
ok(buildVariationPrompt({ title: 'L', artifactId: 'x', version: 1, count: 1, strength: 'halus', keepComposition: false, keepColors: false }).includes('Tidak ada elemen yang wajib dipertahankan'), 'tanpa pilihan pertahankan')

// Batch riwayat.
const items = [
  { id: 'a', sourceMessageId: 5, createdAt: '2026-01-01T10:00:02Z' },
  { id: 'b', sourceMessageId: 5, createdAt: '2026-01-01T10:00:01Z' },
  { id: 'c', sourceMessageId: 9, createdAt: '2026-01-01T11:00:00Z' },
  { id: 'd', sourceMessageId: null, createdAt: '2026-01-01T09:00:00Z' },
]
const batches = groupBatches(items)
deep(batches.map((b) => b.key), ['pesan-9', 'pesan-5', 'manual'], 'batch urut terbaru dulu')
deep(batches[1].items.map((i) => i.id), ['b', 'a'], 'isi batch urut waktu dibuat')

console.log('svg.check: semua pemeriksaan lulus')
