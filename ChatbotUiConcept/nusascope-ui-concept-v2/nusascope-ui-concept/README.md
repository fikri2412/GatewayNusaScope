# NusaScope — UI Concept (v2)

Desain UI untuk **NusaScope**, web chatbot AI multi-model (DeepSeek, GLM, MiniMax) dengan mode **Canvas**. Canvas bisa menampilkan dokumen, spreadsheet, diagram Mermaid (flowchart, class diagram, ERD), gambar, kode, dan preview.

Semua layar ada dalam bentuk **HTML standalone**: cukup double-click untuk membuka di browser, tanpa install apa pun. Font dimuat dari Google Fonts, jadi butuh internet.

---

## Struktur folder

```
nusascope-ui-concept/
├── README.md            ← mulai di sini
├── AGENTS.md            ← instruksi untuk AI coding agent (OpenCode, Claude Code, dll.)
├── FEATURES.md          ← daftar fitur per layar
├── IMPLEMENTATION.md    ← token, aset, komponen & library untuk implementasi React
├── assets/
│   ├── tokens.css       ← warna, font, radius, bayangan, animasi (CSS variables)
│   ├── logo-nusascope.svg
│   ├── glyph-deepseek.svg
│   ├── glyph-glm.svg
│   ├── glyph-minimax.svg
│   └── glyph-auto.svg
└── screens/             ← 4 layar, urut sesuai alur pengguna
    ├── 01-login.html
    ├── 02-home.html
    ├── 03-chat.html
    └── 04-canvas.html
```

---

## Alur layar (urutan pengguna)

```
 01-login.html ──Masuk──▶ 02-home.html ──Kirim / chip saran──▶ 03-chat.html ──Kirim──▶ 04-canvas.html
   (login)                (pilih model)                          (percakapan)            (layar kerja utama)
```

| # | File | Layar | Isi utama | Lanjut ke |
|---|---|---|---|---|
| 1 | `screens/01-login.html` | Login | Form username & password, tampilkan/sembunyikan password, ingat saya, panel visual model | Tombol **Masuk**, lalu loading, lalu pindah ke **02** |
| 2 | `screens/02-home.html` | Home / sesi baru | Sidebar riwayat, kartu pilih model (DeepSeek / GLM / MiniMax), mode berpikir Cepat / Mikir dalam, composer, chip saran | Tombol **Kirim** atau salah satu **chip saran** ke **03** |
| 3 | `screens/03-chat.html` | Percakapan | Bubble user & AI, nama model + waktu respons, "Bandingkan dengan", dropdown ganti model (ditampilkan dalam keadaan terbuka) | Tombol **Kirim** ke **04** |
| 4 | `screens/04-canvas.html` | Mode Canvas | Tab sesi ala browser, chat + upload gambar, canvas 8 jenis konten, streaming, generate & variasi gambar | Layar akhir. Semua interaksi ada di sini |

> Di aplikasi asli, setelah login user masuk ke **Home**. Layar **Percakapan** dan **Canvas** sebenarnya satu halaman yang sama: canvas terbuka di kanan saat AI membuat dokumen, diagram, atau gambar. Di konsep ini keduanya dipisah supaya tiap state mudah dilihat.

---

## Urutan membaca dokumen

1. **README.md**: gambaran umum dan alur layar (file ini)
2. **FEATURES.md**: apa saja yang harus dibuat
3. **IMPLEMENTATION.md**: bagaimana membuatnya di React (library, token, komponen)
4. **assets/tokens.css**: nilai style yang dipakai di semua layar
5. **screens/01 → 04**: acuan visual dan interaksi, dibuka sesuai urutan
6. **AGENTS.md**: kalau pengerjaannya dibantu AI agent

---

## Mencoba interaksi di `04-canvas.html`

- **Tab sesi (atas):** klik tab, **+ Sesi baru**, lalu × untuk memunculkan konfirmasi hapus
- **Chat (kiri):** animasi streaming otomatis berulang. 📎 untuk unggah gambar, bisa juga drag & drop atau Ctrl+V. Klik kirim setelah upload selesai
- **Canvas (kanan):** buka tab Dokumen, Spreadsheet, Flowchart, Class diagram, ERD, Gambar, Kode, dan Preview
  - Diagram: toggle **Hasil / Kode Mermaid**
  - Gambar: **Edit prompt**, lalu **Generate ulang**. Atau pilih thumbnail, lalu **Buat variasi**

## Catatan teknis HTML standalone

Tiap file berisi markup, inline style, dan logika yang sama persis dengan konsep di kanvas desain. Strukturnya ada empat bagian (lihat komentar di bagian atas tiap file):

1. `<style>`: style halaman
2. `view(v)`: markup dalam template string. `${...}` = nilai dari `renderVals()`, sedangkan `.map()` / `? :` untuk perulangan dan kondisi
3. `class Component`: state & handler dengan pola **React class component** (`state`, `setState`, `renderVals`, `componentDidMount`)
4. Runtime kecil yang me-render ulang DOM (memakai Idiomorph, lisensi 0BSD, sudah tertanam di file). Bagian ini **bukan bagian desain** dan tidak perlu ditiru di React

Isi placeholder seperti `[versi model]`, `[Rp ...]`, dan `[model gambar]` sengaja dibiarkan kosong untuk diisi data asli.
