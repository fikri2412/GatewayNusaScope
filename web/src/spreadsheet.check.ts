import { STARTER_CONTENT, csvCell, parseSheets, serializeSheets, sheetToCsv } from './spreadsheet.ts'

// Jalankan: npm run check:spreadsheet. Node menjalankan .ts ini langsung; tanpa framework/dependency.

const deep = (actual: unknown, expected: unknown, label: string) => {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(`${label}: ${JSON.stringify(actual)} !== ${JSON.stringify(expected)}`)
}

// parse: hanya array sheet bernama yang diterima.
deep(parseSheets(STARTER_CONTENT)?.[0].name, 'Sheet1', 'isi awal terbaca')
deep(parseSheets('bukan json'), null, 'bukan JSON')
deep(parseSheets('{"name":"x"}'), null, 'objek bukan array')
deep(parseSheets('[]'), null, 'array kosong')
deep(parseSheets('[{"order":1}]'), null, 'tanpa nama')
deep(parseSheets('[1,2]'), null, 'elemen bukan objek')

// serialize: status tampilan dibuang, sel diambil dari matriks `data`.
const toCelldata = (data: unknown[][]) => data.flatMap((row, r) => row.map((v, c) => (v ? { r, c, v } : null)).filter((x) => x !== null)) as never
const out = JSON.parse(
  serializeSheets(
    [{
      name: 'A', id: 'a', order: 0, status: 1, luckysheet_select_save: [{ row: [0, 0], column: [0, 0] }],
      data: [[{ v: 1, m: '1' }, null], [null, { v: 2, m: '2' }]],
    } as never],
    toCelldata,
  ),
)
deep(Object.keys(out[0]).sort(), ['celldata', 'id', 'name', 'order'], 'status/seleksi tidak ikut tersimpan')
deep(out[0].celldata, [{ r: 0, c: 0, v: { v: 1, m: '1' } }, { r: 1, c: 1, v: { v: 2, m: '2' } }], 'sel padat dari matriks')
deep(JSON.parse(serializeSheets([{ name: 'B', celldata: [{ r: 0, c: 0, v: { v: 'x' } }] } as never], toCelldata))[0].celldata.length, 1, 'tanpa data memakai celldata')
// kanonik: sama masukan → sama keluaran (dasar deteksi "tidak berubah").
const same = [{ name: 'A', data: [[{ v: 1, m: '1' }]] } as never]
deep(serializeSheets(same, toCelldata) === serializeSheets(same, toCelldata), true, 'serialisasi deterministik')

// CSV.
deep(csvCell('biasa'), 'biasa', 'teks biasa')
deep(csvCell('a,b'), '"a,b"', 'koma dikutip')
deep(csvCell('kata "kutip"'), '"kata ""kutip"""', 'kutip digandakan')
deep(csvCell('baris\nbaru'), '"baris\nbaru"', 'newline dikutip')
deep(csvCell('=cmd|calc'), "'=cmd|calc", 'awalan rumus dinetralkan')
deep(csvCell('@SUM(A1)'), "'@SUM(A1)", 'awalan @ dinetralkan')
deep(csvCell('-2+3'), '-2+3', 'angka negatif tidak diubah')
deep(csvCell('-cmd'), "'-cmd", 'minus + huruf dinetralkan')
deep(csvCell(-5), '-5', 'angka negatif bertipe number utuh')
deep(csvCell(null), '', 'null kosong')
deep(sheetToCsv([{ r: 0, c: 0, v: { v: 'Item', m: 'Item' } }, { r: 0, c: 1, v: { v: 'Total' } }, { r: 1, c: 1, v: { v: 20000, m: '20,000' } }] as never), 'Item,Total\r\n,"20,000"', 'CSV memakai nilai tampilan, sel kosong dilewati')
deep(sheetToCsv([]), '', 'sheet kosong')

console.log('spreadsheet.check: semua pemeriksaan lulus')
