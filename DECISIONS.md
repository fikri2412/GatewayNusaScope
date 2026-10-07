# Catatan Keputusan

> **Status terkini & langkah berikutnya ada di bagian "Status akhir sesi (handoff)" di paling bawah file ini.**

Diisi oleh agent setelah setiap fase. Format:

## YYYY-MM-DD — Fase N

- Yang dikerjakan:
- Keputusan dan alasannya:
- Penyimpangan dari DESIGN.md (bila ada):
- Hal yang perlu dicek manusia:

---

## 2026-10-06 — Fase 0

- Yang dikerjakan:
  - Kerangka solution dan 5 project (`Shared`, `Api`, `Worker.Operator`, `Worker.Specialist`, `Tests`) sesuai `SETUP.md`. Dokumen proyek tetap di root; folder `docs/` tidak dipakai (keputusan user).
  - Entity + `AppDbContext` (tabel/kolom snake_case), konstanta `Statuses`/`QueueTypes`/`JobTypes`, migration `Initial`, `database update` sukses. Tabel terverifikasi via `sqlcmd`: `conversations`, `messages`, `attachments`, `queue_items`, `jobs`, `ai_calls` (jumlah kolom sama dengan DDL).
  - Options class per section (`Shared/Options/AppOptions.cs`) di-bind dan divalidasi (`ValidateOnStart`) di API dan kedua worker. `AppDbContext` didaftarkan di API.
  - `web/` (Vite React TS) dengan `@microsoft/signalr`, `.env.development`, port 5173 strict.
  - `dotnet build`, `dotnet test`, `npm run build` sukses; API dan kedua worker start tanpa error validasi.
- Keputusan dan alasannya:
  - Solution berformat `OperatorAi.slnx` (SDK 10 terpasang: 7, 8, 9.0.315, 10.0.303). Semua project dikunci `net9.0`.
  - `dotnet-ef` global versi 10.0.11 dipakai terhadap EF Core 9.0.x; migration berjalan normal.
  - Database: `(localdb)\MSSQLLocalDB` (satu-satunya instance; tidak ada SQLEXPRESS atau instance default). Instance auto-create, database `OperatorAi` dibuat oleh migration.
  - Versi frontend: React 19.2, Vite 8.3, TypeScript 6.0, `@microsoft/signalr` 10.0.11 (React 19 diterima sesuai SETUP.md). Lint bawaan template: `oxlint`.
  - Kolom `is_visible` dan `notify_operator` tidak punya DEFAULT di database; nilai awal diset di CLR (`IsVisible = true`, `NotifyOperator = false`). Alasan: `HasDefaultValue(true)` pada `bool` membuat EF mengabaikan nilai `false` saat insert. `retry_count` tetap `DEFAULT 0`, `status` selalu dikirim eksplisit (`Pending`).
  - FK memakai `NO ACTION` (sesuai DDL, tanpa cascade). `ai_calls.conversation_id` tanpa FK (sesuai DDL).
  - Template `UseHttpsRedirection` dihapus dari API karena hanya ada profil http.
  - `Internal:Key` dibuat acak (32 byte hex) dan diset oleh agent ke user-secrets ketiga project agar validasi startup lulus.
  - `Ai:ApiKey` tidak divalidasi saat startup (belum dipakai sampai Fase 2); dipakai oleh worker mulai Fase 2.
