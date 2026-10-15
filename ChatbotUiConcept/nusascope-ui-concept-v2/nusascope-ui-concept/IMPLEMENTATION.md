# NusaScope — Panduan Implementasi React

Dokumen ini menjelaskan **dari mana setiap aset, komponen, dan library berasal**, supaya hasil implementasi di React sama dengan `screens/01-login.html` → `02-home.html` → `03-chat.html` → `04-canvas.html`.

> Versi library tidak saya tulis karena cepat berubah. Pakai versi stabil terbaru, lalu cek dokumentasi masing-masing.

---

## 1. Stack yang disarankan

| Kebutuhan | Library | Catatan |
|---|---|---|
| Framework | **React + TypeScript** (Vite) | Sudah sesuai stack frontend-mu |
| Styling | **Tailwind CSS** *atau* CSS biasa + `assets/tokens.css` | Token sudah dalam bentuk CSS variables, jadi bisa dipakai dengan keduanya |
| Primitif UI aksesibel | **Radix UI** (`@radix-ui/react-*`) | Dialog, AlertDialog, DropdownMenu, Popover, Tabs, ToggleGroup, Tooltip. Tanpa style bawaan, jadi tampilan 100% mengikuti konsep |
| Ikon | **lucide-react** | Gaya garis (stroke 2px, ujung bulat) sama dengan ikon di konsep. Lihat tabel di §4 |
| State global | **zustand** | Sesi/tab, model aktif, lampiran, mode canvas |
| Data/server state | **TanStack Query** | Daftar sesi, riwayat chat |
| Streaming AI | `fetch` + `ReadableStream` / SSE (`eventsource-parser`) | Backend ASP.NET Core bisa kirim SSE (`text/event-stream`) |
| Render markdown jawaban AI | **react-markdown** + **remark-gfm** | Tabel, list, dan code block di bubble chat |
| Syntax highlight | **shiki** *atau* **prism-react-renderer** | Warna tema ada di token `--ns-code-*` |
| Editor dokumen (tab Dokumen) | **TipTap** | Ekstensi `BubbleMenu` cocok untuk toolbar AI melayang (Perpendek / Lebih formal / Tanya…) |
| Editor kode (tab Kode) | **@monaco-editor/react** *atau* **@uiw/react-codemirror** | Monaco lebih lengkap, CodeMirror lebih ringan |
| Diagram (Flowchart, Class, ERD) | **mermaid** | AI menghasilkan kode Mermaid, lalu dirender jadi SVG. Lihat konfigurasi tema di §6 |
| Spreadsheet | **AG Grid Community** *atau* **react-data-grid** + **SheetJS (`xlsx`)** | SheetJS untuk impor/ekspor `.xlsx`. Kalau pakai Handsontable, cek lisensinya untuk pemakaian komersial |
| Upload gambar | **react-dropzone** | Drag & drop + file picker. Paste (Ctrl+V) ditangani manual lewat `onPaste` |
| Animasi | **CSS di `tokens.css`** (cukup) | Opsional `motion` (Framer Motion) kalau mau transisi layout yang lebih kompleks |
| Diagram interaktif (opsional) | **@xyflow/react** (React Flow) | Hanya kalau nanti user perlu drag node di flowchart |

---

## 2. Aset

| Aset | Sumber | Cara pakai |
|---|---|---|
| Font **Geist** (UI) & **Geist Mono** (angka, kode, versi model) | Google Fonts, sudah di-`@import` di `tokens.css`. Alternatif: paket npm `geist` | `font-family: var(--ns-font-sans)` / `var(--ns-font-mono)` |
| Font **Newsreader** (judul dokumen di canvas) | Google Fonts, sudah di-`@import` | Hanya untuk `h1` dokumen & preview |
| Logo NusaScope | `assets/logo-nusascope.svg` | Header, tab bar, login |
| Simbol model | `assets/glyph-*.svg` | Kartu model, dropdown, header jawaban AI, tab sesi |
| Ilustrasi gambar contoh | Ilustrasi vektor di `Canvas2.dc.html` (cangkir kopi) | **Placeholder saja**. Ganti dengan `<img src={url}>` dari hasil model gambar |

> **Logo resmi DeepSeek / GLM / MiniMax tidak dipakai.** Simbol di atas adalah glyph buatan NusaScope. Kalau mau memakai logo resmi, cek dulu brand guideline tiap penyedia.

---

## 3. Design tokens (ringkas)

Semua ada di `assets/tokens.css`. Yang paling sering dipakai:

