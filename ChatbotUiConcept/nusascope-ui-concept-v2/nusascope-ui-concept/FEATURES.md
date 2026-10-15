# NusaScope — Daftar Fitur

Fitur yang ada di UI concept, dikelompokkan per layar. Nama di tanda kurung adalah file di `screens/`, urut sesuai alur pengguna (01 → 04). Tanda **[mockup]** artinya di konsep hanya disimulasikan dan perlu backend sungguhan saat implementasi.

---

## 1. Login (01-login)

- Login dengan **username** dan **password**
- Tombol **tampilkan / sembunyikan password**
- Checkbox **Ingat saya di perangkat ini**
- Tautan **Lupa password?** dan **Daftar**
- Status loading di tombol Masuk ("Memeriksa…") **[mockup]**
- Panel visual berisi kartu model DeepSeek, GLM, dan MiniMax yang melayang
- Responsif: di layar kecil hanya form yang tampil

## 2. Home / sesi baru (02-home)

- Sapaan di halaman kosong ("Selamat siang. Mulai dari mana?")
- **Pilih model AI** lewat kartu: DeepSeek, GLM, MiniMax. Tiap kartu berisi simbol, deskripsi, dan versi model
- **Mode berpikir**: Cepat / Mikir dalam
- Composer dengan lampiran file dan pencarian web
- Chip saran cepat: Ringkas dokumen, Bantu tulis kode, Brainstorm ide, Analisis data
- Sidebar riwayat chat terbaru

## 3. Percakapan (03-chat)

- Bubble pesan user dan jawaban AI dengan nama model + waktu respons
- **Ganti model** dari dropdown di composer: Otomatis, DeepSeek, GLM, MiniMax
- Mode **Otomatis**: sistem memilihkan model sesuai pertanyaan **[mockup]**
- **Bandingkan dengan model lain**: jawab ulang pertanyaan yang sama dengan model berbeda
- Aksi per jawaban: Salin, Jawab ulang, Simpan ke koleksi
- Tombol Bagikan percakapan

## 4. Sesi & tab (04-canvas)

- **Setiap sesi tampil sebagai tab** seperti tab browser
- Tombol **+ Sesi baru** membuat dan mengaktifkan tab baru
- Pindah sesi dengan klik tab
- Tombol **×** di tab memunculkan **pop-up konfirmasi hapus** ("Hapus sesi ini?")
  - Kalau tab aktif dihapus, tab di sebelahnya otomatis aktif
  - Kalau semua tab dihapus, otomatis dibuat satu sesi baru
- Tab bisa di-scroll horizontal kalau jumlahnya banyak
- Judul sesi aktif ikut tampil di header

## 5. Streaming jawaban (04-canvas)

- Teks muncul **kata per kata** dengan efek blur yang pelan-pelan jadi tajam, plus kursor berkedip
- Paragraf di canvas yang sedang ditulis AI diberi **highlight berdenyut**
- Status di chat: "Mengedit *bagian X* di canvas… 42%", lalu centang **"diperbarui · v3"**
- Toolbar AI baru muncul setelah streaming selesai

## 6. Upload gambar (04-canvas)

- Tombol **lampirkan (klip)** dengan menu:
  - Unggah gambar dari perangkat (PNG, JPG, WEBP)
  - Ambil foto / screenshot
- **Drag & drop** ke area chat dengan overlay "Lepas gambar di sini"
- **Paste gambar** dengan Ctrl+V
- Thumbnail pratinjau dengan **progress upload** dan tombol hapus per gambar
- Tombol kirim dinonaktifkan sampai upload selesai
- Setelah terkirim: status "sedang melihat gambar…", lalu aksi cepat **Deskripsikan / Ambil teks (OCR) / Buka di canvas** **[mockup]**

## 7. Mode Canvas (04-canvas)

Chat ada di kiri, ruang kerja di kanan. Canvas punya tab jenis konten:

| Tab | Fitur |
|---|---|
| **Dokumen** | Dokumen rich text dengan judul, sub-judul, dan tabel. **Toolbar AI melayang** saat teks diseleksi: Perpendek, Lebih formal, Tanya… |
| **Spreadsheet** | Grid ala Excel: huruf kolom, nomor baris, **formula bar**, sel aktif, banyak sheet (Anggaran / Proyeksi) |
| **Flowchart** | Render dari Mermaid `flowchart`, latar grid titik, kontrol zoom |
| **Class diagram** | Render dari Mermaid `classDiagram`: atribut, method, multiplicity, komposisi |
| **ERD** | Render dari Mermaid `erDiagram`: notasi crow's foot, penanda PK/FK, tipe kolom, label relasi |
| **Gambar** | Hasil generate gambar (lihat §8) |
| **Kode** | Editor kode dengan nomor baris dan highlight baris yang diubah AI |
| **Preview** | Pratinjau hasil (misalnya halaman web) dalam frame |

Fitur umum canvas:

- Diagram punya toggle **Hasil / Kode Mermaid**. Kode Mermaid bisa disalin, jadi semua output Mermaid bisa ditampilkan
- **Riwayat versi** (v1, v2, v3…)
- Urungkan, Salin, Unduh, Tutup canvas
- Kartu referensi canvas di chat yang bisa diklik untuk membuka canvas

## 8. Generate gambar (04-canvas → tab Gambar)

- Menampilkan **prompt** yang dipakai
- Gambar utama + **4 variasi** yang bisa dipilih
- Info ukuran (1024×1024, dst.) dan model gambar
- **Edit prompt**:
  - Ubah teks prompt
  - Pilih **Gaya**: Flat, 3D, Line art, Cat air
  - Pilih **Rasio**: 1:1, 4:5, 16:9
  - **Generate ulang**, lalu hasil disimpan sebagai **Batch** baru. Batch lama tetap ada di riwayat
- **Buat variasi** dari gambar yang dipilih:
  - Kekuatan perubahan: **Halus / Sedang / Kuat**
  - Opsi pertahankan **Komposisi** / **Warna**
  - Menghasilkan 4 variasi turunan
- Animasi generate: gambar buram yang makin tajam, progres %, dan thumbnail skeleton berkilau
- **Perbesar resolusi** dan **Unduh PNG** **[mockup]**

## 9. Responsif

- Semua layar full-page bisa dipakai di desktop, tablet, dan HP
- Mode canvas: di layar sempit, chat dan canvas ditumpuk
- Tab sesi, tab canvas, dan tabel bisa di-scroll horizontal
