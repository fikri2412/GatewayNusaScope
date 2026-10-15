# AGENTS.md — Instruksi untuk AI coding agent

Kamu akan mengimplementasikan UI **NusaScope** di **React + TypeScript (Vite)**. Paket ini adalah sumber kebenaran desain. Hasil akhirnya harus **terlihat dan berperilaku sama** dengan file di `screens/`.

---

## 1. Urutan membaca (wajib, berurutan)

| Urutan | File | Untuk apa |
|---|---|---|
| 1 | `README.md` | Struktur paket & **alur layar** 01 → 04 |
| 2 | `FEATURES.md` | Checklist fitur. Gunakan sebagai backlog |
| 3 | `IMPLEMENTATION.md` | Library yang dipakai, pemetaan ikon, **daftar komponen** & sumbernya |
| 4 | `assets/tokens.css` | **Semua** nilai warna/font/radius/bayangan/animasi. Import di root app, jangan hardcode ulang |
| 5 | `screens/01-login.html` | Acuan visual layar 1 |
| 6 | `screens/02-home.html` | Acuan visual layar 2 |
| 7 | `screens/03-chat.html` | Acuan visual layar 3 |
| 8 | `screens/04-canvas.html` | Acuan visual layar 4 (paling kompleks, kerjakan terakhir) |

## 2. Urutan pengerjaan yang disarankan

1. **Fondasi**: setup Vite + TS, import `assets/tokens.css`, pasang font, buat komponen dasar (`Button`, `IconButton`, `TextField`, `Chip`, `SegmentedControl`, `ModelGlyph`) dari pola yang berulang di semua layar.
2. **`01-login.html`** → `LoginPage`.
3. **`02-home.html`** → `HomePage` + `ModelCard` + `ThinkingModeToggle` + `Composer`.
4. **`03-chat.html`** → `ChatPage` + `UserBubble` + `AssistantMessage` + `ModelPicker` + `CompareWith`.
5. **`04-canvas.html`** dikerjakan per bagian, dalam urutan ini:
   1. `SessionTabBar` + `DeleteSessionDialog`
   2. Panel chat: `StreamingText`, `EditStatus`, `Composer` + upload (`AttachMenu`, `AttachmentThumb`, `DropOverlay`)
   3. `CanvasPanel` + `CanvasTypeTabs` + `CanvasToolbar`
   4. Isi tab: Dokumen → Spreadsheet → Flowchart / Class / ERD (Mermaid) → Gambar → Kode → Preview
6. **Routing**: `/login` → `/` (home) → `/c/:sessionId` (percakapan + canvas di halaman yang sama).

## 3. Cara membaca file `screens/*.html`

Tiap file terdiri dari empat bagian berurutan:

1. **`<style>`**: CSS halaman (keyframes, hover, breakpoint). Pindahkan ke CSS module atau `tokens.css`.
2. **`function view(v)`**: markup. Ini **padanan JSX**:
   - `${esc(v.x)}` → `{x}`
   - `${(v.list || []).map((item) => `...`).join('')}` → `{list.map(item => ...)}`
   - `${v.cond ? `...` : ''}` → `{cond && ...}`
   - `data-on-click="${on(v.fn)}"` → `onClick={fn}` (begitu juga `data-on-input` → `onChange`, `data-on-drop` → `onDrop`, dst.)
   - `aria-disabled="${...}"` → `disabled={...}`
   - Inline `style="..."` → objek `style` atau class Tailwind dengan **nilai yang sama persis**
3. **`class Component`**: state & handler. Pola class component: `this.state` → `useState`/zustand, `renderVals()` → isi function component, `componentDidMount` → `useEffect`.
4. **Runtime** (`DCLogic`, `render`, event delegation, Idiomorph): **jangan ditiru**. Bagian ini hanya membuat file bisa jalan tanpa React.

`window.__app.state` bisa dibaca di console browser untuk melihat state saat ini.

## 4. Verifikasi visual (lakukan untuk setiap layar)

1. Buka `screens/0X-*.html` dan halaman React yang sesuai di browser dengan ukuran sama (1280×820 dan 390×844 untuk HP).
2. Ambil screenshot keduanya (misalnya dengan Playwright) dan bandingkan.
3. Cek juga state setelah interaksi. Contoh di `04-canvas.html`: dialog hapus sesi, panel Edit prompt, panel Buat variasi, overlay drag & drop, dan toggle Kode Mermaid.

## 5. Aturan

- **Jangan mengubah desain**: warna, jarak, radius, font, dan teks UI harus mengikuti `screens/` dan `tokens.css`.
- Ikon pakai **lucide-react** sesuai tabel di `IMPLEMENTATION.md` §4, kecuali logo dan glyph model yang memakai `assets/*.svg`.
- **Jangan memakai logo resmi** DeepSeek, GLM, atau MiniMax. Pakai `assets/glyph-*.svg`.
- Placeholder (`[versi model]`, `[Rp ...]`, `[model gambar]`, `[NAMA KAMPUS]`) diisi dari data/konfigurasi, bukan di-hardcode.
- Data contoh (judul sesi, ide nama kopi, ERD kedai kopi) hanya contoh. Ganti dengan data dari API.
- Pertahankan aksesibilitas yang sudah ada: `aria-label` di tombol ikon, `role="tab"`, `role="alertdialog"`, dan `prefers-reduced-motion`.
- Fitur yang ditandai **[mockup]** di `FEATURES.md` butuh endpoint backend. Buat interface/service-nya dan beri TODO bila endpoint belum ada.