- Penyimpangan dari DESIGN.md:
  - **Provider AI**: atas permintaan user, endpoint bukan Anthropic-compatible melainkan OpenAI Chat Completions OpenCode: `https://opencode.ai/inference/openai/v1/chat/completions` (`Ai:BaseUrl` = `https://opencode.ai/inference/openai/v1/`, `Ai:MessagesPath` = `chat/completions`, `Ai:Provider` = `OpenAiCompatible`). Model: `deepseek-v4.1-flash` (dari `Ai:Model`). Bagian 10 `DESIGN.md` menyebut implementasi format OpenAI sebagai kelas baru yang memenuhi `IAiClient`; ini harus dikerjakan di Fase 2 sebagai pengganti `AnthropicCompatibleClient`. Dampak yang perlu dirancang ulang di Fase 2: format `tools`/`tool_calls`/role `tool` (bukan `tool_use`/`tool_result`), header `Authorization: Bearer` (bukan `x-api-key`), blok `thinking` diganti field `reasoning_content`, serta bagian 8 (format `ai_content_json`) dan bagian 9.
  - Observasi uji langsung (satu panggilan dengan `max_tokens: 64`): respons 200, `finish_reason: "length"`, `content` kosong, seluruh token habis di `reasoning_content`. Model selalu berpikir; `Ai:MaxTokens` 4096 perlu diuji di Fase 2 dan opsi `Ai:Thinking` mungkin tidak berlaku di endpoint ini.
  - Paket tambahan di luar daftar bagian 2: `Microsoft.Extensions.Options.DataAnnotations` 9.x di `Shared`, untuk `ValidateDataAnnotations()`. Paket `coverlet.collector` bawaan template dihapus dari project test.
  - Folder `Shared/Options/` ditambahkan (tidak ada di bagian 3).
- Hal yang perlu dicek manusia:
  - **API key OpenCode** sudah diset ke user-secrets project `Worker.Operator` dan `Worker.Specialist` (key dikirim lewat chat, tidak ada di file repo). Karena sempat tertulis di percakapan, pertimbangkan merotasi key itu.
  - Panggilan uji ke `deepseek-v4-flash` menghasilkan `403 Model access is disabled`; `deepseek-v4.1-flash` berhasil (200).
  - Perintah `user-secrets` bila perlu diulang:
    ```powershell
    dotnet user-secrets set "Ai:ApiKey" "<api key>" --project src/OperatorAi.Worker.Operator
    dotnet user-secrets set "Ai:ApiKey" "<api key>" --project src/OperatorAi.Worker.Specialist
    dotnet user-secrets set "Internal:Key" "<string acak sama>" --project src/OperatorAi.Api
    dotnet user-secrets set "Internal:Key" "<string acak sama>" --project src/OperatorAi.Worker.Operator
    dotnet user-secrets set "Internal:Key" "<string acak sama>" --project src/OperatorAi.Worker.Specialist
    ```
  - Semua dokumen (`AGENTS.md`, `DESIGN.md`, `DECISIONS.md`, `SETUP.md`) ada di root repo.

## 2026-10-07 — Gateway G0 (Fondasi)

- Yang dikerjakan:
  - `gateway/` (solution `AiGateway.slnx`): `AiGateway.Core` (entity, `GatewayDbContext`, options, `DbSeeder`), `AiGateway.Api` (port 5090, `/healthz`), `AiGateway.Tests`, `web/` (Vite React TS, port 5174, build ke `src/AiGateway.Api/wwwroot`). Tidak ada referensi ke `OperatorAi.*`.
  - Database `AiGateway` (LocalDB `(localdb)\MSSQLLocalDB`), migration `Initial`, 18 tabel Tingkat 1 sesuai `GATEWAY.md` bagian 6 (diverifikasi lewat `sqlcmd`: 18 tabel, 12 CHECK constraint, 5 indeks unik berfilter).
  - Seed idempoten: plan `Default` (tanpa batas) dan satu platform admin `admin@gateway.local`. Password acak dibuat agent dan disimpan di user-secrets (`Seed:AdminPassword`, project `AiGateway.Api`); baca dengan `dotnet user-secrets list --project gateway/src/AiGateway.Api`.
  - Verifikasi: `dotnet build` 0 warning, `dotnet test` 7 lulus (isolasi tenant, constraint, seeder), `npm run build` sukses, API start dan `GET /healthz` 200.
