# TESTING.md

## Aturan: jangan jalankan unit test di agent cloud

Unit test (xUnit) **tidak dijalankan** oleh agent cloud (Claude Code web/remote). Pengujian dilakukan di agent lokal pemilik repo.

Untuk agent cloud:

- **Jangan** menjalankan `dotnet test` (solution maupun per project), `dotnet vstest`, atau perintah lain yang mengeksekusi project test xUnit.
- Tetap boleh `dotnet build` dan `npm run build` di `web/` untuk memastikan kode terkompilasi.
- Tetap boleh **menulis atau mengubah** kode test bila tugas memintanya; hanya eksekusinya yang ditunda ke lokal.
- Langkah "jalankan `dotnet test`" di `AGENTS.md` dan kriteria "Selesai bila" yang bergantung pada hasil test dianggap **ditunda ke agent lokal**. Jangan klaim test lulus.
- Di ringkasan akhir dan `DECISIONS.md`, tulis jelas: "unit test belum dijalankan (dijalankan di agent lokal)".
- Jangan mengubah, melewati, atau menghapus test agar build/CI hijau.

Untuk agent lokal: jalankan `dotnet test` penuh sebelum fase dinyatakan selesai.
