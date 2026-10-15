# Handoff 6C — snapshot historis, ditutup setelah acceptance

Tanggal pencatatan: 2026-10-12. User meminta pekerjaan dilanjutkan besok dan file ini dibuat supaya agent berikutnya mengetahui konteks tepatnya.

**Pembaruan 2026-10-13: 6C selesai implementasi dan acceptance terisolasi.** Bukti final ada di [`IMPROVEMENTS.md` §15](IMPROVEMENTS.md#15-hasil-implementasi-dan-verifikasi-6c) dan `DECISIONS.md` entri 2026-10-13; ringkasan penutupan ada di §9 di bawah. Bagian 1–8 adalah **snapshot 2026-10-12**, bukan instruksi aktif untuk mengulang job/build/lab lama. Angka 323 tidak berlaku sebagai hasil source final. **6D belum dimulai; rollout utama tidak dilakukan.**

## 1. Permintaan dan keputusan user

- 6C diperluas dari PDF teks menjadi **PDF teks, PDF scan/campuran, serta PNG/JPG/JPEG**. Membaca tulisan sekaligus grafik, diagram, screenshot, dan informasi visual melalui model vision.
- **Tidak ada video**. Tidak membangun ekstraksi frame atau analisis video.
- **Penyimpanan folder lokal saja**, reuse `FileStorage`. Belum blob/object storage/cloud. Database menyimpan metadata dan path relatif, bukan byte/base64 gambar.
- User mengizinkan pengecualian aturan model uji untuk vision, tetapi **harus GLM-5.3-Flash (`glm-5.3-flash`) melalui OpenCode Go**, bukan model vision lain atau Zen. Aturan uji lain tetap `deepseek-v4.1-flash` melalui Go kecuali user memutuskan lain.
- Tidak mengganti model workflow secara diam-diam. Model, policy, source message, streaming, dan konfirmasi vision disnapshot ketika enqueue dan diwariskan ke job/callback/retry.
- **6D PRD/Mermaid → PNG, bug gateway yang ditunda, Docker, worker reuse, serta layanan cloud tidak termasuk pekerjaan ini.**

## 2. Yang sudah diimplementasikan

### Gateway dan transport AI

- `gateway/src/AiGateway.Core/Domain/Entities.cs`: `Model.SupportsVision` nullable.
- Migration additive **`20261012090000_G6CVisionCapabilities`** beserta designer dan model snapshot: `models.supports_vision` nullable. Migration sudah berhasil diterapkan ke database gateway **privat**, tidak ke database utama user.
- `ProvisioningService.cs` / `ProvisioningService.Admin.cs`, `ModelEndpoints.cs`: create/patch/DTO vision mengikuti tri-state tools/streaming, termasuk `clearSupportsVision`. Target route/BaseUrl berubah → konfirmasi vision ikut dihapus; seluruh target termasuk fallback nonaktif tercakup.
- `DataPlaneEndpoints.cs`: `/v1/models` membawa `capabilities: { tools, streaming, vision }`; pembacaan JSON body berbatas byte juga berlaku untuk chunked/IIS. Default batas gateway tetap 4 MB.
- `AnthropicAdapter.cs` / `ResponsesAdapter.cs` mempertahankan bagian teks + gambar, bukan flattening yang menghilangkan gambar. Request dari chatbot memakai inline PNG/JPEG data URL; adaptor gateway juga mempertahankan dukungan format/URL yang didokumentasikan. Gateway tidak mengambil URL gambar sendiri.
- `ChatProxy.cs`: inline image data URL direduksi menjadi penanda sebelum request body opt-in disimpan ke database. Upstream tetap menerima gambar asli.
- `gateway/web/src/types.ts` / `pages/ProviderPages.tsx`: badge dan editor vision tri-state.
- `src/OperatorAi.Shared/Ai/IAiClient.cs`: `AiMessage` memiliki parameter terakhir `JsonArray? ContentParts`; serialisasi mengirim array konten dan pesan multimodal tidak boleh digabung sebagai teks biasa.
- `GatewayModelsClient.cs`, `Contracts/Dtos.cs`, `ModelSnapshot.cs`: metadata vision, snapshot `supportsVision`, dan `AttachmentDto.ContentType`.

### API, operator, queue, dan UI chatbot

- `AttachmentEndpoints.cs`: multipart dibaca langsung secara berbatas sebelum buffering seluruh file; maksimum upload 10 MB + overhead multipart 64 KiB; file disimpan lokal dahulu. Validasi format nyata PNG/JPEG, MIME kanonik, serta signature PDF. Parser PDF penuh berjalan di worker, bukan API.
- PNG/JPEG dibatasi **16.000.000 piksel sebelum decode penuh**, ekstensi harus cocok dengan format decoder. File ditolak/partial upload dibersihkan jika upload tidak committed.
- `ConversationEndpoints.cs`: gambar standalone menuntut `SupportsVision == true`; server menolak bypass UI. Maksimum **6 lampiran per pesan**.
- `QueueRepository.cs`: job callback mewarisi `supportsVision`; kategori kegagalan **`documentInvalid`** dipisahkan dari **`modelUnavailable`**. Dokumen invalid dihentikan terminal tanpa retry dan callback tidak mengulang AI hanya untuk menjelaskan error input.
- `Tools/ToolDefinitions.cs` / `ToolDispatcher.cs` / `OperatorPrompt.cs`: tool **`analisis_dokumen`** untuk PDF/gambar, argumen `attachment_id`, `tujuan`, `langsung`, `visual`. Rantai **DocumentRead → DocumentAnalyze** dibuat dalam transaksi tools yang sudah ada. Excel/CSV tetap `ExcelRead → Analyze → Pdf`.
- `DirectReply.cs` / `TicketProcessor.cs`: output `DocumentAnalysisResult` tervalidasi dapat disampaikan lengkap tanpa panggilan reformatting yang tidak perlu. `finishReason` diteruskan ke pesan canonical.
- Allow-list `.pdf/.png/.jpg/.jpeg` ditambah di tiga `appsettings.Development.json`; URL/secret milik user tidak diubah.
- `web/src/api/attachments.ts`, `components/AttachmentPreview.tsx`, Composer, MessageBubble, ModelSelect, JobStatusBar, client/types/CSS: upload dokumen/gambar, gate vision, badge, status job, preview via **fetch JWT → Blob browser → object URL**, native dialog untuk perbesar, fokus kembali, cleanup URL/abort. Blob di browser bukan penyimpanan cloud.
- `web/package.json`: check tambahan `check:attachments`; tidak ada dependency frontend baru.
- `.gitignore`: `storage/` diubah menjadi `/storage/` agar source `src/**/Storage/` tidak ikut terabaikan. Subagent sebelumnya force-add dua source Storage baru; jangan menghapusnya atau mengubah staging user sembarangan.

### Worker spesialis dan dependency baru

- Shared: `Storage/InputFileValidation.cs`, `Storage/DocumentInputException.cs`, `Contracts/DocumentAnalysisResult.cs`.
- Worker: `Handlers/DocumentReadHandler.cs`, `DocumentAnalyzeHandler.cs`, `Documents/PdfTextExtractor.cs`, `PdfPageRenderer.cs`, `VisionPayload.cs`, `DocumentReadOutput.cs`; registrasi di Program dan mapping dependensi di SpecialistWorker.
- Dependency: **PdfPig 0.1.16** (package id `PdfPig`, namespace `UglyToad.PdfPig`), **PDFtoImage 5.2.0**, **SkiaSharp 3.119.1**. SkiaSharp juga direferensikan Shared untuk validasi upload.
- Ekstraksi teks memakai `ContentOrderTextExtractor.GetText(page, true)` dan mempertahankan nomor halaman. Maksimum **100 halaman / 100.000 karakter hasil ekstraksi**, melebihi batas gagal eksplisit, bukan dipotong.
- Halaman tanpa teks atau dengan gambar tertanam diperiksa visual; `visual=true` memeriksa seluruh halaman. Halaman judul berteks pendek tidak otomatis dianggap scan. Grafik vektor/layout yang tidak terdeteksi sebagai gambar perlu permintaan visual; jangan mengklaim deteksi otomatis sempurna.
- PDF dirender via PDFium, ukuran disesuaikan terhadap dimensi halaman sebelum alokasi sehingga sisi terpanjang maksimum **1280 px**. JPEG maksimum **400 KB**, ladder downscale sampai 640 px jika dibutuhkan. Original upload tetap dipertahankan.
- Vision batch maksimum **4 gambar / 2.000.000 karakter base64**. Semua halaman visual diproses dalam batch, bukan semuanya ditahan di memori atau dibuang. Render JPEG/base64 hanya hidup sementara **di memori**; tidak menulis file render ke DB/disk, sehingga tidak ada temp render yang perlu dihapus.
- Vision menghasilkan catatan per halaman, lalu sintesis memakai teks penuh + catatan. Skema hasil: `judul`, `ringkasan`, `penjelasan`, `poin[]`, `sumberHalaman[]`, `catatan`, dan metadata `finishReason` bila perlu. Nomor halaman di luar dokumen memicu satu perbaikan; truncation visual tidak boleh dianggap laporan lengkap. Metadata finish dari model JSON tidak boleh mengalahkan finish transport.
- `DocumentWatchdog` mematikan proses worker dengan **exit code 76** jika ekstraksi PDF atau render melewati batas 60 detik. Dispatcher memulihkan run mengikuti retry policy. Ini bukan sandbox/hard memory limit; watchdog dalam proses nyata belum dismoke secara terpisah. Memory limit keras tetap membutuhkan fasilitas OS/deployment, bukan jaminan dari batas upload.
- Windows sudah dipakai dalam uji. Jangan mengklaim native rendering/Linux API sudah diverifikasi; API Linux mungkin memerlukan SkiaSharp native assets tambahan.

## 3. Bukti yang benar-benar sudah diamati

| Pemeriksaan | Hasil |
| --- | --- |
| Build chatbot terakhir yang dijalankan | 0 warning / 0 error, output terisolasi |
| Build gateway terakhir yang dijalankan | 0 warning / 0 error, output terisolasi |
| Full suite chatbot | **323 passed / 0 failed / 0 skipped**; TRX tersimpan di lab |
| Full suite gateway | **354 passed / 0 failed / 1 skipped**; skip smoke DNS/TLS opsional yang sudah ada |
| Kedua frontend build/lint | Lulus; warning preview `set-state-in-effect` sudah diperbaiki dengan state berkunci download URL, lalu build/lint chatbot diulang bersih |
| `check:attachments`, `check:drafts` | Lulus pada source sebelum edit backend terakhir |
| Migration gateway privat | Berhasil, 6 migration terpasang; migration chatbot privat 8 |
| GLM Go vision preflight | Gateway privat Go-only membaca kode acak `TRX-745EC7`, batang merah/biru 40/80, selisih 40; HTTP 200, 4,32 detik, usage 926/283. Bukan benchmark chatbot |
| Browser → IIS Express InProcess → workers → gateway Go, PNG | DocumentRead/DocumentAnalyze Done, retry 0; jawaban memuat kode serta fakta grafik; preview/perbesar/Esc/focus restore diamati |
| PDF teks 2 halaman | Kedua job Done, retry 0; membaca `PRD-K7R9`, batas Rp 5.000.000 dan sumber halaman 1/2 |
| PDF scan | Kedua job Done, retry 0; membaca `SCAN-996A5C`, keputusan verifikasi sebelum pembayaran dan batas persetujuan |
| JPEG | Kedua job Done, retry 0 pada state SQL akhir. Canonical jawaban/preview mobile belum ditinjau akhir dari browser |
| PDF terkunci / corrupt / 101 halaman | DocumentRead Failed retry 1 secara terminal; dependent DocumentAnalyze Failed retry 0; JobFailed callback Done. Alasan tepat: password, file rusak, maksimum 100 halaman |
| PNG rusak, PNG disamarkan JPG, video | Upload 400; tidak meninggalkan baris attachment/file rejected |
| Upload >10 MB | HTTP 413 |
| Image model vision unknown | UI Kirim disabled; bypass server dengan DeepSeek → 400 |
| Download PNG | JWT 200 `image/png`, 17.298 byte identik original; tanpa token 401, user lain 404; conversation user lain 404 |
| Gambar dalam DB job input/output | Query terhadap `data:image` menghasilkan 0; attachment hanya metadata/path lokal |

Catatan browser: ada `ERR_ABORTED` pada fetch preview yang dibatalkan lifecycle/StrictMode; preview nyata berhasil. Jangan mengklaim semua telemetri jaringan bersih atau menganggap abort itu otomatis bug upload. Beberapa harness `waitForFunction` gagal/waktu tunggu habis, sementara hasil PDF teks akhirnya sudah ada; cek canonical/job, jangan mengirim ulang sekadar karena harness menunggu gagal.

## 4. Masalah utama yang belum selesai

**Smoke PDF campuran belum lulus end-to-end.** Fixture `dokumen-campuran.pdf` memiliki grafik/kode transaksi pada halaman 1 dan scan/kode rapat pada halaman 2.

- Job **7 DocumentRead Done**, `visualPages` mencakup kedua halaman.
- Job **8 DocumentAnalyze akhirnya Failed, retry_count 3**.
- Error akhir: `AI tidak terjangkau: The operation didn't complete within the allowed timeout of '00:02:00'.`
- Log membuktikan satu panggilan vision menerima HTTP 200 setelah sekitar **52 detik**, prompt 3794 / completion 1510, dan catatan visual halaman **1,2** diterima (2019 karakter).
- Panggilan sintesis sesudahnya melewati attempt timeout **60 detik**, retry resilience, kemudian total timeout **120 detik**. Workflow dicoba ulang sesuai queue policy dan tetap gagal.
- **Penyebab akar belum dipastikan.** Jangan menganggap parser/render rusak; bukti yang ada mengarah ke penantian panggilan inferensi. Periksa log client/gateway/usage dan kontrak request yang benar sebelum memilih tindakan. Jangan menaikkan timeout, menonaktifkan validasi, mengganti GLM, atau memakai Zen sekadar agar smoke terlihat hijau.
- Callback kegagalan mixed juga sudah Done. Semua job/queue lab berstatus terminal saat sesi dihentikan.

## 5. Edit PALING AKHIR — belum dibangun/diuji

Setelah suite 323 dan smoke di atas, agent memperbaiki:

1. `src/OperatorAi.Api/Endpoints/AttachmentEndpoints.cs`: MIME sekarang switch eksplisit XLSX/CSV; tipe lain wajib melalui `InputFileValidation` sehingga tidak ada fallback palsu `text/csv` untuk ekstensi tidak dikenal. Truncation nama file menjaga ekstensi agar handler tidak menolak PDF/gambar valid ber-nama panjang.
2. `src/OperatorAi.Api/Endpoints/ConversationEndpoints.cs`: batas 6 diperiksa pada **panjang array mentah sebelum Distinct/alokasi**; duplikasi besar tidak boleh melewati batas.
3. `tests/OperatorAi.Tests/DocumentWorkflowTests.cs`: menambah regression `Six_attachments_are_accepted_but_an_oversized_array_is_rejected_before_catalog_failure`.

**Biner di lab masih source sebelum tiga edit ini.** Jangan menyebut 323 sebagai hasil final source terbaru. Tidak ada build/test baru setelah user meminta lanjut besok.

## 6. Lab dipertahankan; proses sudah dihentikan

Semua service/browser milik sesi sudah dihentikan. Tidak ada worker dotnet lab hidup saat snapshot akhir; **Pending/Processing job = 0, queue = 0**. Tidak membiarkan inferensi berjalan semalaman.

| Komponen | Lokasi / identitas |
| --- | --- |
| Root lab | `C:\Users\DEV360~1\AppData\Local\Temp\operator-6c-qb79o4r8` |
| Database chatbot privat | `OperatorAi_6C_a2ebff8d4e` pada `(localdb)\MSSQLLocalDB` |
| Database gateway privat | `AiGateway_6C_b1e5b3bca9` pada instance yang sama; clone database dev, provider/route uji Go-only |
| Build terisolasi | `<lab>\build\bin\<Project>\debug\` |
| TRX chatbot | `<lab>\test-results\6c-chatbot.trx` (323 passed) |
| Upload smoke | `<lab>\storage\uploads\{conversationId}\...` |
| IIS application host | `<lab>\applicationhost.config` dan `<lab>\iis\` (copy binary API) |
| Launcher IIS | `<lab>\start-api.ps1`, port 5186, bindings localhost dan 127.0.0.1 |
| Launcher gateway | `<lab>\start-gateway.ps1`, port 5196 |
| Vite lab | `<lab>\vite.config.mjs`, port 5178, proxy API/hub ke 5186 |
| Helper migration | `<lab>\setup\LabSetup.csproj`; compiled `<lab>\setup-build\bin\LabSetup\debug\LabSetup.dll` |
| Log IIS/worker | `<lab>\iislog\stdout_20261012100624_30836.log` dan log IIS lainnya |
| Fixtures | `gambar-penjualan.png`, `foto-penjualan.jpg`, `prd-teks.pdf`, `dokumen-scan.pdf`, `dokumen-campuran.pdf`, `password.pdf`, `rusak.pdf`, `101-halaman.pdf`, `palsu.png`, `video.mp4`, `besar.png` |

User uji utama: `lab6c_3c2f4948`; password acak hanya di memori sesi yang sekarang sudah tidak dapat diandalkan. **Tidak ditulis di dokumen.** Bila perlu login, buat akun uji baru via register pada DB privat; jangan meminta password user atau menaruh key di chat/file repo. File launcher API membaca `Ai:ApiKey` dan `Internal:Key` dari user-secrets API, membuat JWT key lab baru, dan menunjuk seluruh koneksi/path ke lab.

Settings lab: cap Specialist awal 1, kemudian diubah ke **2 lewat Pengaturan gateway privat** agar kasus invalid/JPEG tidak terblokir di belakang mixed timeout. Output policy key lab 4096 dari gateway. Ini konfigurasi uji, bukan perubahan default/config utama atau bukti speedup.

Temp folder bisa dibersihkan OS; periksa keberadaannya sebelum memakai command di bawah. Jika hilang, buat lab baru terisolasi dari source, bukan menjalankan pengujian pada database utama.

## 7. Urutan konkret untuk melanjutkan besok

1. Baca `HANDOFF_6C.md`, bagian 6C di `IMPROVEMENTS.md`, dan caller aktual; pertahankan perubahan user. Jangan menyalakan inference terlebih dahulu.
2. Build source terbaru ke artifacts terisolasi. Jalankan regression baru + suite terdampak; catat hasil yang benar-benar diamati. Source terakhir belum diverifikasi.
3. Sebelum menyalakan IIS, refresh `<lab>\iis\` dari output API terbaru; worker launcher menunjuk binari worker dalam build terisolasi. Jangan mengganti binari utama user tanpa rollout yang eksplisit.
4. Jalankan gateway, IIS, dan Vite lab; pastikan routes **Go-only**, GLM vision/tools terkonfirmasi pada target privat, output policy berlaku, callback worker menuju API lab yang benar.
5. Investigasi mixed timeout di log/usage, lalu buktikan mixed PDF halaman 1/2 berhasil dibaca oleh GLM melalui browser. Pertahankan error/retry yang sudah terjadi sebagai bukti; jangan menyembunyikannya.
6. Lengkapi review canonical JPEG, preview/download pada 390px tanpa overflow, admin gateway vision tri-state pada browser, limit lampiran 6/7 dan filename panjang, chunked multipart/body limit IIS, serta cancellation/retry/watchdog sesuai perubahan. Regression banyak edge sudah ada; actual OS watchdog/cancellation belum lengkap.
7. Jalankan alur lama Excel/CSV → Analyze/PDF dengan **DeepSeek Go**, bukan GLM/Zen tanpa izin tambahan. Suite lama sudah lulus, tetapi smoke runtime ulang untuk kompatibilitas 6C belum dilakukan.
8. Sesudah smoke lengkap: perbarui **README.md, DESIGN.md, IMPROVEMENTS.md, GATEWAY.md, gateway/README.md, DECISIONS.md** agar mencerminkan implementasi, dependency, batas, migration, acceptance, dan bukti aktual. Saat ini dokumen desain utama masih menyebut PDF/6C belum dibuat atau scope PDF teks lama.
9. Bersihkan service/tab/lab/database/fixtures yang dibuat sesi setelah acceptance selesai, dengan guard nama/path; jangan menyentuh data/proses/secret user. Barulah tandai 6C selesai. 6D tetap pekerjaan terpisah.

Contoh command build/check (gunakan path yang masih ada):

```powershell
$lab = 'C:/Users/DEV360~1/AppData/Local/Temp/operator-6c-qb79o4r8'
dotnet build OperatorAi.slnx --artifacts-path "$lab/build" --nologo
dotnet test "$lab/build/bin/OperatorAi.Tests/debug/OperatorAi.Tests.dll" --logger 'trx;LogFileName=6c-chatbot-latest.trx' --results-directory "$lab/test-results"
# Build web gateway SELESAI dahulu; jangan bersamaan dengan build backend yang membaca wwwroot.
cd gateway/web
npm run build
npm run lint
cd ..
dotnet build AiGateway.slnx --artifacts-path "$lab/build" --nologo
# Bila gateway code berubah, jalankan suite terdampak, bukan menganggap 354 masih otomatis berlaku.
```

Launcher, masing-masing service terpisah sesudah binary copy benar:

```powershell
# cwd: <lab>/build/bin/AiGateway.Api/debug
powershell -NoProfile -File <lab>/start-gateway.ps1
# cwd: <lab>/iis
powershell -NoProfile -File <lab>/start-api.ps1
# cwd: repo/web
node node_modules/vite/bin/vite.js --config <lab>/vite.config.mjs
```

`<lab>` pada blok launcher adalah placeholder path root lab di tabel, bukan path literal. Mesin ini menolak apphost `.exe`; untuk helper gunakan **`dotnet ...dll`**, bukan `dotnet run` yang mencoba exe.

## 8. Catatan integrasi agar tidak mengulang kesalahan

- Tidak ada language server yang dikonfigurasi saat pekerjaan ini; caller ditelusuri dengan grep. Periksa availability lagi bila lingkungan berubah.
- Tiga slice didelegasikan: gateway/transport, specialist PDF/vision, dan frontend chatbot. Integrasi dan verifikasi parent sudah dilakukan; jangan mereplikasi implementasi atau membangun control plane/storage kedua.
- `AiMessageContentPartsTests.cs` sengaja **dihapus** karena copy/round-trip-only checks. Penggantinya `ConversationBuilderTests.Adjacent_user_text_does_not_merge_away_a_multimodal_turn` menguji bug merging yang consumer-visible. Jangan membuat ulang file yang dihapus.
- Migration gateway dibuat mengikuti snapshot/designer yang ada; MigrateAsync privat sudah berhasil. Bila mengubah model, verifikasi kesesuaian migration, bukan menebak snapshot.
- Gateway build pernah gagal CS0023 pada redaction helper; sudah diperbaiki. Build berikutnya gagal karena asset `wwwroot` dihapus saat npm build berjalan paralel; menyelesaikan web build dahulu lalu backend menghasilkan build bersih. Tidak ada workaround menonaktifkan pemeriksaan.
- Native PDF/image limits mengurangi risiko tetapi bukan sandbox, jaminan bebas halusinasi, atau jaminan semua scan terbaca. Laporkan bagian tidak terbaca secara jujur.
- Tidak ada perubahan database/main secret/main process user untuk rollout 6C. `.vs` hanya dibaca untuk clone IIS; file user tidak diedit. Profil user menunjuk gateway HTTPS berbeda dari lab; URL itu dipertahankan.

## 9. Penutupan 2026-10-13

- Tiga edit terakhir yang semula belum dibangun sudah dibangun dan diuji. Source final: **328 test chatbot lulus, 354 test gateway lulus/1 skip opsional**, kedua backend 0 warning/error, kedua frontend build/lint dan check attachment/draft lulus.
- PDF campuran berhasil melalui browser → IIS Express InProcess → worker → gateway Go-only → **GLM-5.3-Flash**: DocumentRead job 17 Done retry 0, DocumentAnalyze job 18 Done retry 1, canonical membaca kedua halaman/kode/grafik/keputusan/sumber. Job 8 lama yang Failed retry 3 tetap dicatat sebagai bukti, bukan ditimpa keberhasilan baru.
- Timeout terlokalisasi pada penantian header inferensi upstream (gateway 499, TTFB kosong, batas attempt/total 60/120 detik). Variasi latency upstream belum diketahui; tidak terbukti bug parser/render/kontrak request. Retry otomatis kemudian mendapat HTTP 200; model/timeout/validasi tidak diganti demi smoke.
- JPEG canonical, download JWT identik original, perbesar/Escape/fokus, nama panjang, dan 390 px diverifikasi. Root overflow CSS diperbaiki; scrollWidth/body/composer 390. Admin vision yes → no → unknown → yes terverifikasi tanpa mengubah snapshot workflow yang sudah enqueue.
- Dua bug upload dari smoke IIS diperbaiki: chunked oversized 400 → **413**, multipart terputus 500 → **400**. Mapping IOException hanya pada pembacaan parser/section; 413 tidak tertelan, error disk tidak disamarkan sebagai error user. Empat regression permanen ditambahkan; upload 41/41, enqueue 10/10, body-limit gateway 6/6 lulus; abort FIN/RST tidak meninggalkan row/file.
- Watchdog proses nyata exit 76; sukses/exception tidak punya late kill, PDFium render nyata berhasil. Recovery/retry/stale/lease guarded writes teruji; sinyal OS **CTRL_BREAK/SIGQUIT** memberi exit 0, Pending retry 0 tanpa output/callback, lalu resume Done retry 0. Ini bukan pembuktian hang/AV PDF jahat, hard memory sandbox, atau Ctrl+C pada environment yang mewariskan ignore flag.
- Excel/CSV lama → Analyze/PDF **DeepSeek Go** berhasil, semua job Done retry 0; PDF JWT 200 `%PDF-`, profil sesuai fixture. Tidak ada model lain/Zen yang dipakai untuk acceptance.
- README, DESIGN, IMPROVEMENTS, GATEWAY, gateway/README, dan DECISIONS diperbarui agar mencerminkan source/kontrak/batas/bukti. File ini dipertahankan sebagai sejarah resume, bukan prosedur menyalakan kembali lab yang sudah usang.
- Tidak ada upgrade database/binari/proses/config/secret utama user untuk 6C. Linux belum diverifikasi. 6D PRD/Mermaid PNG, video, cloud, Docker/worker reuse, serta bug gateway ditunda tetap di luar pekerjaan ini.
- **Cleanup selesai:** lab utama 0 Pending/Processing job/tiket; service/tab/proses milik sesi sudah dihentikan. Database chatbot/gateway privat di §6 serta database watchdog `OperatorAi_6C_wd_d38af6b6`—**tiga database total**—dibuang dengan nama exact; root folder lab `operator-6c-qb79o4r8` dan seluruh fixture/binary/backup/scaffold dihapus dengan guard. Path/launcher/database di bagian historis tidak lagi dapat dipakai. Untuk rollout atau pekerjaan berikutnya, buat lab terisolasi baru; jangan menjalankan command lama pada database utama.