| Token | Nilai | Dipakai untuk |
|---|---|---|
| `--ns-bg` | `#F7F6F3` | Latar halaman |
| `--ns-surface` | `#FFFFFF` | Kartu, input, popover |
| `--ns-muted` | `#EDEBE6` | Bubble user, segmented control |
| `--ns-border` | `#E4E2DC` | Semua border 1px |
| `--ns-text` / `--ns-text-muted` | `#1A1A1D` / `#6B6A70` | Teks utama / sekunder |
| `--ns-primary` | `#1A1A1D` | Tombol utama (Kirim, Masuk, Unduh) |
| `--ns-accent` / `--ns-accent-tint` | `#0F5F57` / `#EAF3F1` | Fokus, terpilih, progres, centang |
| `--ns-danger` | `#B42318` | Tombol Hapus sesi |
| Model | DeepSeek `#0F5F57`, GLM `#3D3F8F`, MiniMax `#B45F2E` | Glyph + tint kartu terpilih |
| Radius | 8 / 10 / 12 / 14 / 16 / 20 / pill | Tombol 10–12, kartu 14–16, composer 16–20 |
| Bayangan | `--ns-shadow-composer`, `-popover`, `-modal` | Composer, dropdown, dialog |

**Pola fokus input:** border `--ns-accent` + `box-shadow: var(--ns-ring-focus)`. Panel "edit prompt" dan "buat variasi" juga memakai pola ini.

### Tailwind (opsional)

```js
// tailwind.config.js → theme.extend
colors: {
  bg: "var(--ns-bg)", surface: "var(--ns-surface)", muted: "var(--ns-muted)",
  line: "var(--ns-border)", ink: "var(--ns-text)", "ink-muted": "var(--ns-text-muted)",
  accent: { DEFAULT: "var(--ns-accent)", tint: "var(--ns-accent-tint)" },
  danger: { DEFAULT: "var(--ns-danger)", tint: "var(--ns-danger-tint)" },
  deepseek: "var(--ns-model-deepseek)", glm: "var(--ns-model-glm)", minimax: "var(--ns-model-minimax)",
},
fontFamily: { sans: ["Geist", "system-ui", "sans-serif"], mono: ["Geist Mono", "monospace"], serif: ["Newsreader", "serif"] },
boxShadow: { composer: "var(--ns-shadow-composer)", popover: "var(--ns-shadow-popover)", modal: "var(--ns-shadow-modal)" },
```

---

## 4. Pemetaan ikon → `lucide-react`

Ikon di konsep digambar manual dengan gaya yang sama seperti Lucide. Padanannya:

| Di konsep | lucide-react |
|---|---|
| Kirim (panah atas) | `ArrowUp` |
| Lampirkan | `Paperclip` |
| Tambah / sesi baru | `Plus` |
| Tutup / hapus lampiran | `X` |
| Salin | `Copy` |
| Unduh | `Download` |
| Urungkan | `Undo2` |
| Jawab ulang | `RotateCw` |
| Cari di web | `Globe` |
| Riwayat | `Clock` |
| Koleksi / simpan | `Bookmark` |
| Edit / mengedit | `PenLine` |
| Hapus sesi | `Trash2` |
| Lihat / sembunyikan password | `Eye` / `EyeOff` |
| Username / password | `User` / `Lock` |
| Unggah gambar / kamera / drop | `Image` / `Camera` / `Upload` |
| Centang | `Check` (di dalam lingkaran `--ns-accent`) |
| Dropdown | `ChevronDown` / `ChevronUp` |
| Mode otomatis | `Sparkles` atau `assets/glyph-auto.svg` |

Pengaturan default: `size={18} strokeWidth={2}`. Ikon kecil di tab atau chip pakai `size={12–14} strokeWidth={2.6}`.

---

## 5. Komponen (dari mana & isinya apa)

Kolom **Sumber** menunjuk file di `screens/` yang bisa dibuka di browser untuk melihat ukuran, style, dan interaksi persisnya.

### Layout & navigasi

