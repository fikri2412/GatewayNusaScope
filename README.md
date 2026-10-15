# GatewayNusaScope

Dua produk dalam satu repo:

- **`gateway/`** — AI Gateway multi-tenant (produk mandiri). Panduan: [`gateway/README.md`](gateway/README.md).
- **Operator AI** (`src/`, `web/`, `tests/`) — chatbot web; AI sebagai operator yang membagi tugas ke worker C# lewat queue di SQL Server. Spesifikasi: [`DESIGN.md`](DESIGN.md); keputusan dan status: [`DECISIONS.md`](DECISIONS.md).
- **Improvement Fase 6** — [`IMPROVEMENTS.md`](IMPROVEMENTS.md): 6A1 (runtime/prompt/monitoring), 6A2 (katalog + selector model dinamis, snapshot workflow), **6B (streaming jawaban ke browser)**, dan **6C (analisis dokumen: PDF teks/scan/campuran serta PNG/JPG/JPEG/WEBP lewat vision)** sudah diimplementasikan; acceptance/sisa verifikasi 6C berjalan di lingkungan terisolasi dan dicatat di [`IMPROVEMENTS.md` §15](IMPROVEMENTS.md#15-hasil-implementasi-dan-verifikasi-6c). **6D (PRD/Mermaid → PNG) belum dibuat**; bug gateway yang ditunda tetap di luar scope.
- **Kelompok A & Kelompok B** — [`IMPROVEMENTS.md` §17](IMPROVEMENTS.md#17-desain-web-chatbot-nusascope-dan-backlog-konsep): Kelompok A (refresh sesi + atur ulang password, hapus sesi, koleksi jawaban, jawaban alternatif Jawab ulang/Bandingkan, ekspor `.md` lokal) dan Kelompok B (mode berpikir Cepat/Mikir dalam, otomatis pilih model, pencarian web lewat gateway, upload WEBP, ambil foto/screenshot, progres upload byte persentase, serta aksi gambar Deskripsikan/OCR) telah diimplementasikan dan diverifikasi.
- **Gateway GA-01–GA-04** — migration GA-03, backend dan UI sudah diterapkan ke gateway utama pada 2026-10-14 (IIS Express `https://localhost:44387`, LocalDB `AiGateway`). SQL idempotent: [`gateway/sql/ga03-usage-completeness.sql`](gateway/sql/ga03-usage-completeness.sql); bukti/batas rollout: [`IMPROVEMENTS.md` §18.6](IMPROVEMENTS.md#186-rollout-utama-gateway-ga-01ga-04-2026-10-14). Rollout ini tidak menerapkan upgrade chatbot.
- **Web Search Provider Hub** — gateway menyimpan key Tavily/Exa/Firecrawl terenkripsi; chatbot hanya memakai key gateway melalui `cari_web`/`fetch_url`. Registrasi, upgrade dan batas biaya/fallback: [`gateway/README.md` §12](gateway/README.md#12-web-search-provider-hub). Seed memerlukan tenant eksplisit, bukan key provider pada worker.

## Menjalankan Operator AI (pengembangan)

Prasyarat: .NET 9 SDK, Node.js, SQL Server LocalDB (`(localdb)\MSSQLLocalDB`), `dotnet-ef`, dan **gateway yang hidup di `http://localhost:5090`** dengan satu key klien `gw_...` (lihat `gateway/README.md`; chatbot memanggil AI hanya lewat gateway).

### 1. Secret (sekali saja)

Secret hanya lewat `dotnet user-secrets`, tidak pernah di file yang di-commit.

```powershell
# Kunci internal worker -> API: nilai SAMA di ketiga project
dotnet user-secrets set "Internal:Key" "<string acak >= 16 karakter>" --project src/OperatorAi.Api
dotnet user-secrets set "Internal:Key" "<string acak yang sama>"        --project src/OperatorAi.Worker.Operator
dotnet user-secrets set "Internal:Key" "<string acak yang sama>"        --project src/OperatorAi.Worker.Specialist

# Kunci penandatangan JWT: hanya API, minimal 32 karakter
dotnet user-secrets set "Auth:JwtKey" "<string acak >= 32 karakter>" --project src/OperatorAi.Api

# Key gateway (gw_...): API (settings/status/katalog model) dan kedua worker
dotnet user-secrets set "Ai:ApiKey" "<key gateway>" --project src/OperatorAi.Api
dotnet user-secrets set "Ai:ApiKey" "<key gateway>" --project src/OperatorAi.Worker.Operator
dotnet user-secrets set "Ai:ApiKey" "<key gateway>" --project src/OperatorAi.Worker.Specialist
```

Mengganti `Auth:JwtKey` membuat semua token yang beredar tidak berlaku (user harus masuk lagi).

**Produksi (owner) — email atur ulang password + tautan web.** Endpoint "Lupa password?" tersedia bila `Smtp:Host` **dan** `Web:BaseUrl` terisi; selama salah satu kosong API menjawab 503 `reset_unavailable`. Secret hanya lewat user-secrets; tidak pernah lewat file yang di-commit:

```powershell
dotnet user-secrets set "Smtp:Host" "smtp.example.com"          --project src/OperatorAi.Api
dotnet user-secrets set "Smtp:Port" "587"                       --project src/OperatorAi.Api
dotnet user-secrets set "Smtp:Username" "<akun smtp>"           --project src/OperatorAi.Api   # opsional
dotnet user-secrets set "Smtp:Password" "<secret smtp>"         --project src/OperatorAi.Api   # hanya secret store
dotnet user-secrets set "Smtp:From" "no-reply@example.com"      --project src/OperatorAi.Api
dotnet user-secrets set "Smtp:UseSsl" "true"                    --project src/OperatorAi.Api
dotnet user-secrets set "Web:BaseUrl" "https://chat.example.com" --project src/OperatorAi.Api
```

`Smtp:From` wajib alamat sah bila `Smtp:Host` diisi, dan `Web:BaseUrl` harus URL http/https absolut (kosong = fitur atur ulang dimatikan); keduanya divalidasi saat API start. Email dikirim lepas dari respons: kegagalan SMTP hanya tercatat di log API, tidak terlihat user. Tautan **Privasi**/**Ketentuan** di halaman login dibaca web saat build lewat `VITE_PRIVACY_URL` dan `VITE_TERMS_URL` (mis. `web/.env.production` atau environment sebelum `npm run build`); masing-masing hanya tampil bila diisi:

```text
VITE_PRIVACY_URL=https://example.com/privasi
VITE_TERMS_URL=https://example.com/ketentuan
```

### 2. Jalankan

**Upgrade 6A1/6A2/6B/6C + kebijakan runtime terpusat:** hentikan penerimaan pesan baru dan habiskan seluruh `Pending`/`Processing` queue/job dengan binari lama. Sesudah drain: stop API/worker/gateway lama, build seluruh project, terapkan migration chatbot (`Fase6A1ExecutionPolicy`, `Fase6A2ModelSelection`, `Fase6BStreaming`, `GatewayRuntimeLimits`) dan gateway (`G6A1AppStatusReports`, `G6A2ModelCapabilities`, `G6CVisionCapabilities`), lalu restart binari dan UI baru. Ulangi `gateway/sql/tenant-security.sql` bila upgrading 6A1 pada instalasi ber-RLS. **6C sendiri tidak menambah migration chatbot**; `GatewayRuntimeLimits` adalah cutover berikutnya, menambah constraint snapshot `ai`/`upload`/`document` dan sengaja menolak pekerjaan aktif tanpa backfill. Terminal historis tetap disimpan. Konfirmasikan tools/streaming/vision seluruh target route; unknown vision tidak boleh untuk gambar. Jangan mencampur worker lama dan baru: snapshot runtime wajib lengkap dan hash-nya diverifikasi.

**Upgrade Kelompok A (sesi login/refresh, koleksi, jawaban alternatif, ekspor):** hentikan penerimaan pesan baru dan habiskan seluruh `Pending`/`Processing` queue/job dengan binari lama, stop API/dua worker chatbot, build seluruh project chatbot, terapkan migration `20261013184807_KelompokA`, isi `Smtp:*`/`Web:BaseUrl`/`VITE_PRIVACY_URL`/`VITE_TERMS_URL` bila dipakai (bagian 1), lalu restart binari dan UI baru. Migration sejauh ini hanya diterapkan pada database lab `OperatorAi_KA_4c1e9a07`, **belum pada database runtime utama**; owner menerapkannya saat cutover dengan koneksi target yang diperiksa (`dotnet ef database update`). Tidak perlu mengubah atau menghentikan gateway untuk Kelompok A. Semua user harus masuk sekali lagi karena token lama tanpa klaim `sec` ditolak.

```powershell
.\scripts\dev.ps1            # cek secret, terapkan migration, lalu API :5080, web :5175, gateway :5090
.\scripts\dev.ps1 -NoGateway # bila gateway sudah berjalan di tempat lain
```

Buka `http://localhost:5175`, **Daftar** akun baru (username, password, email), centang **Ingat saya di perangkat ini** bila login harus bertahan lewat refresh cookie (tanpa centang, sesi berakhir saat access token habis), lalu pilih model di **Home** sebelum mengirim. Sesi baru dibuat saat unggah/kirim pertama; riwayat ada di sidebar Home dan percakapan yang tersimpan bisa dipilih lewat tab. Unggah lewat tombol klip, drag/drop atau paste berkas; format/ukuran/jumlah efektif berasal dari kebijakan API. Format yang didukung `.xlsx`/`.csv` untuk analisis + PDF output, atau `.pdf`/`.png`/`.jpg`/`.jpeg` untuk analisis dokumen; PNG/JPEG hanya bisa dikirim dengan model yang dikonfirmasi **vision** di gateway. Model lama/custom/unknown tetap terlihat beserta alasan nonaktif, bukan dipilih otomatis.

Worker (`Worker.Operator`, `Worker.Specialist`) **tidak dijalankan manual**: dispatcher di API menyalakannya per tiket (`dotnet <dll> --once`), lalu proses keluar. Port 5175 harus bebas (Vite `strictPort`, CORS API hanya mengizinkan `http://localhost:5175`). Bila origin berubah, Stop lalu Run/F5 API supaya CORS startup memakai kode baru; API yang hidup bukan bukti origin web sudah diizinkan. Mesin ini memblokir apphost `.exe`, jadi pakai `dotnet run` / `dotnet <dll>`. Hentikan API/gateway yang sedang berjalan sebelum build biasa (DLL terkunci).

**Alamat API untuk worker:** setelah menulis balasan, worker memanggil `POST {Api:BaseUrl}/internal/notify` supaya API mendorongnya ke browser lewat SignalR. Nilai default `http://localhost:5080` (cocok dengan `dotnet run` / `scripts/dev.ps1`). Bila API dijalankan dengan cara lain, mis. **Visual Studio/IIS Express** (`http://localhost:52928`), isi alamat itu di user-secrets kedua worker, kalau tidak balasan tersimpan di database tetapi browser menampilkan "Operator sedang mengetik…" terus sampai halaman di-refresh:

```powershell
dotnet user-secrets set "Api:BaseUrl" "http://localhost:52928" --project src/OperatorAi.Worker.Operator
dotnet user-secrets set "Api:BaseUrl" "http://localhost:52928" --project src/OperatorAi.Worker.Specialist
```

`scripts/dev.ps1` menimpa nilai ini ke `:5080` lewat environment variable, jadi tidak perlu diubah saat memakainya.

**OpenCode Go untuk uji nyata, bukan Zen:** provider/route uji harus memakai `https://opencode.ai/inference/go/openai/v1/`, tanpa fallback Zen. **Semua uji AI nyata (smoke, benchmark, uji kegagalan) memakai model `deepseek-v4.1-flash`**, kecuali user meminta lain secara eksplisit. **Pengecualian yang disetujui untuk 6C: uji vision (PDF scan/campuran, PNG/JPG/JPEG, grafik) memakai `glm-5.3-flash` lewat Go**; smoke lain (termasuk Excel/CSV → Analyze → PDF) tetap `deepseek-v4.1-flash`. Jangan mengganti model vision dengan model lain dan jangan memakai Zen sebagai pengganti. Worker mengirim User-Agent `OperatorAi/1.0` dan `x-opencode-session` = id conversation; gateway hanya meneruskan keduanya ke endpoint Go. Key/izin/langganan/limit Go tetap harus valid. Ketentuan: [OpenCode Go](https://opencode.ai/docs/go/#where-can-i-use-it).

**Worker lebih cepat start:** worker dijalankan sebagai proses baru per tiket. Secara default dispatcher memakai build Debug, yang membayar JIT di tiap pesan. `.\scripts\publish-workers.ps1` mempublikasikan kedua worker Release + ReadyToRun ke `artifacts/workers/` (±0,7 dtk lebih cepat per tiket); lalu isi `Dispatcher:OperatorDll` dan `Dispatcher:SpecialistDll` dengan path yang dicetak skrip, lewat appsettings API atau menu **Pengaturan** gateway (namespace `chatbot`, tanpa restart). Jalankan ulang skrip setiap kode worker/Shared berubah; publikasi tidak mengikuti build Visual Studio.

**Log worker:** keluaran tiap worker diteruskan ke log API dengan awalan `[Operator <run id>]`, termasuk baris `timing:` (sejak enqueue, boot, klaim, siapkan tiket, riwayat, konten AI pertama, commit) untuk melihat ke mana waktu satu pesan terpakai.

### 3. Build dan test

```powershell
dotnet build OperatorAi.slnx
dotnet test OperatorAi.slnx          # butuh LocalDB; tiap test membuat database sementara
cd web; npm run build; npm run lint; npm run check:attachments; npm run check:drafts
```

## Konfigurasi yang sering diubah

| Kunci | Arti |
| --- | --- |
| `Operator:MaxToolLoops` | 1–20, default 5. Batas putaran tool per percobaan tiket, bukan panjang jawaban. |
| `Operator:MaxTokensPerConversation` | 1000–2147483647, default 200000. Batas lunak token yang diketahui; Operator dan Specialist memeriksa sebelum panggilan AI. Panggilan berjalan dapat melewatinya. |
| `Operator:MaxHistoryMessages` | 1–500, default 40. Cap riwayat tambahan; pesan user asal dan kelompok tool tiket sendiri tetap utuh di luar cap. |
| `Auth:TokenMinutes` | 1–10080. Umur access token JWT; habis → web memperbarui sesi lewat cookie refresh bila **Ingat saya** aktif, jika tidak user masuk lagi. Tidak ada nilai di `appsettings.json` dasar (Development memakai 480), jadi instalasi non-Development wajib menyetelnya — nilai kosong/0 menggagalkan start. |
| `Auth:RefreshDays` / `Auth:ResetMinutes` | 1–90 / 5–120; default 30/30. Umur cookie refresh **Ingat saya** (hari) dan masa berlaku tautan atur ulang password di email (menit). |
| `Dispatcher:MaxConcurrentOperators` / `MaxConcurrentSpecialists` | Batas proses worker bersamaan; bisa diubah dari menu **Pengaturan** gateway (namespace `chatbot`) tanpa restart. |
| `Queue:MaxRetries` / `Queue:StaleLockMinutes` | 1–20 / 1–1440; default 3 / 10. `MaxRetries=3` berarti **tiga percobaan total**. Stale adalah umur heartbeat, bukan timeout pekerjaan. |
| `Ai:MaxTokens` | **Opsional**, 1–64000; tidak diset di `appsettings` secara default. Kosong = `max_tokens` tidak dikirim dan batas keluaran ditentukan gateway (policy tenant/project/key + batas model di UI gateway, berlaku tanpa deploy ulang). Diisi hanya bila chatbot sengaja memasang batas sendiri, lokal atau lewat Pengaturan gateway (namespace `chatbot`); gateway tetap bisa memperkecilnya. Berlaku untuk pesan baru. Reasoning dapat memakai sebagian budget; bila jawaban sering terpotong (`finish_reason=length`), naikkan policy/batas model di gateway. |
| `Response:Detail` | `ringkas`, `normal`, `mendalam` (default). Permintaan panjang/format eksplisit user tetap diikuti. |
| `Ai:AttemptTimeoutSeconds` / `Ai:TotalRequestTimeoutSeconds` | Settings gateway `int`, default 60/120 detik; cap 600/3600. Attempt membatasi satu percobaan HTTP; total mencakup retry **dan pembacaan body**. Retry standar tetap, breaker tetap window 120 detik. |
| `Ai:StreamFirstByteSeconds` | Settings gateway `int`, 1–300, default 30. Batas menunggu header/event pertama; lewat batas = tiket Operator dihentikan tanpa retry langsung. |
| `Ai:StreamIdleSeconds` / `Ai:MaxStreamSeconds` | Settings gateway `int`, 1–3600 / 1–86400 detik; default 60/900. Batas diam dan durasi body SSE memakai snapshot panggilan. |
| `Ai:MaxStreamBytes` / `Ai:MaxStreamEventBytes` | Settings gateway `int`, 1–8388608 / 1–262144 byte; default sama dengan cap. Tidak ada retry setelah teks stream dipublikasikan. |
| `Upload:AllowedExtensions` | Settings gateway **`text` CSV**, mis. `.pdf,.png`; subset non-kosong dari `.xlsx,.csv,.pdf,.png,.jpg,.jpeg`, tanpa duplikat. Lokal tetap array JSON sebagai baseline; bukan string CSV. |
| `Upload:MaxBytes` / `Upload:MaxAttachmentsPerMessage` | Settings gateway `int`, 1–10485760 byte / 1–6 lampiran; default 10 MiB/6. Total body menambah 64 KiB overhead multipart, termasuk chunked/IIS. UI mengambil kebijakan efektif dari API, bukan konstanta. |
| `Document:*` | Settings gateway untuk halaman/karakter/piksel, ukuran JPEG, resolusi/batch/base64 vision, dan watchdog. Hard ceiling tetap di kode; daftar key/default/range lengkap di [`IMPROVEMENTS.md` §16](IMPROVEMENTS.md#16-kebijakan-runtime-terpusat-pasca-6c). |
| `Prompt:AdditionalInstructions` | Text ≤ 2000 karakter, default kosong. Tambahan bisnis/gaya admin; bukan pengganti aturan inti, authorization, atau tempat secret. |

Key Operator/Queue/AI/Upload/Document/detail/prompt/Dispatcher dapat dibuat di **Pengaturan gateway**, namespace `chatbot`, tipe `int` atau `text`. API polling default 15 detik; satu kandidat invalid ditolak seluruhnya, hapus override kembali ke baseline lokal yang dicapture saat startup. JWT, key, origin/callback, connection string, storage, `Smtp:*`/`Web:BaseUrl`, dan batas OS/deployment tetap lokal/secret. Model tetap per kiriman dari gateway; tidak ada `Ai:Model`.

Panel **Status aplikasi** membedakan configured/applied, versi/hash, rejected/ignored, jumlah queue/job, dan kebijakan pekerjaan aktif. API melapor dengan key gateway yang sama, tanpa pesan/file/prompt/secret. Laporan menjadi stale setelah `max(60 detik, 3 × interval laporan)`; belum melapor bukan antrean nol. Satu instalasi API per key.

Pesan, queue, dan snapshot disimpan atomik; job/callback/retry/recovery memakai snapshot asal meskipun admin mengganti nilai. Worker menjaga lease setiap 20 detik dan membatalkan kerja bila ownership hilang. Usage yang tidak lengkap ditampilkan sebagai batas bawah, bukan biaya nol; `finish_reason=length` diberi peringatan terlihat tanpa auto-continue.

`GET /api/attachment-policy` wajib JWT dan memberi `{ maxBytes, allowedExtensions, maxAttachmentsPerMessage, maxImagePixels, sourceSettingsVersion, policyHash }` dari satu snapshot. UI memuat ulang sebelum upload/send dan saat window focus; perubahan policy tidak menghapus draft atau lampiran lama. Kiriman baru memeriksa ulang count mentah, format dan ukuran; job lama tetap memakai kebijakan asal. Timeout AI dan seluruh budget dokumen juga ikut hash/snapshot, bukan dibaca ulang dari appsettings worker.

Laporan analisis standar yang lengkap memakai output JSON tervalidasi secara langsung bila Operator menetapkan `langsung=true`; seluruh temuan, rekomendasi, tabel/kolom, dan catatan tetap ditampilkan. Hal yang sama berlaku untuk hasil analisis dokumen (PDF/gambar) dan notifikasi lampiran PDF. Permintaan sintesis/terjemahan/format lain atau tambahan prompt admin tetap memakai AI. System prompt dan profil sumber mengurangi risiko halusinasi, **bukan jaminan semua interpretasi model benar**.

## Pemilihan model (6A2)

- `/api/models` wajib JWT, memakai key gateway **server-side**; semua user chatbot berbagi scope key instalasi, bukan tenant gateway masing-masing.
- `Model` → **Muat ulang** memperbarui katalog tanpa rebuild. Kosong, loading, dan gateway gagal (503 + **Coba lagi**) berbeda; tidak ada daftar static/default/auto-pilih alias pertama.
- Tools harus `true` dan readiness route/provider/credential harus tersedia. Unknown/false tetap terlihat dengan alasan. Readiness bukan health probe; batas output adalah minimum konfigurasi model/policy, bukan context window atau sisa kuota.
- Capability **vision** (6C) tri-state: `true` = bisa memeriksa gambar, `false` = tidak bisa, `null` = belum dikonfirmasi (perlakukan seperti tidak bisa). Badge tampil di samping model; lampiran PNG/JPEG hanya bisa dikirim bila `supportsVision = true` (UI memblokir, server menolak bypass). PDF teks tetap bisa diproses model non-vision; halaman scan/grafik memerlukan model vision dan job gagal jujur tanpa fallback model bila tidak ada.
- Pilihan tersimpan saat pesan diterima, lalu dibekukan di tiket, semua job, retry/recovery, dan callback. Ganti selector hanya untuk kiriman berikutnya; badge pesan memakai alias workflow sendiri.
- Model dicabut (`403 model_not_allowed` / `404 model_not_found`) → satu kegagalan terminal dengan pesan pilih model lain; Specialist juga menggagalkan dependensinya dan callback menjawab tanpa AI. Tidak ada fallback model. Gangguan jaringan tetap retriable.
- Send baru: `{ content, attachmentIds, model }` → `202 { message, queueItemId }`. Model wajib; kegagalan validasi tidak menulis pesan/tiket dan UI mempertahankan draft/lampiran.
- Hasil smoke Go/UI/IIS Express, perbandingan dua model, retry dan batas kualitas ada di [`IMPROVEMENTS.md` §12](IMPROVEMENTS.md#12-hasil-implementasi-dan-verifikasi-6a2). Runtime/provider utama belum di-upgrade; lakukan drain + migration + konfigurasi Go/konfirmasi tools (dan vision) + restart sebelum memakai UI baru. Streaming jawaban: bagian berikutnya; dokumen dan gambar: bagian 6C.

## Streaming jawaban (6B)

- Jawaban Operator dialirkan hanya untuk alias yang di gateway dikonfirmasi `supportsStreaming = true` (Admin → Model); alias lain tetap dijawab utuh. Status itu ditangkap server saat pesan diterima dan dibekukan di tiket, job, dan callback.
- Worker membaca SSE gateway, lalu `POST /internal/stream` (kunci internal, tanpa membangunkan dispatcher) → API → SignalR `streamEvent`. Browser menampilkan **draft** (garis aksen, kursor dan status sementara) dan menggantinya dengan pesan canonical bila `streamId` cocok. Draft tidak disimpan di SQL; balasan final tetap atomik bersama status tiket `Done`. Animasi dimatikan melalui `prefers-reduced-motion`.
- Reasoning internal model dan argumen tool tidak pernah dikirim ke browser. Stream terputus → draft ditandai "terputus", tiket dicoba ulang dengan `streamId` baru (retry tidak menumpuk teks); usage yang tidak diterima ditandai tidak lengkap (`usage_incomplete`), bukan nol.
- Reconnect SignalR tidak memutar ulang chunk: draft ditandai "menyinkronkan" sampai pesan canonical tiba; `GET /api/conversations/{id}/activity` dan event `activityChanged` menentukan indikator "sedang mengetik".
- Batas SSE (`Ai:StreamIdleSeconds`, `Ai:MaxStreamSeconds`, `Ai:MaxStreamBytes`, `Ai:MaxStreamEventBytes`) dan hasil ukur ada di [`IMPROVEMENTS.md` §13](IMPROVEMENTS.md#13-hasil-implementasi-dan-verifikasi-6b). Streaming mempercepat **teks pertama yang terlihat**, bukan antrean, cold start worker, atau reasoning model.


## Analisis dokumen dan gambar (6C)

- **Allow-list upload:** format yang didukung `.xlsx`, `.csv`, `.pdf`, `.png`, `.jpg`, `.jpeg`; video ditolak. Policy gateway memilih subset, ukuran/jumlah dan piksel efektif; default/ceiling 10 MiB per file, 6 lampiran, 16.000.000 piksel. Total body menambah 64 KiB overhead multipart termasuk chunked/IIS. Isi divalidasi nyata (signature PDF, codec PNG/JPEG sebelum decode penuh) dan disimpan lokal; DB hanya metadata + path, bukan byte/base64.
- **Pipeline:** PDF dan gambar memakai `DocumentRead` (tanpa AI: ekstraksi teks per halaman + penentuan halaman yang perlu pemeriksaan visual) → `DocumentAnalyze` (AI: batch halaman visual lewat model vision, lalu sintesis `judul`/`ringkasan`/`penjelasan`/`poin`/`sumberHalaman`/`catatan`). Excel/CSV tidak berubah: `ExcelRead → Analyze → Pdf`.
- **Teks, scan, campuran, dan vision:** PDF teks bisa dibaca model apa pun. Halaman tanpa teks atau yang memuat gambar tertanam, dan seluruh file PNG/JPEG, diperiksa lewat vision sehingga memerlukan model `supportsVision = true`. `visual = true` memeriksa semua halaman PDF — grafik/layout vektor yang tidak terdeteksi sebagai gambar hanya diperiksa saat itu. Halaman berteks pendek tidak otomatis dianggap scan. Deteksi ini best-effort: tidak ada klaim semua scan/grafik pasti terbaca, dan bagian yang tidak terbaca dilaporkan jujur, bukan dikarang.
- **Batas yang gagal eksplisit:** semua dari snapshot job. Default/ceiling PDF 100 halaman/100.000 karakter; render 1280 px/400000 byte, minimum default 640 px; batch 4 gambar/2.000.000 karakter base64. Gateway dapat memperkecil batas sesuai range §16; melewati batas = job Failed, bukan dipotong. Render/base64 hanya di memori. PDF terkunci/rusak gagal eksplisit.
- **Kategori kegagalan dipisahkan:** `modelUnavailable` (model dicabut → balasan pilih model lain, tanpa retry) versus `documentInvalid` (dokumen rusak/terkunci/melebihi batas, atau halaman visual pada model non-vision → dihentikan terminal dan callback menjawab jujur tanpa memanggil AI). Timeout/`AiException` tetap retriable.
- **Watchdog runtime:** `Document:WatchdogSeconds` snapshot (1–60, default 60) menjaga ekstraksi/render PDF; deadline terlewati menghentikan proses dengan exit 76 dan job dipulihkan sesuai kebijakan antrean. Bukan sandbox atau hard memory limit OS.
- **UI:** pratinjau gambar memakai `fetch` ber-JWT → Blob browser → object URL (bukan `<img src>`, bukan penyimpanan cloud), dialog native untuk memperbesar, Esc mengembalikan fokus, serta cleanup URL/abort. Tata letak mobile satu kolom tanpa overflow horizontal dan nama file panjang tetap terbaca. Check ringan: `npm run check:attachments`.
- **Kompatibilitas jalur lama:** alur Excel (`.xlsx`) dan CSV → Analyze → PDF diuji ulang di lab 6C dengan `deepseek-v4.1-flash` lewat Go — job selesai tanpa retry, PDF terunduh berheader `%PDF-`, dan laporan cocok dengan fixture. Detail dan buktinya di [`IMPROVEMENTS.md` §15](IMPROVEMENTS.md#15-hasil-implementasi-dan-verifikasi-6c).
- **Status:** implementasi dan smoke 6C berjalan di lingkungan uji terisolasi (Windows, gateway Go-only, `glm-5.3-flash` untuk vision). Hasil/acceptance aktual dan batas yang belum diverifikasi ada di [`IMPROVEMENTS.md` §15](IMPROVEMENTS.md#15-hasil-implementasi-dan-verifikasi-6c). **Runtime/provider utama belum di-upgrade**, dan render native Linux/batas memori OS belum diverifikasi.

## Kelompok A: sesi login, koleksi, jawaban alternatif, ekspor lokal

- **Sesi login & "Ingat saya":** daftar memakai username + password + email (email unik, dinormalkan; akun lama tanpa email tetap bisa masuk tetapi tidak bisa atur ulang password). **Ingat saya di perangkat ini** (default mati) menyimpan refresh token sebagai cookie `Secure` + `HttpOnly` + `SameSite=Strict` di path `/api/auth` dengan umur `Auth:RefreshDays` hari; tanpa centang itu sesi berakhir saat access token habis (`Auth:TokenMinutes`). Cookie dirotasi tiap pembaruan; pemakaian ulang token lama di luar tenggang singkat (±10 detik, untuk tab yang bersamaan) mencabut semua sesi user. Logout mencabut refresh token di server, tetapi access token yang beredar tetap sah sampai kedaluwarsa (tanpa denylist).
- **Satu situs:** cookie refresh hanya terkirim bila web dan API satu situs — di produksi berarti satu domain/reverse-proxy (localhost aman walau beda port). Konfigurasi lintas situs belum didukung/diuji.
- **Atur ulang password:** "Lupa password?" butuh `Smtp:*` + `Web:BaseUrl` (bagian 1); email berisi tautan `{Web:BaseUrl}/?reset=<token>` yang berlaku `Auth:ResetMinutes` menit. Ganti password sukses mengganti security stamp + mencabut semua refresh token, sehingga semua token lama langsung ditolak. Setelah upgrade ini semua token terbitan lama (tanpa klaim `sec`) ditolak — semua user masuk sekali lagi. Akun lama tanpa email tidak menerima email atur ulang (fail closed); isi `users.email` dulu bila akun itu perlu.
- **Jawab ulang & Bandingkan:** per jawaban assistant ada **Salin jawaban**, **Jawab ulang** (model jawaban asal, fallback model sesi; jawaban dari job/callback mengulang tiket penuh sehingga bisa memulai job baru = biaya token tambahan), **Bandingkan dengan model lain** (1–3 model yang lolos validasi katalog/readiness), dan **Simpan ke koleksi**. Tiket alternatif boleh memakai model berbeda dari model sesi — kecocokan model hanya divalidasi untuk tiket utama, dan `conversations.model_alias` tidak berubah. Jawaban alternatif diberi label **Alternatif** dan tidak ikut konteks pesan/tiket berikutnya; tidak ada aksi "jadikan utama". Header jawaban menampilkan model, waktu total, dan token bila diketahui; baris lama tanpa data tetap tampil.
- **Koleksi & hapus sesi:** Koleksi di sidebar menyimpan cuplikan jawaban per user (model + judul sesi); **Hapus sesi** menghapus permanen pesan, lampiran, dan file hasil sesi itu, tetapi jawaban koleksi tetap ada (sesi hilang → "Sumber dihapus"). Hapus sesi ditolak 409 `conversation_busy` selama masih ada tiket/job sesi itu yang belum selesai.
- **Ekspor .md:** tombol "Ekspor .md" mengunduh Markdown percakapan secara lokal (lampiran hanya nama file); belum ada tautan berbagi. Jawaban assistant dirender Markdown (GFM, HTML dimatikan, tautan keluar `noopener`, blok kode di-highlight); draft streaming tetap teks biasa.
- **Batas yang diketahui:** pengiriman email lepas dari respons (gagal = hilang + dicatat di log); rate limit mengasumsikan satu instance API; biaya tampil sebagai token per jawaban, bukan uang.

## Berhenti dan pemulihan

- Worker yang dihentikan baik-baik (Ctrl+C / SIGTERM) mengembalikan tiketnya ke `Pending` tanpa menambah `retry_count`; proses berikutnya mengambilnya.
- Worker yang dimatikan paksa dipulihkan dispatcher lewat run id-nya (`retry_count` +1). Tiket yang macet tanpa sinyal dipulihkan setelah `Queue:StaleLockMinutes`.
