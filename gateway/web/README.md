# GateWay NusaScope — management UI

UI produksi React + TypeScript untuk AI Gateway. Desain A + C dari `../ui-mockup/` diterapkan ke aplikasi ini; API, autentikasi, role guards, dan form tetap memakai backend sungguhan.

## Jalankan dan build

Dari `gateway/web/`:

```powershell
npm ci
npm run dev       # http://localhost:5174; proxy API ke http://localhost:5090
npm run build     # TypeScript + Vite -> ../src/AiGateway.Api/wwwroot
npm run lint
```

Jalankan backend sesuai `../README.md`. Untuk bundle produksi, bangun UI **sebelum** `dotnet build`/`dotnet publish`; ASP.NET Core melayani UI dan API dari origin yang sama. Tidak ada dependensi UI/chart baru atau CDN.

## Tampilan dan navigasi

- Default **Dark**, pilihan **Light/System** di menu avatar; halaman auth mempunyai pilihan tema sendiri. System mengikuti perubahan tema OS. Hanya preferensi tampilan `gw-theme` disimpan di localStorage; access/refresh token tetap di memori.
- Sidebar penuh di desktop, rail otomatis pada lebar 701–1024px yang bisa diperluas manual, dan drawer di mobile. Drawer mengunci focus/background, mendukung Tab/Shift+Tab dan Esc, serta menutup setelah navigasi.
- `Ctrl+K` / `⌘K` membuka pencarian halaman yang diizinkan role pengguna. Arrow/Enter memilih halaman; satu Esc menutup meski query tidak kosong. Ini navigasi, bukan pencarian request-ID.
- Logo sidebar mengikuti tema; sign-in, invite, dan reset selalu memakai **Primary lockup — light** yang rata tengah. Form/judul tetap rata kiri; logo auth mempunyai backing terang dalam tema dark.
- PNG dan Geist/Geist Mono lokal berada di `public/branding/` dan `public/fonts/` (lisensi font disertakan). Source artwork di mockup tidak diubah.
- Dashboard memakai summary API yang sama: metric strip, sparkline harian, grafik SVG Request/Token/Biaya/Error+ditolak, tabel harian, serta share/rincian model. Angka circuit, TTFB, quota, dan delta simulasi mockup tidak ditampilkan sebagai data produksi.
- Table scroll berada di dalam panel; form, badge, dialog native, secret sekali-tampil, skeleton, serta empty/error states memakai token tema yang sama.

## Smoke manual

1. Masuk sebagai platform/owner/admin/viewer. Viewer tidak mempunyai aksi tulis; Pengguna hanya untuk owner, route/palette tetap sadar-peran.
2. Cek auth, resource tenant/platform, dan modal pada lebar 1440/820/390px serta Light/Dark. Cek rail expand/collapse, drawer focus/Esc, dan perubahan OS saat tema System.
3. Dashboard: ubah rentang, tab grafik, tabel, dan rincian model. Cocokkan total dengan summary API; loading tidak menampilkan angka lama, error tidak disamarkan menjadi pemakaian nol.

Hasil verifikasi terbaru dicatat di `../../DECISIONS.md`. Lint masih mempunyai warning lama Fast Refresh dan hook `useLoad`; pekerjaan ini bukan audit/perombakan fetch/auth.

