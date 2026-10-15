# NusaScope — web chatbot

React + TypeScript + Vite; desain dari `ChatbotUiConcept/nusascope-ui-concept-v2/nusascope-ui-concept`. Panduan runtime/API/worker: [`../README.md`](../README.md). Checklist fitur konsep yang belum tersedia: [`../IMPROVEMENTS.md` §17](../IMPROVEMENTS.md#17-desain-web-chatbot-nusascope-dan-backlog-konsep).

## Jalankan dan periksa

```powershell
npm install
npm run dev                 # http://localhost:5175, strictPort
npm run build
npm run lint
npm run check:attachments
npm run check:drafts
npm run check:thinking
```

`VITE_API_URL` berasal dari `.env.development` (IIS Express) atau environment proses. `scripts/dev.ps1` di root menimpanya ke API lokal `:5080`. Origin web harus sesuai CORS API; perubahan UI ini tidak mengubah port, origin, akun atau secret.

## Permukaan yang tersedia

- Login/daftar nyata, toggle password, busy/error; sesi tetap `sessionStorage`, logout/401 membuang token.
- Home: pilih alias katalog gateway; kartu dapat digeser jika banyak model. Tidak ada default model atau Automatic palsu. Chip saran hanya mengisi draft.
- Riwayat sidebar dan tab sesi; ArrowLeft/ArrowRight/Home/End memilih tab. Sesi baru dibuat pada unggah/kirim pertama. Belum ada hapus atau bagikan sesi.
- Chat dan SignalR streaming nyata, draft/canonical, alias per pesan, copy jawaban, job aktif, token/truncation warning.
- Pilihan **Cepat / Mikir dalam** mengikuti percakapan, bukan umur komponen Composer. Mode Home dipindahkan ke sesi baru sesudah pesan diterima; kiriman gagal mempertahankan pilihan. Preferensi per akun/sesi disimpan di `sessionStorage` tab ini dan dipulihkan saat pindah sesi atau reload. Sesi tanpa preferensi memakai Cepat; belum ada sinkronisasi mode lintas tab/perangkat.
- Picker/drop/paste file memakai policy upload yang sama. Format/byte/count berasal dari API; PNG/JPEG memerlukan vision terkonfirmasi. Busy upload nyata, tanpa progress persen buatan. Gambar ditampilkan lewat fetch JWT + blob; dialog native dengan Esc dan fokus kembali.
- Desain light dari konsep, responsif, fokus keyboard dan reduced motion. Editor Canvas, Mermaid, compare/regenerate, web search dan generate gambar belum ada; lihat backlog, bukan tombol dummy. Persistensi pilihan mode UI tidak membuktikan dukungan parameter reasoning tiap provider/model.

## Aset dan implementasi

`src/tokens.css` memakai nilai konsep; `src/index.css` menggantikan stylesheet lama. Login dan composer punya stylesheet scoped. Logo/glyph konsep ada di `public/brand`; ikon garis digambar native SVG tanpa paket tambahan. Geist/Geist Mono + lisensi di `public/fonts` reuse aset gateway, tanpa CDN. Newsreader belum diperlukan karena editor dokumen belum tersedia.

API, hook SignalR, validasi lampiran/model, JWT download dan snapshot server tetap sumber kebenaran; data HTML contoh tidak disalin menjadi data produk. Backend tidak diubah oleh pemasangan desain.
