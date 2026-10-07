# AGENTS.md

Proyek: **Operator AI**, chatbot web dengan AI sebagai operator yang membagi tugas ke worker C# lewat queue di SQL Server.

## Wajib dibaca

- `DESIGN.md` adalah spesifikasi lengkap dan sumber kebenaran. Baca bagian yang relevan sebelum menulis kode.
- `GATEWAY.md` adalah spesifikasi AI Gateway (produk mandiri multi-tenant di `gateway/`). Gateway dikerjakan lebih dulu dari chatbot; fasenya G0-G5 di dokumen itu. Untuk pekerjaan gateway, `GATEWAY.md` menjadi sumber kebenaran.
- `DECISIONS.md` berisi keputusan yang sudah diambil di sesi sebelumnya. Baca seluruhnya di awal sesi; bagian **"Status akhir sesi (handoff)"** di paling bawah berisi kondisi terkini dan langkah berikutnya. Bila belum ada, berarti proyek belum dimulai: kerjakan `SETUP.md` dulu.
- `SETUP.md` berisi langkah membuat kerangka project (awal Fase 0). Semua dokumen proyek berada di root repo; tidak ada folder `docs/`.

## Cara kerja

1. Tanyakan atau tentukan fase yang dikerjakan (lihat `DESIGN.md` bagian 15).
2. Tulis rencana singkat: file yang dibuat/diubah dan urutannya.
3. Implementasikan, lalu jalankan `dotnet build`, `dotnet test`, dan `npm run build` di `web/` bila disentuh.
4. Cek kriteria "Selesai bila" fase tersebut.
5. Tambahkan ringkasan fase dan keputusan penting ke `DECISIONS.md`.

## Batasan

- Stack: .NET 9, ASP.NET Core, Worker Service, EF Core 9 + Dapper, SQL Server, React + Vite + TypeScript.
- AI chatbot: DeepSeek V4.1 Flash lewat endpoint OpenAI-compatible OpenCode (`https://opencode.ai/inference/openai/v1/`, lihat `DECISIONS.md` Fase 0). Setelah gateway selesai, `Ai:BaseUrl` worker diarahkan ke gateway (API key gateway, bukan key provider). Nama model selalu dari konfigurasi `Ai:Model`.
- Tanpa Docker, Firebase, message broker, Redis, atau database lain.
- Secret hanya lewat `dotnet user-secrets`, tidak pernah di file yang di-commit.
- Jangan mengerjakan lebih dari satu fase dalam satu sesi kecuali diminta.
