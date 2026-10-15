import { parseThinkingModes } from './api/session.ts'

const deepStrictEqual = (actual: unknown, expected: unknown, label: string) => {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(label)
}

// Jalankan: npm run check:thinking. Preferensi rusak/asing tidak boleh menjatuhkan chat atau menjadi mode request.
for (const raw of [null, '', '{broken', 'null', 'true', '42', '[]', '["deep"]']) {
  deepStrictEqual(parseThinkingModes(raw), {}, `abaikan snapshot bukan objek: ${raw}`)
}
deepStrictEqual(
  parseThinkingModes('{"conversation-a":"deep","conversation-b":"fast","home":"deep","unknown":"high","missing":null,"nested":{"mode":"deep"}}'),
  { 'conversation-a': 'deep', 'conversation-b': 'fast', home: 'deep' },
  'pertahankan mode yang sah per sesi; abaikan mode dan bentuk yang tidak didukung',
)
console.log('thinkingModes.check: semua pemeriksaan lulus')