| Komponen | Sumber | Primitif / library | Catatan |
|---|---|---|---|
| `SessionTabBar` | 04-canvas (atas) | Radix `Tabs` | Latar `--ns-tabstrip`. Tab aktif berwarna `--ns-bg` dengan radius `10px 10px 0 0`. Lebar tab 128–220px, judul terpotong dengan ellipsis. Bar bisa di-scroll horizontal |
| `SessionTab` | 04-canvas | — | Isinya titik warna model, judul, dan tombol × (24px). Muncul dengan animasi `.ns-tab` |
| `NewSessionButton` | 04-canvas | — | Tombol hitam "+ Sesi baru" di ujung kanan. Di ≤560px menyusut jadi ikon saja |
| `DeleteSessionDialog` | 04-canvas | Radix `AlertDialog` | Ikon tempat sampah di kotak `--ns-danger-tint`, judul, nama sesi, tombol Batal / Hapus sesi |
| `Sidebar` (riwayat) | 02-home | — | Lebar 260px. Di ≤720px pindah ke atas dan daftar riwayat disembunyikan |
| `AppHeader` | 03-chat, 04-canvas | — | Logo, nama sesi, tombol Bagikan |

### Pemilihan model

| Komponen | Sumber | Primitif | Catatan |
|---|---|---|---|
| `ModelCard` | 02-home | Radix `ToggleGroup` (single) | Kartu terpilih: tint warna model + border 1.5px warna model + ikon centang. Saat hover, kartu naik 2px dan glyph berputar 45° |
| `ModelGlyph` | `assets/glyph-*.svg` | — | Ukuran: 32px di kartu, 22px di dropdown, 14–20px di chip |
| `ModelPicker` (dropdown) | 03-chat | Radix `DropdownMenu` / `Popover` | Urutan: Otomatis, pemisah, lalu daftar model. Item terpilih diberi latar tint dan centang. Versi model ditulis dengan Geist Mono |
| `ModelChip` (di composer) | 03-chat, 04-canvas | trigger `ModelPicker` | Latar `--ns-surface-2`, berisi glyph + nama + chevron |
| `ThinkingModeToggle` | 02-home | Radix `ToggleGroup` | Pilihan "Cepat" / "Mikir dalam" |
| `CompareWith` | 03-chat | — | "Bandingkan dengan" + chip model lain untuk jawab ulang pakai model berbeda |

### Chat

| Komponen | Sumber | Catatan |
|---|---|---|
| `UserBubble` | 03-chat, 04-canvas | Latar `--ns-muted`, radius `18px 18px 4px 18px`, maks 80–88% lebar |
| `AssistantMessage` | 03-chat, 04-canvas | Header berisi glyph, nama model, dan durasi (mono). Body dirender dengan `react-markdown`. Di bawahnya ada aksi Salin / Jawab ulang |
| `StreamingText` | 04-canvas | Tiap token dibungkus `<span className="ns-word">`, ditambah `<span className="ns-caret"/>` selama stream masih berjalan |
| `CanvasRefCard` | 04-canvas | Kartu "Proposal … · v2 · terbuka di canvas" yang membuka canvas saat diklik |
| `EditStatus` | 04-canvas | Spinner + "Mengedit *Ringkasan*… 42%", lalu berubah jadi centang `.ns-pop` + "diperbarui · v3" |
| `QuickActions` | 04-canvas | Chip pill di bawah jawaban, misalnya Deskripsikan / OCR / Buka di canvas |
| `Composer` | 02-home, 03-chat, 04-canvas | Latar putih, border `--ns-border`, radius 16–20, `--ns-shadow-composer`. Saat ada lampiran, border berubah 1.5px `--ns-accent` |
| `AttachMenu` | 04-canvas | Radix `DropdownMenu`, muncul di atas tombol klip |
| `AttachmentThumb` | 04-canvas | Ukuran 64×64. Selama upload: `brightness(.75) blur(1px)` + progress bar 4px. Ada tombol × bulat gelap di pojok |
| `DropOverlay` | 04-canvas | Garis putus-putus 2px `--ns-accent` di atas latar tint 94% |

### Canvas