- Keputusan dan alasannya:
  - Isolasi tenant fail-closed: query filter global memakai `GatewayDbContext.CurrentTenantId`; selama belum diset (`-1`) query entity milik tenant kosong. Kode platform memakai `IgnoreQueryFilters()` eksplisit. `SaveChanges` mengisi `TenantId` dari konteks, menolak tulis lintas tenant dan perubahan `TenantId`.
  - `scope_id` di `policies` selalu terisi (id tenant untuk scope `tenant`) agar unik `(tenant_id, scope, scope_id)` berlaku.
  - `usage_logs` dan `usage_daily` tanpa FK (ditulis tiap permintaan). `usage_daily.model_id = 0` untuk model yang belum terselesaikan.
  - Semua FK `NO ACTION`; soft delete (`deleted_at`) untuk `providers`, `models`, `projects` dengan indeks unik berfilter.
  - Kunci Data Protection disimpan di tabel `data_protection_keys` (`PersistKeysToDbContext`) agar ikut backup.
  - Warning `ConfigureWarnings` untuk interaksi query filter dan relasi wajib dimatikan di `UseGatewaySqlServer` (relasi hanya lewat FK, tanpa navigation).
- Penyimpangan dari GATEWAY.md:
  - Paket tambahan: `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`, `Microsoft.Extensions.Identity.Core` (`PasswordHasher<T>`), `Microsoft.Extensions.Options.DataAnnotations`, `Microsoft.AspNetCore.Mvc.Testing` (test). `Core` memakai `FrameworkReference Microsoft.AspNetCore.App`. `JwtBearer` ditambahkan saat G3.
- Hal yang perlu dicek manusia:
  - **Kunci Data Protection tersimpan sebagai XML polos** (log: "No XML encryptor configured"). Dump database berisi kunci dan ciphertext sekaligus. Wajib diperbaiki di G4 (sertifikat atau DPAPI); dicatat di checklist G4. Jangan dipakai untuk tenant nyata sebelum itu.
  - Key OpenCode di chatbot masih di user-secrets worker; akan dipindah ke data provider tenant uji di G1.

## 2026-10-07 — Gateway G1 (Proxy inti + katalog)

- Yang dikerjakan:
  - Data plane: `POST /v1/chat/completions` (non-streaming) dan `GET /v1/models`, auth `Bearer gw_<prefix>_<secret>`, error berformat OpenAI, header `X-Request-Id`. Pipeline: validasi → model alias → route berurut (priority, lalu acak berbobot) → forwarding dengan fallback → `usage_logs` + `usage_daily` dalam satu transaksi.
  - `ProvisioningService` (tenant, provider dari template atau custom, rotasi key tanpa downtime, model + route + harga, project, API key klien, discover model via `models_path`). Dipakai admin API di G3.
  - Master data: `provider_templates` + `catalog_models`; sinkronisasi dari OpenCode Console API (`OpenCodeCatalogSync`). Live: 112 model dari 2 provider (`opencode` 83, `opencode-go` 29); 33 model berkeluarga `openai_chat` dan aktif dapat diimpor, 65 berkeluarga lain tersimpan tapi belum diteruskan.
  - Harga: tier konteks (`min_input_tokens`), harga cache read, token ter-cache ditagih harga cache.
  - Verifikasi: 58 test lulus (isolasi tenant, auth, penolakan, IP allow list, fallback, passthrough, enkripsi key, rotasi, katalog, sinkronisasi, harga). Uji nyata lewat gateway ke OpenCode: `deepseek-v4.1-flash` (200, biaya 0.000072 = 36x0.3 + 51x1.2 per juta) dan `glm-5.3-flash` (biaya 0.0000347).