| Komponen | Sumber | Library | Catatan |
|---|---|---|---|
| `CanvasPanel` | 04-canvas | — | Panel kanan dengan latar `--ns-canvas-bg`. Di ≤860px ditumpuk di bawah chat |
| `CanvasTypeTabs` | 04-canvas | Radix `Tabs` | Dokumen, Spreadsheet, Flowchart, Class diagram, ERD, Gambar, Kode, Preview. Bisa di-scroll horizontal |
| `CanvasToolbar` | 04-canvas | — | Pemilih versi (`v2`), Urungkan, Salin, Unduh, Tutup |
| `DocumentView` | 04-canvas | TipTap | Kertas putih maks 720px, padding 48/56. `h1` memakai Newsreader 36px |
| `AiBubbleToolbar` | 04-canvas | TipTap `BubbleMenu` | Latar gelap `#1A1A1D`, radius 12, berisi label "AI" + Perpendek / Lebih formal / Tanya… |
| `SheetView` | 04-canvas | AG Grid / react-data-grid | Formula bar (sel + `fx` + rumus mono), header kolom A–D, sel aktif dengan outline 2px accent, tab sheet di bawah |
| `MermaidView` | 04-canvas | mermaid | Latar `.ns-dot-grid`, kontrol zoom − 100% +, toggle **Hasil / Kode Mermaid** |
| `MermaidSource` | 04-canvas | shiki | Panel gelap berisi `diagram.mmd` dan tombol Salin |
| `ImageResultView` | 04-canvas | — | Bar prompt, gambar utama, 4 thumbnail, aksi Buat variasi / Perbesar resolusi / Unduh PNG |
| `PromptEditor` | 04-canvas | — | Textarea + chip Gaya + chip Rasio + Batal / Generate ulang |
| `VariationPanel` | 04-canvas | — | Gambar sumber + pilihan Halus/Sedang/Kuat + checkbox Pertahankan + "Buat 4 variasi" |
| `GeneratingOverlay` | 04-canvas | — | Gambar utama `blur(18px→0)` mengikuti progres + kartu progres. Thumbnail diganti `.ns-shim` |
| `CodeView` | 04-canvas | Monaco / CodeMirror | Panel gelap, nomor baris, baris yang diubah AI di-highlight `--ns-code-highlight` |
| `PreviewView` | 04-canvas | `<iframe sandbox>` | Frame dengan tiga titik di header |

### Auth

| Komponen | Sumber | Catatan |
|---|---|---|
| `LoginPage` | 01-login | Form di kiri (maks 380px) dan panel gelap di kanan. Panel kanan disembunyikan di ≤860px |
| `TextField` | 01-login | Tinggi 48, radius 12, ikon di kiri. Fokus: border accent + ring |
| `PasswordField` | 01-login | Tombol Eye/EyeOff dengan `aria-label` dinamis |
| `FloatingModelCards` | 01-login | Tiga kartu dengan `.ns-bob-1/2/3` |

---

## 6. Konfigurasi Mermaid supaya sesuai tema

```ts
import mermaid from "mermaid";

mermaid.initialize({
  startOnLoad: false,
  theme: "base",
  fontFamily: "Geist, system-ui, sans-serif",
  themeVariables: {
    background: "#FFFFFF",
    primaryColor: "#FFFFFF",
    primaryBorderColor: "#1A1A1D",
    primaryTextColor: "#1A1A1D",
    lineColor: "#55545A",
    secondaryColor: "#EAF3F1",
    tertiaryColor: "#FBF1EA",
    fontSize: "13px",
  },
  flowchart: { curve: "basis", padding: 16 },
  er: { useMaxWidth: true },
});

const { svg } = await mermaid.render(`m-${id}`, code);
```

Untuk node keputusan berwarna terakota dan langkah aktif berwarna teal seperti di konsep, gunakan `classDef` di kode Mermaid yang dihasilkan AI.

---

## 7. Pola streaming (setara efek di konsep)

```tsx
function StreamingText({ tokens, done }: { tokens: string[]; done: boolean }) {
  return (
    <>
      {tokens.map((t, i) => <span key={i} className="ns-word">{t}</span>)}
      {!done && <span className="ns-caret" aria-hidden />}
    </>
  );
}
```

`key` harus stabil (indeks token), supaya hanya token baru yang menjalankan animasi.

---

## 8. Breakpoint responsif

| Lebar | Perubahan |
|---|---|
| ≤ 860px | Mode canvas: chat dan canvas ditumpuk. Login: panel kanan disembunyikan |
| ≤ 720px | Home: sidebar pindah ke atas, daftar riwayat disembunyikan |
| ≤ 640px | Percakapan: grid kartu jawaban jadi 1 kolom |
| ≤ 560px | Tombol "Sesi baru" jadi ikon saja |

Target sentuh minimal 44px untuk tombol utama, sementara tombol ikon kecil di toolbar berukuran 32–36px.

---

## 9. Aksesibilitas yang sudah ada di konsep (pertahankan)

- Semua tombol ikon punya `aria-label`, misalnya "Tutup sesi …", "Hapus screenshot-1.png", atau "Tampilkan password".
- Tab memakai `role="tab"` + `aria-selected`. Pilihan model memakai `aria-pressed`.
- Dialog hapus memakai `role="alertdialog"` + `aria-modal`. Radix AlertDialog sudah menangani focus trap dan Esc.
- Animasi dimatikan saat `prefers-reduced-motion` (sudah ada di `tokens.css`).