- Temuan OpenCode Console API (https://opencode.ai/v2/docs/console/api/):
  - Base `https://opencode.ai/console`; kredensial service account `oc_sk_...` terikat satu workspace (tanpa header workspace). Izin **Inference only** boleh memanggil inferensi, provider custom, dan `GET /api/v2/config`; izin **All** juga boleh API Console (budgets).
  - `GET /api/v2/config` mengembalikan `providers.<key>.models.<id>` dengan `name`, `capabilities` (tools, input, output), `cost[]` (USD per 1M token: `input`, `output`, `cache.read/write`; elemen `tier.type=context,size=N` berlaku bila prompt > N), `limit` (context, input, output), `disabled`, serta `package` + `settings.baseURL` per model yang menentukan keluarga API (`openai-compatible` = Chat Completions, `openai` = Responses, `anthropic` = Messages, `google`).
  - `deepseek-v4-flash` berstatus `disabled: true` di workspace ini; itu penyebab `403 Model access is disabled` di Fase 0. `deepseek-v4.1-flash` aktif.
  - Inferensi tersedia di `/inference/openai|anthropic|google/...`; provider kustom workspace di `/inference/custom/<connection_id>/...`. `GET /inference/v1/models` publik (tanpa auth) hanya berisi id.
  - Budgets API (`GET/PUT/DELETE /api/v1/budgets/members`) membaca dan mengubah budget per anggota (microcents: 100.000.000 per USD). Belum dipakai; kandidat untuk rekonsiliasi biaya provider vs `usage_daily`.
- Keputusan dan alasannya:
  - Gateway hanya meneruskan `openai_chat`. Model `openai_responses`/`anthropic_messages`/`google_generate` disimpan di katalog dengan `api_family` agar adaptor bisa ditambah tanpa migrasi, tetapi tidak ditawarkan saat impor.
  - Sinkronisasi memakai key yang diberikan pemanggil dan tidak menyimpannya. Baris katalog bersumber `manual` tidak pernah ditimpa.
  - Parameter `decimal` bawaan EF/SqlClient memotong ke 2 desimal; `usage_daily.cost` awalnya tersimpan 0. Diperbaiki dengan `SqlParameter` berpresisi eksplisit + test regresi. Baris `usage_daily` lama di DB dev (satu baris deepseek) tetap 0.
  - Penolakan setelah key valid dicatat ke `usage_logs`/`usage_daily` (kolom `denied`); key tak dikenal tidak dicatat agar tidak bisa membanjiri log tenant.
  - Pencatatan pemakaian tidak memakai token pembatalan request: biaya upstream sudah terjadi walau klien memutus koneksi.
  - Migration `Initial` gateway di-reset dua kali selama G1 (belum ada data produksi); DB dev `AiGateway` dibuat ulang.
- Penyimpangan dari GATEWAY.md:
  - Tabel `provider_templates`/`catalog_models` dan kolom tier/cache ditambahkan (bagian 6 sudah diperbarui). Plan limit dan kebijakan menyusul di G2.
- Hal yang perlu dicek manusia:
  - **Key OpenCode di chat ini berizin All** (permintaan ke `GET /api/v1/budgets/members` dijawab 200; isi daftar anggota tidak dibaca/disalin). Key itu bisa mengubah budget anggota workspace. Rotasi key, lalu buat service account baru dengan izin **Inference only** untuk gateway dan chatbot. Tidak ada key sungguhan di file repo; key dev ada di `dotnet user-secrets` (`DevSeed:UpstreamApiKey` di gateway, `Ai:ApiKey` di kedua worker chatbot).
  - Key gateway dev (`gw_...`) tercetak sekali di log saat seed pertama; bila hilang, hapus database `AiGateway` dan jalankan ulang (Development).

## 2026-10-07 — Gateway G2 (Kebijakan)

- Yang dikerjakan:
  - `PolicyEngine`: daftar model, batas token per request, kuota token harian dan bulanan, anggaran bulanan, batas plan tenant (request/token per bulan), rate limit per menit. Dievaluasi di `ChatProxy` setelah model ditemukan dan sebelum forwarding; penolakan tercatat di `usage_logs.denied_reason` sebagai `kode:scope` (mis. `daily_quota_exceeded:key`).
  - `RequestRateLimiter` (sliding window counter di memori, hitungan hanya naik bila semua scope lolos).
  - `ProvisioningService.SetPolicyAsync` (upsert + validasi, target harus milik tenant); batas plan `max_projects` dan `max_api_keys` (key yang dicabut tidak dihitung) di pembuatan project/key.
  - Verifikasi: 74 test lulus (16 baru: irisan daftar model, batas token terkecil dan dipaksakan, kuota per scope, plan, anggaran, rate limit, validasi, isolasi tenant pada SetPolicy).
- Keputusan dan alasannya:
  - Semua scope dievaluasi sendiri-sendiri, bukan "scope paling spesifik menang" (menggantikan teks lama di `GATEWAY.md`): project tidak bisa melonggarkan batas tenant. Konsekuensi: daftar model = irisan, batas token = terkecil.
  - Urutan pemeriksaan: daftar model, kuota/plan/anggaran (baca `usage_daily`), rate limit paling akhir, supaya penolakan lain tidak menghabiskan jatah rate limit.
  - Permintaan yang ditolak tidak memakai jatah request plan (`requests - denied`).
  - Batas kebijakan dipaksakan walau klien tidak mengirim `max_tokens`; batas model saja tidak menambah field (perilaku G1).
- Batas yang diketahui (ditandai `ponytail:` di kode):
  - Kuota dan anggaran bersifat soft: request yang sedang berjalan bisa melewati batas sedikit. Batas keras perlu reservasi token sebelum forwarding.
  - Rate limiter hanya akurat untuk satu instance gateway (state di memori, satu lock). Beberapa instance perlu penyimpanan bersama.
  - Anggaran menjumlahkan `cost` tanpa memisahkan mata uang; semua harga saat ini USD.
- Hal yang perlu dicek manusia: tidak ada tambahan.

## 2026-10-07 — Gateway G3 (Management)

- Yang dikerjakan:
  - Auth admin: `POST /admin/api/auth/login|refresh|logout|accept-invite|reset-password`, `change-password`, `me`. JWT HS256 berumur pendek (`Auth:AccessTokenMinutes`) + refresh token sekali pakai (rotasi; pemakaian ulang token lama = semua sesi dicabut), kunci akun setelah `Auth:MaxFailedLogins` gagal, `security_stamp` membatalkan token saat password/role berubah.
  - API admin tenant: provider (template/custom, rotasi key, discover, import model dari katalog/discovered), model (route + prioritas/bobot, harga bertingkat + riwayat), project, API key (plaintext tampil sekali), policy per scope (tenant/project/key), usage (summary/logs/export CSV), pengguna (invite/role/reset), audit.
  - API platform: plan CRUD (hapus ditolak bila dipakai), tenant create/patch/suspend + undang owner + reset password, usage lintas tenant, audit instalasi, template + katalog (upsert manual, sinkronisasi OpenCode tanpa menyimpan service key).
  - UI admin React (`gateway/web`) lengkap: login/undangan/reset, dashboard rentang hari ini/7 hari/bulan, provider, model, project, API key, policies, usage + ekspor, users, audit, alerts/webhooks, dan halaman platform (tenant, plan, template, katalog + sinkronisasi, pemakaian, audit). Sesi di memori (tanpa localStorage), refresh single-flight, navigasi sadar-peran; viewer hanya baca.
  - Verifikasi: `dotnet build` 0 warning/0 error; `dotnet test` 219 lulus (isolasi lintas tenant untuk SEMUA endpoint admin/platform, penolakan peran, secret tidak pernah kembali di respons/audit, invite/reset sekali pakai, lockout, konfigurasi penuh lewat UI); `npm run build` sukses; smoke nyata: login platform/owner/viewer, buat tenant + redeem undangan, CRUD alert dari UI, panggilan data plane ke OpenCode (200, biaya tercatat di `usage_logs`/`usage_daily`).
- Keputusan dan alasannya:
  - Semua id resource yang tampil di API memakai `public_id` (GUID); id internal tidak pernah keluar. Id platform (plan/template/katalog/user) tetap numerik karena tidak pernah dipakai lintas tenant.
  - Endpoint detail/aksi lintas tenant selalu 404 (bukan 403) supaya keberadaan resource tenant lain tidak bocor.
  - `PUT /admin/api/policies/{scope}/{id}` mengembalikan 200 + DTO (bukan 204) karena UI memakai hasil upsert.
  - Viewer boleh membaca audit tenant; manajemen pengguna hanya owner.
  - UI memakai router hash internal (tanpa dependensi react-router) dan primitif native (`<dialog>`, `<table>`, `<select>`) — tidak ada paket baru.
- Penyimpangan dari GATEWAY.md: tidak ada yang berarti; tambahan endpoint `change-password` dan `me` (perlu untuk UI), serta halaman alerts/webhooks dari G4.
- Hal yang perlu dicek manusia: password platform admin seed ada di user-secrets (`Seed:AdminPassword`, project `AiGateway.Api`); key gateway dev tercetak sekali di log saat seed pertama.

## 2026-10-07 — Gateway G4 (Pengerasan)

- Yang dikerjakan:
  - **Data Protection**: kunci disimpan di database dan dienkripsi sebelum disimpan — DPAPI (Windows, current user) atau sertifikat bila `Security:KeyCertificateThumbprint` diisi; startup gagal di luar Windows tanpa sertifikat. Ciphertext provider tidak lagi berupa XML polos.
  - **Anti-SSRF** (`OutboundSecurity`): validasi statis saat provider/katalog dikonfigurasi (https wajib, tanpa userinfo/query/fragment, host literal privat ditolak) + validasi ulang tepat sebelum socket dibuka lewat `ConnectCallback` (host di-resolve di situ dan hanya alamat yang lolos yang disambung; redirect dan proxy lingkungan dimatikan). Rentang privat/loopback/link-local/multicast/NAT64/documentation ditolak; pengecualian hanya lewat `Security:AllowedPrivateNetworks` (CIDR/IP/nama host). Berlaku untuk trafik upstream, discover, sinkronisasi katalog, dan webhook.
  - **RLS SQL Server** (`gateway/sql/tenant-security.sql`): role `gateway_app`/`gateway_platform`/`gateway_migration`, predikat `rls.fn_tenant_access` (SESSION_CONTEXT `tenant_id` ATAU keanggotaan `gateway_platform`; flag session apa pun diabaikan), FILTER + BLOCK INSERT/UPDATE untuk semua tabel tenant (termasuk tabel maintenance G4), `audit_logs` append-only untuk app (DENY UPDATE/DELETE), dan prosedur `dbo.api_key_bootstrap` (`EXECUTE AS` user pembaca) sebagai satu-satunya pembacaan sebelum tenant diketahui. `TenantSessionInterceptor` memasang SESSION_CONTEXT per koneksi (pooled/terbuka) dan fail-closed saat tanpa tenant.
  - **Maintenance**: `request_bodies` opt-in per project (`log_content`) dengan masa simpan sendiri, retensi `usage_logs` yang merekonsiliasi `usage_daily` lebih dulu (agregat historis tetap utuh), `job_runs`, alert kuota (daily/monthly token, budget; dedup per rule+periode+ambang), webhook HTTPS dengan tanda tangan HMAC + retry/backoff dan log pengiriman. Worker latar berjalan per siklus; koneksi platform opsional lewat `ConnectionStrings:GatewayPlatform`.
  - **Pengerasan HTTP**: rate limit login per IP (429 + `Retry-After`), batas ukuran body admin, header keamanan (CSP, nosniff, frame deny, referrer), `Cache-Control: no-store` untuk API admin, metrik dasar `gateway.http.requests`/`gateway.http.duration`.
  - Uji keamanan manual (dijalankan, bukan hanya test): brute force 5x gagal → akun terkunci (429 `account_locked`); SSRF http/127.0.0.1/link-local/userinfo → 400 `invalid_base_url`; IDOR project tenant lain → 404; token platform di `/admin/api` → 403; pemindaian log tidak menemukan key provider maupun key klien (hanya baris seed dev sekali yang memang disengaja).
- Keputusan dan alasannya:
  - RLS bersifat **opt-in per environment**: script dijalankan setelah migration (idempoten, satu batch, tanpa `GO`). Selama script belum dijalankan, aplikasi tetap jalan (ada fallback `2812` di `ApiKeyAuthenticator`), tetapi tanpa lapis kedua.
  - Cross-tenant di plane platform (laporan/audit lintas tenant) dan job latar **membutuhkan principal yang menjadi anggota `gateway_platform`**; job maintenance memakainya lewat `ConnectionStrings:GatewayPlatform`. Plane platform HTTP belum memakai koneksi terpisah — sebelum RLS diaktifkan di produksi, sambungkan koneksi platform ke endpoint platform (upgrade path: registrasi DbContext kedua dengan login `gateway_platform`).
  - `EncryptExistingKeysAsync` (membungkus ulang kunci lama di tempat) **dibatalkan**: format XML internal Data Protection tidak stabil untuk ditulis ulang, dan percobaan itu sempat merusak kunci di database dev. Kunci lama dibiarkan; cara aman mengganti kunci adalah rotasi (kunci baru otomatis terenkripsi).
  - Tabel maintenance (`request_bodies`, `alert_rules`, `alert_events`, `webhooks`, `webhook_deliveries`, `job_runs`) masuk Tingkat 2 → ditambahkan lebih awal karena diminta di G4; `job_runs` sengaja tidak ditutup RLS (tanpa `tenant_id`).
  - Webhook memakai handler anti-SSRF yang sama dengan upstream; payload alert tidak pernah memuat isi prompt atau key.
- Batas yang diketahui (ditandai `ponytail:` di kode):
  - Fallback `api_key_bootstrap` (error 2812) hanya untuk database tanpa script RLS; hapus saat semua environment menjalankan script.
  - Interceptor mengabaikan error "database sedang di-drop" (596/233/4060/911/3701/3702) dan membersihkan pool; error lain tetap dilempar.
  - Alert dan retensi berjalan per siklus worker (menit), bukan real-time.
- Hal yang perlu dicek manusia:
  - Sebelum RLS diaktifkan di produksi: buat login aplikasi (`gateway_app`), login platform (`gateway_platform`), jalankan `gateway/sql/tenant-security.sql`, lalu isi `ConnectionStrings:GatewayPlatform` untuk job latar dan sambungkan plane platform.
  - Pertimbangkan menurunkan level log kategori `Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware` supaya 4xx yang memang diharapkan tidak dicatat sebagai `fail`.

## 2026-10-07 — Gateway G5 (Handover)

- Yang dikerjakan:
  - `gateway/README.md`: panduan pasang (prasyarat, build, migration, secret per kunci konfigurasi), menjalankan sebagai Windows Service, mengaktifkan RLS (login aplikasi/platform, jalankan script, koneksi platform), operasional harian (worker, retensi, alert), backup/restore (database + kunci Data Protection + DPAPI/sertifikat), prosedur upgrade, dan troubleshooting.
  - `gateway/scripts/install-service.ps1`: publish + `sc.exe create` service dengan `ASPNETCORE_URLS`, auto-restart saat gagal, dan mode `-Uninstall`. Secret tidak pernah ditulis script.
  - Contoh integrasi: `gateway/examples/curl.md`, `gateway/examples/python/example.py` (stdlib), `gateway/examples/dotnet/` (console .NET 9).
  - Verifikasi: kedua contoh dijalankan sungguhan terhadap gateway hidup (200, `deepseek-v4.1-flash`, usage tercatat); script PowerShell dan contoh Python lolos pemeriksaan sintaks; build dan test suite dijalankan ulang.
- Keputusan dan alasannya:
  - Dokumentasi handover diletakkan di `gateway/README.md` (produk mandiri), bukan di root, supaya bisa diserahkan bersama folder `gateway/`.
  - Contoh memakai stdlib/`HttpClient` tanpa paket tambahan; contoh .NET berdiri sendiri di luar solution dan tidak ikut warnings-as-errors.
  - Script service memakai `sc.exe` (bawaan Windows) alih-alih `New-Service` agar sama untuk semua versi PowerShell.
- Hal yang perlu dicek manusia:
  - Uji pasang di mesin bersih (tanpa SDK dev): publish, isi secret lewat registry/Environment service, jalankan `sql/tenant-security.sql`, lalu login dan panggil data plane.
  - Mesin ini memblokir eksekusi apphost `.exe` (`Access is denied`); jalankan lewat `dotnet <app>.dll` atau whitelist exe. Sudah dicatat di troubleshooting README.

## 2026-10-07 — Status akhir sesi (handoff)

Ringkasan untuk sesi berikutnya. **Fase gateway G0–G5 selesai dikerjakan**; chatbot (`DESIGN.md` Fase 2) belum disentuh.

**Yang sudah ada dan terverifikasi**

- `gateway/` (Core, Api, Tests, web, sql, scripts, examples, README). Build `dotnet build` 0 warning/0 error (warnings-as-errors), `dotnet test` **219 lulus**, `npm run build` sukses, chatbot `dotnet build` tetap bersih.
- G3: auth JWT + refresh + lockout, API admin & platform lengkap, UI admin lengkap (sadar-peran; viewer hanya baca). Smoke nyata: login platform/owner/viewer, buat tenant + redeem undangan, CRUD alert dari UI.
- G4: Data Protection (DPAPI/sertifikat), anti-SSRF (`OutboundSecurity`), RLS SQL Server (`sql/tenant-security.sql`) + `TenantSessionInterceptor`, maintenance (retensi, alert, webhook, job_runs), rate limit login per IP, batas body, header keamanan, metrik. Uji keamanan manual lulus: brute force → 429 `account_locked`; SSRF (http/127.0.0.1/link-local/userinfo) → 400 `invalid_base_url`; IDOR → 404; token platform di `/admin/api` → 403; log bebas key.
- G5: `gateway/README.md` (pasang, service, RLS, operasional, backup/restore, upgrade, troubleshooting), `scripts/install-service.ps1` (lolos parse), contoh `examples/curl.md`, `examples/python/example.py`, `examples/dotnet/` — kedua contoh dijalankan sungguhan ke gateway hidup (200, usage tercatat).
- Perubahan paling akhir (sudah diverifikasi): rate limit `auth-ip` dipasang ke endpoint `login`/`refresh`/`accept-invite`/`reset-password`; `Security:AuthRequestsPerMinute` default 20 (harness test memakai 100000); `RequestProtectionOptions` range 1–1.000.000; `TestDb.DisposeAsync` membersihkan pool **per database** (bukan `ClearAllPools`) agar tidak memutus test class lain yang paralel.

**Langkah berikutnya yang wajar**

1. Uji pasang di mesin bersih sesuai `gateway/README.md` (publish → service → secret → `sql/tenant-security.sql` → login → panggil data plane). Ini satu-satunya kriteria G5 yang belum dijalankan.
2. Bila RLS akan diaktifkan di produksi: sambungkan plane platform HTTP ke koneksi principal `gateway_platform` (saat ini baru job latar lewat `ConnectionStrings:GatewayPlatform`); endpoint laporan/audit lintas tenant akan kosong tanpa itu.
3. Fase 2 chatbot: arahkan `Ai:BaseUrl` worker ke gateway dan sesuaikan `DESIGN.md` §5/§9/§10 (tabel `ai_models`/`ai_policies`/`ai_calls` tidak dibuat di database chatbot).
4. Rotasi key OpenCode (masih berizin All) dan buat service account izin **Inference only** untuk gateway dan chatbot.
5. Ditunda sejak awal: streaming `stream: true`, adaptor `anthropic_messages`/`openai_responses`/`google_generate`, guardrail, invoice.

**Cara cepat menjalankan**

```powershell
cd gateway
dotnet build; dotnet test                       # butuh LocalDB
dotnet run --project src/AiGateway.Api          # dev: http://localhost:5090 (launchSettings)
# produksi/verifikasi DLL: jalankan dari folder output (appsettings ada di sana)
cd src/AiGateway.Api/bin/Debug/net9.0; $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet AiGateway.Api.dll
```

- Password platform admin dev: `dotnet user-secrets list --project gateway/src/AiGateway.Api` (`Seed:AdminPassword`).
- Key gateway dev dicetak sekali saat seed pertama; hilang → drop database `AiGateway` lalu start lagi (Development).
- Catatan mesin ini: eksekusi apphost `.exe` diblokir (`Access is denied`) → jalankan lewat `dotnet <app>.dll`.
