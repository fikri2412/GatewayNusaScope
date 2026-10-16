# AI Gateway — Panduan Pasang dan Operasional

Gateway multi-tenant: satu pintu antara aplikasi pelanggan dan provider AI (BYOK).
Spesifikasi produk ada di `../GATEWAY.md`; keputusan teknis di `../DECISIONS.md`.

- [1. Prasyarat](#1-prasyarat)
- [2. Pasang](#2-pasang)
- [3. Konfigurasi dan secret](#3-konfigurasi-dan-secret)
- [4. Menjalankan sebagai Windows Service](#4-menjalankan-sebagai-windows-service)
- [5. Row-Level Security (opsional, disarankan di produksi)](#5-row-level-security-opsional-disarankan-di-produksi)
- [6. Operasional harian](#6-operasional-harian)
- [7. Backup dan restore](#7-backup-dan-restore)
- [8. Upgrade](#8-upgrade)
- [9. Troubleshooting](#9-troubleshooting)
- [10. Streaming (SSE)](#10-streaming-sse)
- [11. Contoh integrasi](#11-contoh-integrasi)
- [12. Web Search Provider Hub](#12-web-search-provider-hub)

---

## 1. Prasyarat

| Kebutuhan | Versi | Catatan |
| --- | --- | --- |
| .NET SDK | 9.0.x | `dotnet --list-sdks` |
| `dotnet-ef` | 9.0.x | `dotnet tool install --global dotnet-ef --version 9.0.20`; dipakai `dotnet ef database update` |
| SQL Server | 2019+ / LocalDB | Database terpisah `AiGateway` |
| Node.js | 20+ | hanya untuk membangun UI (`gateway/web`) |
| Windows | 10/Server 2019+ | untuk DPAPI & Windows Service; Linux butuh sertifikat (lihat §3) |

## 2. Pasang

```powershell
cd gateway
dotnet build                                    # 0 warning (warnings-as-errors)
dotnet test                                     # seluruh test suite (jumlah terbaru di DECISIONS.md); butuh LocalDB
cd web; npm ci; npm run build; cd ..            # UI -> src/AiGateway.Api/wwwroot
```

Database:

```powershell
# Development: AutoMigrate=true sudah menjalankan migration saat start.
# Produksi: jalankan migration secara terkontrol:
dotnet ef database update --project src/AiGateway.Core --startup-project src/AiGateway.Api
```

Platform admin pertama dibuat otomatis dari `Seed:AdminEmail` + `Seed:AdminPassword` (hanya bila belum ada).
Data dev (tenant contoh + katalog OpenCode) hanya dibuat bila `DevSeed:UpstreamApiKey` diisi dan environment Development.

## 3. Konfigurasi dan secret

Semua secret lewat `dotnet user-secrets` (dev) atau variabel lingkungan (produksi). Tidak ada secret di file yang di-commit.

```powershell
dotnet user-secrets set "ConnectionStrings:Gateway" "Server=.;Database=AiGateway;Trusted_Connection=True;TrustServerCertificate=True" --project src/AiGateway.Api
dotnet user-secrets set "Jwt:SigningKey" "<acak >= 32 karakter>" --project src/AiGateway.Api
dotnet user-secrets set "Seed:AdminEmail" "admin@example.com" --project src/AiGateway.Api
dotnet user-secrets set "Seed:AdminPassword" "<minimal 12 karakter>" --project src/AiGateway.Api
```

| Kunci | Arti | Default |
| --- | --- | --- |
| `ConnectionStrings:Gateway` | database aplikasi (wajib) | — |
| `ConnectionStrings:GatewayPlatform` | koneksi principal `gateway_platform`: job latar, laporan lintas tenant dan audit tanpa tenant di plane platform (wajib bila RLS aktif) | memakai koneksi aplikasi |
| `Jwt:SigningKey` | kunci tanda tangan access token (wajib, >= 32 karakter) | — |
| `Security:KeyCertificateThumbprint` | sertifikat X.509 untuk enkripsi kunci Data Protection (wajib di luar Windows) | DPAPI (Windows) |
| `Security:AllowedPrivateNetworks` | CIDR/IP/nama host yang boleh dihubungi walau privat (mis. provider internal) | kosong |
| `Security:TrustedProxies` | CIDR/IP reverse proxy yang header `X-Forwarded-For`/`X-Forwarded-Proto`-nya dipercaya (menentukan `client_ip`, allow-list IP API key, dan rate limit login) | hanya loopback |
| `Security:AuthRequestsPerMinute` | rate limit endpoint login per IP | 20 |
| `Security:MaxAdminRequestBytes` | batas body API admin | 1 MB |
| `Proxy:MaxRequestBytes` / `Proxy:MaxResponseBytes` | batas body permintaan klien dan respons upstream; permintaan ditegakkan per byte saat dibaca, jadi juga berlaku untuk body chunked dan host seperti IIS | 4 MB / 8 MB |
| `Proxy:UpstreamTimeoutSeconds` | deadline satu route sampai headers + body REST selesai (juga body error / event SSE pertama sebelum commit); setelah commit SSE menjadi batas diam antar baris | 120 s |
| `Proxy:MaxStreamSeconds` | durasi maksimum pompa SSE, termasuk read, write, flush dan pengiriman error terminal | 900 |
| `Proxy:MaxConcurrentPerTenant` | permintaan chat bersamaan per tenant; kelebihan ditolak 429 `concurrency_limit_exceeded` | 64 |
| `Auth:*` | `MaxFailedLogins`, `LockoutMinutes`, `AccessTokenMinutes`, `RefreshTokenDays`, `MinPasswordLength` | 5 / 15 / 15 / 14 / 12 |
| `Maintenance:*` | masa simpan body/usage/job/delivery, jadwal worker, batas webhook | lihat `MaintenanceOptions` |
| `Database:AutoMigrate` | jalankan migration saat start (dev) | `false` |

## 4. Menjalankan sebagai Windows Service

```powershell
# PowerShell sebagai Administrator
cd gateway
.\scripts\install-service.ps1 -ServiceName AiGateway -Port 5090 -PublishDir C:\AiGateway

# Konfigurasi produksi (variabel lingkungan dibaca service):
#   ConnectionStrings__Gateway, Jwt__SigningKey, Seed__AdminEmail, Seed__AdminPassword, ...
# Atau salin appsettings.Production.json (tanpa secret) ke folder publish.
```

Script akan `dotnet publish`, membuat service (`sc.exe create`) dengan `ASPNETCORE_URLS`, lalu start.
Uninstall: `.\scripts\install-service.ps1 -Uninstall -ServiceName AiGateway`.

Aplikasi memakai `UseWindowsService()` (paket `Microsoft.Extensions.Hosting.WindowsServices`), jadi service terhubung ke
Service Control Manager dan bisa start/stop dengan benar; `sc.exe failure` di script membuat SCM me-restart proses bila gagal.
Service membaca konfigurasi dari variabel lingkungan (cara aman untuk secret), dari `appsettings*.json` di folder publish
(content root service = folder publish), dan dari user-secrets hanya bila environment `Development`.

## 5. Row-Level Security (opsional, disarankan di produksi)

`sql/tenant-security.sql` adalah lapis kedua isolasi tenant di database: FILTER + BLOCK untuk 20 tabel tenant
(11 tabel inti + 5 tabel maintenance + `app_settings`, `app_status_reports`, `search_provider_configs`, `search_provider_states`), `audit_logs` append-only, dan prosedur bootstrap API key.
`users`, `refresh_tokens`, `user_tokens` (login/user bersifat global; platform admin ber-`tenant_id` NULL), `tenants`,
dan `job_runs` sengaja **di luar** RLS — kelimanya hanya bergantung pada filter aplikasi; header script adalah acuan.
Idempoten, satu batch, jalankan **setelah** migration:

```powershell
sqlcmd -S <server> -d AiGateway -E -i sql\tenant-security.sql
```

Lalu (sekali per instalasi):

```sql
CREATE LOGIN gateway_app_login WITH PASSWORD = '<secret>';
CREATE USER  gateway_app_user FOR LOGIN gateway_app_login;
ALTER ROLE gateway_app ADD MEMBER gateway_app_user;

CREATE LOGIN gateway_platform_login WITH PASSWORD = '<secret>';
CREATE USER  gateway_platform_user FOR LOGIN gateway_platform_login;
ALTER ROLE gateway_platform ADD MEMBER gateway_platform_user;
```

- Aplikasi memakai `gateway_app_user` (jangan tambahkan ke `db_owner`/`db_datawriter`).
- Job latar **dan plane platform HTTP** (`/platform/api`) memakai `gateway_platform_user` lewat `ConnectionStrings:GatewayPlatform`. Di plane platform koneksi itu hanya dipakai untuk dua hal: laporan lintas tenant (`/platform/api/usage`, `/platform/api/audit`) dan baris audit tanpa tenant (aksi platform, login platform admin, login gagal, accept-invite). Semua endpoint lain, termasuk endpoint tenant, tetap di koneksi aplikasi. Tanpa `GatewayPlatform`, dengan RLS aktif laporan itu kosong dan baris audit tanpa tenant ditolak predikat RLS lalu dicatat sebagai Error di log, bukan di `audit_logs`. Kredensial `GatewayPlatform` yang salah membuat laporan itu 500 (aksi lain tetap jalan; baris audit tanpa tenant hanya dicatat sebagai Error).
- Migration dijalankan oleh principal `gateway_migration` (db_ddladmin).
- Jalankan ulang script setiap kali ada tabel tenant baru (mis. setelah upgrade G4+).

## 6. Operasional harian

- **Worker latar** (`MaintenanceWorker`) berjalan di dalam proses API: retensi `request_bodies`/`usage_logs`/`job_runs`/`webhook_deliveries`,
  evaluasi alert kuota, dan pengiriman webhook. Hasil tiap siklus tercatat di `job_runs`.
- **Isi prompt** hanya disimpan bila `projects.log_content = 1`, dengan `content_retention_days` (atau `Maintenance:DefaultContentRetentionDays`). Gambar inline (data URL base64) tidak pernah ikut tersimpan: isinya diganti penanda `[inline image omitted]` saat body dicatat, walau upstream tetap menerima gambar asli.
- **Alert**: rule per tenant/project/key (`daily_tokens`, `monthly_tokens`, `monthly_budget`) dengan ambang persen; event dedup per periode+ambang.
- **Log**: 4xx yang memang diharapkan (login gagal, penolakan kebijakan) tercatat di `audit_logs`/`usage_logs`;
  pertimbangkan menurunkan level kategori `Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware` ke `Warning` bila log terlalu ramai.
- **Health**: `GET /healthz` (200 bila database bisa dihubungi).
- **Batas satu instance**: rate limit kebijakan, gerbang konkurensi per tenant (`Proxy:MaxConcurrentPerTenant`), dan klaim pengiriman
  webhook disimpan di memori proses — menjalankan lebih dari satu instance gateway melemahkan ketiganya (perlu state bersama).
- **Webhook**: tiap pengiriman membawa `X-Gateway-Timestamp` (Unix **detik**) dan `X-Gateway-Signature: sha256=<hex>`, yaitu
  HMAC-SHA256 atas string `"{timestamp}.{payload}"` dengan secret webhook tenant. Penerima **harus** menolak timestamp basi
  (mis. selisih > 5 menit) agar payload lama tidak bisa diputar ulang, dan membandingkan tanda tangan secara constant-time.
  Kegagalan 408/429/5xx dicoba ulang dengan backoff sampai `Maintenance:WebhookMaxAttempts`; riwayat ada di `webhook_deliveries`.
- **Worker + RLS**: bila RLS aktif tetapi `ConnectionStrings:GatewayPlatform` kosong, job lintas tenant (retensi
  `usage_logs`/`request_bodies`/`job_runs`/`webhook_deliveries`, evaluasi alert, pengiriman webhook) tidak melihat baris tenant
  mana pun — job menjadi no-op dan startup mencatat peringatan.
- **Pengaturan aplikasi** (menu **Pengaturan**, G6): pasangan key/value per tenant dan namespace (mis. `chatbot`, key `Dispatcher:MaxConcurrentOperators`) dengan tipe `int`/`bool`/`text`, maksimal 200 per tenant. Owner/admin menulis, viewer membaca; tiap perubahan tercatat di audit. **Bukan tempat secret**: nilai polos dan tampil di UI. Aplikasi klien membacanya dengan API key gatewaynya: `curl -H "Authorization: Bearer gw_..." "http://localhost:5090/client/api/settings?namespace=chatbot"` → `{ "version": "...", "items": [{ "key", "value", "valueType" }] }` dengan header `ETag`; kirim `If-None-Match: "<version>"` untuk polling murah (`304` bila tidak berubah). Penolakan memakai format error data plane tetapi tidak dicatat sebagai pemakaian AI. Setelah upgrade, jalankan migration (`G6AppSettings`) lalu ulangi `sql/tenant-security.sql` bila RLS aktif (policy `settings_isolation_policy`).
  - **Chatbot Operator AI (6A1):** API polling namespace `chatbot` setiap `Settings:RefreshSeconds` (default 15) memakai `Ai:ApiKey` yang sama dengan worker. Endpoint memakai origin `Ai:BaseUrl`, bukan `/v1/`. Override Operator/Queue, `Ai:MaxTokens`, `Response:Detail`, dan `Prompt:AdditionalInstructions` kini didukung bersama Dispatcher; range/type lengkap di [`../README.md`](../README.md) dan [`../DESIGN.md`](../DESIGN.md) §4. Seluruh kandidat valid diterapkan atomik; invalid mempertahankan last-known-good, hapus kembali ke baseline lokal. Snapshot pesan/job/callback/retry tidak berubah karena setting baru. Dispatcher dapat berubah tanpa restart; penurunan tidak menghentikan worker aktif.
  - **Kontrol runtime pasca-6C:** namespace yang sama menerima tujuh budget AI HTTP/SSE, tiga kontrol upload/lampiran dan sembilan budget dokumen/vision/watchdog. `Upload:AllowedExtensions` adalah CSV **text** (contoh `.pdf,.png`), bukan JSON array; angka bertipe **int**. Daftar key/range/ceiling di [`../IMPROVEMENTS.md` §16](../IMPROVEMENTS.md#16-kebijakan-runtime-terpusat-pasca-6c). Report allowlist diperluas, tanpa migration gateway baru. Upgrade chatbot wajib drain + migration `GatewayRuntimeLimits` + restart API/worker/web; job lama tidak diberi fallback budget terbaru.
  - **Status aplikasi:** `POST /client/api/app-status` dengan key gateway meng-upsert laporan aman; scope tenant/project/key berasal authenticator. `GET /admin/api/app-status?namespace=chatbot` untuk tenant viewer; halaman Pengaturan menampilkan configured/applied, core prompt/policy hash, sumber sinkron/rejected/ignored, counts SQL queue/job, serta kelompok kebijakan aktif. Body dibatasi 64 KiB; tidak menerima pesan, file, teks prompt, DLL, secret, atau exception mentah; endpoint tidak menambah AI usage. Tambahan prompt hanya metadata redacted. Stale mengikuti jam server: `max(60 detik, 3 × interval laporan)`. Gagal mengirim tidak menghapus laporan; belum melapor bukan nol. Satu instalasi API per key (instalasi berbeda gunakan key berbeda).
  - **Upgrade status:** migration `G6A1AppStatusReports`, lalu ulangi script RLS untuk tabel/policy baru. Status menunjukkan laporan API terakhir, bukan attestation worker atau counter penggunaan tool/history per-turn.
- **Rotasi key provider** (mis. OpenCode): (1) buat key baru di konsol provider. Untuk OpenCode: **Keys → service account → Add API Key**; izin **Inference only** sudah cukup untuk gateway (inferensi, provider custom, `GET /api/v2/config` untuk sinkronisasi katalog), izin **All** hanya menambah Console API (budget) yang tidak dipakai gateway. Membuat dan mencabut key di OpenCode selalu butuh orang yang login di konsol (tidak bisa lewat API, juga dengan key berizin All). (2) Di gateway, UI **Provider → Rotasi key** (atau `POST /admin/api/providers/{id}/rotate-key`) menaruh key baru sebagai `active` dan menonaktifkan yang lama tanpa downtime; key tidak pernah ditampilkan kembali. (3) Cabut key lama di konsol. `DevSeed:UpstreamApiKey` (user-secrets) hanya dibaca saat seed dev pertama, jadi mengganti nilainya tidak mengubah provider yang sudah ada. Klien (mis. chatbot) memakai key gateway `gw_...`, bukan key provider, sehingga tidak ikut berubah.

## 7. Backup dan restore

Yang harus ikut di-backup (ketiganya):

1. **Database `AiGateway`** — data utama.
2. **Tabel `data_protection_keys`** — kunci enkripsi key provider (ikut backup database di atas).
3. **DPAPI**: bila memakai `ProtectKeysWithDpapi`, kunci hanya bisa didekripsi oleh **user Windows yang sama**.
   Untuk restore di mesin lain, gunakan sertifikat (`Security:KeyCertificateThumbprint`) dan backup sertifikatnya (private key).

```powershell
sqlcmd -S <server> -Q "BACKUP DATABASE AiGateway TO DISK='D:\backup\AiGateway.bak' WITH INIT, COMPRESSION"
# restore
sqlcmd -S <server> -Q "RESTORE DATABASE AiGateway FROM DISK='D:\backup\AiGateway.bak' WITH REPLACE"
```

Restore ke mesin baru: pulihkan database → pasang sertifikat/kunci DPAPI yang sama → jalankan ulang `sql/tenant-security.sql`
(kalau RLS dipakai) → start service. **Tanpa kunci yang sama, key provider tidak bisa didekripsi** dan tenant harus mengisi ulang key.

## 8. Upgrade

1. Backup database (lihat §7).
2. Tunggu request aktif selesai dan hentikan seluruh binari gateway lama.
3. Bangun UI dahulu (`cd web; npm ci; npm run build`), lalu backend (`dotnet publish -c Release`) dan ganti folder service.
   UI harus mendahului backend agar manifest static assets sesuai `wwwroot`; bila metadata build masih merujuk
   asset lama, jalankan `dotnet clean AiGateway.slnx -c Release` sebelum build/publish ulang.
4. Jalankan migration: `dotnet ef database update --project src/AiGateway.Core --startup-project src/AiGateway.Api`
   (atau `Database:AutoMigrate=true` sekali jalan). Migration 6C `20261012090000_G6CVisionCapabilities` bersifat additive:
   hanya menambah kolom nullable `models.supports_vision` (bukan tabel tenant baru), jadi `sql/tenant-security.sql` tidak berubah.
   Model yang sudah ada tetap berstatus "belum dikonfirmasi" sampai admin menyetelnya di menu **Model**.
   Migration GA-03 `20261014090000_GA03UsageCompleteness` menambah `usage_logs.usage_incomplete` dan
   `usage_daily.incomplete_usage_requests`; tidak ada tabel/policy RLS baru. Request historis forwarded dianggap
   belum terbukti lengkap; penolakan/tanpa attempt dikecualikan, dan agregat mempertahankan ketidakpastian log
   yang sudah dihapus retensi. Jangan mencampur writer lama dengan skema/binari baru.
5. Jalankan ulang `sql/tenant-security.sql` bila versi baru menambah tabel tenant.
6. Restart service dengan binari dan UI baru bersama.
7. Cek `GET /healthz`, login admin, dan `job_runs` setelah satu siklus worker.
8. Bila RLS aktif: `ConnectionStrings:GatewayPlatform` kini juga dipakai plane platform HTTP (laporan lintas tenant dan audit tanpa tenant), bukan hanya job latar; pastikan login-nya anggota `gateway_platform` (§5).

SQL GA-03 siap pakai: [`sql/ga03-usage-completeness.sql`](sql/ga03-usage-completeness.sql).
Script ini idempotent untuk database yang sudah sampai migration 6C; apply pertama dan replay sudah diuji
pada salinan backup runtime utama. Backfill dikompilasi setelah kolom baru tersedia, termasuk pada batch script EF.
Dari direktori `gateway/`, dengan koneksi target yang sudah diperiksa:

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AiGateway -b -i sql/ga03-usage-completeness.sql
```

Alternatif EF (tidak perlu menjalankan keduanya), sesudah build Release:

```powershell
dotnet ef database update 20261014090000_GA03UsageCompleteness `
  --project src/AiGateway.Core --startup-project src/AiGateway.Api `
  --configuration Release --no-build
```

Untuk konfigurasi LocalDB pengembangan, jalankan EF dengan `ASPNETCORE_ENVIRONMENT=Development`.
Instalasi produksi harus menggunakan koneksi/secret deployment-nya sendiri, bukan LocalDB contoh.

## 9. Troubleshooting

| Gejala | Sebab / tindakan |
| --- | --- |
| `ConnectionStrings:Gateway belum diisi` | isi user-secrets/variabel lingkungan; saat menjalankan DLL, jalankan dari folder output (appsettings ada di sana) |
| `Access is denied` saat start `.exe` | kebijakan mesin memblokir apphost; jalankan `dotnet AiGateway.Api.dll` dari folder output (atau whitelist exe di antivirus/AppLocker) |
| `Jwt:SigningKey` gagal validasi saat start | kunci kurang dari 32 karakter |
| Semua respons tenant kosong setelah mengaktifkan RLS | login aplikasi belum di `gateway_app` / `SESSION_CONTEXT` tidak terpasang (interceptor) |
| `Could not find stored procedure 'dbo.api_key_bootstrap'` di log | script RLS belum dijalankan (fallback dipakai; jalankan script) |
| Laporan lintas tenant (`/platform/api/usage`, `/platform/api/audit`) kosong dengan RLS aktif | `ConnectionStrings:GatewayPlatform` kosong, atau login-nya bukan anggota role `gateway_platform` (cek `SELECT IS_MEMBER(N'gateway_platform')` dengan login itu = 1) |
| `/platform/api/usage` atau `/platform/api/audit` mengembalikan 500 setelah mengisi `GatewayPlatform` | kredensial/database `GatewayPlatform` salah; lihat log (login gagal ke SQL Server) |
| Key provider tidak bisa didekripsi setelah pindah mesin | kunci Data Protection berbeda; restore sertifikat/kunci yang sama (§7) |
| `invalid_base_url` saat menambah provider | SSRF guard: harus `https` absolut tanpa user/query/fragment dan bukan alamat privat (kecuali diizinkan di `Security:AllowedPrivateNetworks`) |
| Service tidak kunjung `Running` / error 1053 saat start | proses belum terhubung ke Service Control Manager; pastikan `Microsoft.Extensions.Hosting.WindowsServices` terpasang dan `UseWindowsService()` dipanggil, lalu cek Event Log |
| Retensi, alert, atau webhook tidak jalan saat RLS aktif | `ConnectionStrings:GatewayPlatform` kosong → job lintas tenant no-op (ada peringatan saat start) |
| `client_ip` / allow-list IP API key salah di belakang reverse proxy | tambahkan IP-proxy ke `Security:TrustedProxies`; `X-Forwarded-For` dari sumber lain diabaikan |
| `429` dengan alasan `concurrency_limit_exceeded` | tenant mengirim terlalu banyak permintaan chat bersamaan; sesuaikan `Proxy:MaxConcurrentPerTenant` |

## 10. Streaming (SSE)

`POST /v1/chat/completions` menerima `"stream": true` dan menjawab Server-Sent Events (format OpenAI).
Nilai `stream` selain boolean (`1`, `"true"`, objek, ...) ditolak `400 invalid_stream`; absen/`false` tetap respons JSON utuh.
Semua yang terjadi sebelum forwarding (auth, model, kebijakan, konkurensi) sama seperti non-streaming — penolakan selalu
JSON, bukan SSE.

```bash
curl -N -s -X POST "$BASE/v1/chat/completions" \
  -H "Authorization: Bearer $GW_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4.1-flash","messages":[{"role":"user","content":"Halo"}],"stream":true,"stream_options":{"include_usage":true}}'
```

`curl -N` mematikan buffering curl. Header respons: `Content-Type: text/event-stream; charset=utf-8`,
`Cache-Control: no-cache`, `X-Accel-Buffering: no`, dan `X-Request-Id`; tiap event di-flush pada batas baris kosong.

| Yang dilakukan gateway | Keterangan |
| --- | --- |
| Ganti `model` tiap chunk | Diisi alias tenant, bukan nama model upstream |
| Minta usage ke upstream | Gateway **selalu** mengirim `stream_options.include_usage = true`; penagihan butuh chunk usage terakhir |
| Sembunyikan chunk usage | Chunk usage-saja (`choices: []`) hanya diteruskan bila **klien** mengirim `stream_options.include_usage: true`; bila tidak, `usage` juga dihapus dari chunk biasa |
| Baris lain | `: comment`, `event:`, `id:`, `retry:` dan `data:` non-JSON diteruskan apa adanya; `data: [DONE]` menutup stream (ditambahkan gateway bila upstream menutup tanpa mengirimnya tetapi `finish_reason` sudah terlihat) |
| Fallback | Hanya **sebelum** event pertama terkirim: stream kosong, event pertama berisi `error`, atau upstream menjawab sukses dengan **bukan** `text/event-stream` (mis. JSON karena `stream:true` diabaikan → `upstream_protocol_mismatch`) → route berikutnya dicoba; setelah itu tidak ada fallback. Klien yang meminta SSE tidak pernah menerima JSON sukses diam-diam |
| Family route | Route yang katalog template-nya berfamily tidak didukung (`google_generate`) tidak dikirim ke provider: dilewati (log + fallback ke route lain); bila tak ada route lain → `502 unsupported_upstream_family` (`denied_reason` sama). `/v1/models` melaporkan `readiness.available=false` untuk model yang hanya punya route seperti itu. Provider custom tanpa template tetap diteruskan sebagai Chat Completions |
| Error upstream | Payload `error` setelah commit diteruskan sekali lalu stream ditutup; `[DONE]` berikutnya tidak diteruskan atau mengubah status menjadi sukses |
| Konkurensi | Slot `Proxy:MaxConcurrentPerTenant` ditahan selama stream berjalan dan dilepas saat stream berakhir (termasuk klien putus) |

Kegagalan **setelah** stream dimulai (HTTP 200 sudah terkirim): gateway mencoba mengirim event error lalu
menutup tanpa `[DONE]`. Write/flush error memakai deadline yang sama; bila deadline sudah habis atau downstream
macet, koneksi dapat ditutup **tanpa event error**. Klien harus memperlakukan EOF tanpa terminator sebagai kegagalan.
Kode gateway: `upstream_stream_interrupted` (EOF/reset sebelum selesai), `upstream_stream_timeout` (idle atau
deadline total, termasuk downstream), `upstream_response_too_large` (batas byte). Error upstream memakai payload
provider dan dicatat sebagai `denied_reason=upstream_stream_error`, tanpa retry/fallback setelah commit.

Pencatatan pemakaian:

- Satu baris `usage_logs` (+ `usage_daily`) ditulis **saat stream berakhir**, dari usage yang benar-benar diterima:
  input/output/cache/reasoning, biaya tercatat, `finish_reason`, dan `ttfb_ms`. Anthropic/Responses mempertahankan
  snapshot parsial meskipun chunk usage final belum sempat diterbitkan.
- Kegagalan setelah commit: `status=error`, `http_status=200`, kode alasan di `denied_reason`. Deadline gateway
  tidak berubah menjadi 499 bila pembatalan write menyebabkan Kestrel ikut membatalkan `RequestAborted`.
- Klien yang membatalkan lebih dahulu: upstream dibatalkan, `status=error`, `http_status=499`.
- `usage_incomplete=true` untuk usage hilang/invalid/parsial atau stream yang gagal; angka yang diketahui tetap
  disimpan. Nol dengan penanda ini **bukan** bukti tidak ada pemakaian. Penolakan/tanpa attempt tidak incomplete.
- `usage_daily.incomplete_usage_requests` ikut laporan tenant/platform dan rekonsiliasi retensi. Dashboard/Usage
  menampilkan jumlah belum lengkap, token batas bawah, dan biaya tercatat yang bukan tagihan final; CSV menambah
  kolom `usage_incomplete`. Kuota/anggaran tetap memakai angka yang diketahui, tanpa estimasi/reservasi baru.
- Bila project menyalakan `log_content`, ringkasan stream disimpan di `request_bodies.response_json` sebagai
  `{"streamed":true,"content":"<teks jawaban>",...}` (teks dibatasi 100.000 karakter); pada klien putus di tengah
  stream, isinya adalah teks parsial yang sudah diterima.

Catatan reverse proxy: gateway sendiri sudah mematikan buffering server (`DisableBuffering` sebelum header dikirim) dan
mengirim `X-Accel-Buffering: no` agar nginx tidak menahan event; aplikasi tidak memasang kompresi respons. Proxy di depannya
tetap harus mematikan response buffering/kompresi untuk SSE, dan timeout baca proxy harus lebih besar dari
`Proxy:UpstreamTimeoutSeconds` (jeda antar baris) dan `Proxy:MaxStreamSeconds` (durasi total).

Pengaturan proxy berikut **belum diverifikasi** di lingkungan nyata (nama direktif perlu dicek terhadap versi proxy yang dipakai):

- nginx: `proxy_buffering off;` `gzip off;` (di location gateway) dan `proxy_read_timeout` lebih besar dari `Proxy:MaxStreamSeconds`.
- IIS + ARR: *Server Proxy Settings → Response buffer threshold = 0 KB*, nonaktifkan *dynamic compression* untuk `text/event-stream`, dan naikkan *Time-out* ARR.
- Azure Front Door / API Management / CDN lain: pastikan respons `text/event-stream` tidak di-cache atau di-buffer menurut dokumentasi layanan tersebut.

Uji nyata di belakang proxy yang benar-benar dipakai (bukan `localhost` langsung): jalankan permintaan streaming yang menghasilkan jawaban
panjang dan beri stempel waktu per baris. Event harus tiba **bertahap**, bukan sekaligus di akhir:

```bash
curl -N -s -X POST "$PROXY_BASE/v1/chat/completions" \
  -H "Authorization: Bearer $GW_KEY" -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4.1-flash","messages":[{"role":"user","content":"Tulis 300 kata tentang laut."}],"stream":true}' \
| while IFS= read -r line; do printf '%s %s\n' "$(date +%T.%N)" "$line"; done
```

Bandingkan dengan panggilan langsung ke gateway: bila stempel waktu di belakang proxy menumpuk di satu detik yang sama di akhir, proxy masih melakukan buffering.

## 11. Contoh integrasi

Ada di `examples/`:

- `examples/curl.md` — panggilan data plane (termasuk streaming dengan `curl -N`) dan admin dengan curl.
- `examples/python/example.py` — Python 3.10+ (hanya stdlib), `python examples/python/example.py <base_url> <api_key> [--reasoning]`.
- `examples/dotnet/` — console .NET 9 (`cd examples/dotnet; dotnet run -- <base_url> <api_key>`).

Semua contoh memanggil `GET /v1/models` dan `POST /v1/chat/completions` (respons utuh, lalu streaming SSE) dengan
API key gateway (`gw_<prefix>_<secret>`).
`GET /v1/models` hanya menampilkan alias enabled milik tenant key yang diizinkan oleh irisan policy tenant/project/key.
Empat field OpenAI (`id`, `object`, `created`, `owned_by`) tetap ada, ditambah:

| Metadata | Arti |
| --- | --- |
| `capabilities.tools`, `capabilities.streaming`, `capabilities.vision` | `true` / `false` / `null` (belum dikonfirmasi). Bukan tebakan dari alias. `vision` = konfirmasi admin bahwa target menerima gambar inline (PNG/JPEG dari chatbot); metadata `supports_vision` katalog hanya dipakai saat impor. |
| `readiness.available`, `readiness.reason` | Ada route enabled + provider enabled/tidak dihapus + credential active; reason `no_available_provider` bila tidak ada. Sumber eligibility sama dengan forwarding; **bukan health upstream**. |
| `limits.max_output_tokens` | Minimum `models.max_output_tokens` dan semua policy aktif `MaxTokensPerRequest`; null jika tidak ada batas konfigurasi. Bukan context window/sisa kuota/jaminan kapasitas provider. |

Model yang belum ready tetap tampil; chat tanpa target eligible gagal `503 model_unavailable`.
Menu **Model** menyediakan konfirmasi tri-state tools/streaming/vision (Belum dikonfirmasi/Didukung/Tidak didukung; patch API memakai pasangan `supportsVision`/`clearSupportsVision`). Konfirmasi mencakup seluruh target route, termasuk fallback nonaktif; perubahan set `(provider, upstreamModel)` atau BaseUrl provider membatalkan ketiganya dalam transaksi yang sama. Perubahan priority/weight saja atau rotasi credential tidak membatalkannya. Import katalog satu route memakai flag tools dan vision katalog; streaming, model legacy/custom, dan import discover tetap unknown; baris model lama hasil migration `20261012090000_G6CVisionCapabilities` tetap NULL sampai admin mengonfirmasi.

**OpenCode Go:** pakai provider BaseUrl `https://opencode.ai/inference/go/openai/v1/`. Klien mengirim User-Agent aplikasi sendiri dan `x-opencode-session` stabil per conversation (1–200 huruf/angka/`._-`, satu nilai). Gateway meneruskan hanya dua header allowlisted ini ke host/path Go, bukan ke provider lain; tidak membuat session acak per request atau memalsukan OpenCode User-Agent. Chatbot mengirim `OperatorAi/1.0` dan id conversation secara otomatis. Katalog/readiness tidak membuktikan langganan/limit/kontrak Go; smoke nyata tetap diperlukan. Untuk uji repo, **tidak memakai Zen sebagai fallback**. [Persyaratan Go](https://opencode.ai/docs/go/#where-can-i-use-it).

## 12. Web Search Provider Hub

Search/scrape terpisah dari provider model AI, tetapi memakai database, tenant, JWT admin, API key klien, Data Protection, audit, rate limiter dan anti-SSRF gateway yang sama. Cara paling mudah mendaftarkan provider: menu **Konfigurasi → Web Search** di UI gateway (owner/admin tenant; viewer tidak melihat menu ini). Jangan memakai menu **Provider**: itu untuk endpoint model AI (field *Models path* di sana harus path relatif tanpa awalan `/`). API di bawah tetap tersedia untuk otomasi.

Halaman Web Search: daftar provider dengan status, kredit periode, sukses/gagal/failover dan error terakhir; tambah (tipe + API key cukup; nama/base URI default), edit (tipe tidak bisa diubah, key opsional), nonaktifkan/aktifkan, test, reset, dan hapus.

### Upgrade

Migration awal `20261014120000_AddSearchProviderHub` tetap utuh. Migration tambahan `20261014130000_SearchProviderHubAudit` menambah generation/admin-generation serta lease half-open, tanpa menghapus konfigurasi/pemakaian yang sudah ada. Snapshot EF dan target model migration sudah sinkron; warning pending model tidak disembunyikan.

```powershell
dotnet ef database update --project src/AiGateway.Core --startup-project src/AiGateway.Api
# Alternatif SQL idempotent, dimulai dari schema GA-03:
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AiGateway -b -i sql/web-search-provider-hub.sql
# Jika deployment memakai RLS, pasang/ulang policy setelah migration:
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AiGateway -b -i sql/tenant-security.sql
```

Backup dahulu; restart seluruh writer gateway bersama setelah upgrade. Jangan campur writer hub lama dengan state/generation baru. Migration tidak memasukkan API key atau provider contoh.

### Registrasi dan panggilan

Semua `/admin/providers` (alias `/admin/api/search-providers`) membutuhkan JWT **owner/admin tenant**, bukan token platform admin atau API key `gw_...`. Kunci provider write-only; DTO hanya memuat hint bertopeng. Base URI default: Tavily `https://api.tavily.com`, Exa `https://api.exa.ai`, Firecrawl `https://api.firecrawl.dev`; override wajib HTTPS publik, tanpa userinfo/query/fragment.

| Provider | Body minimum `POST /admin/providers` |
| --- | --- |
| Tavily | `{"type":"tavily","apiKey":"<key>"}` |
| Exa | `{"type":"exa","apiKey":"<key>"}` |
| Firecrawl | `{"type":"firecrawl","apiKey":"<key>"}` |

Nama default sama dengan type; isi `name` untuk beberapa akun provider dengan type sama. Opsional: `baseUri`, `weight` (default 1), `capabilities` (`search,extract`), `monthlyBudget`, `costPerSearch`, `costPerExtract`, `resetPolicy` (`first_of_month_utc`, `rolling_30_days`, `manual`), `errorMappingOverride`.

| Endpoint | Operasi |
| --- | --- |
| `GET /admin/providers`, `GET /admin/providers/{id}` | Registry milik tenant |
| `PUT /admin/providers/{id}`, `DELETE /admin/providers/{id}` | Ubah / soft delete |
| `POST /admin/providers/{id}/enable`, `/disable`, `/reset` | Status; reset tidak mengaktifkan provider disabled |
| `POST /admin/providers/{id}/test` | Satu search murah; tercatat dan memakai kredit |
| `GET /admin/providers/status` | Status, estimasi sisa budget, retry, error aman, counter |
| `POST /v1/search` | `{"query":"...","maxResults":5,"includeContent":false}`; cap 10 hasil |
| `POST /v1/extract` | `{"urls":["https://example.com"]}`; cap 5 URL |

`/v1/*` search/extract memakai `Authorization: Bearer gw_...` milik tenant. Pilihan acak berbobot, tanpa mengulang provider dalam request yang sama; 503 bila habis kandidat, dengan `Retry-After`. Policy rate limit tenant/project/key dan default search 60/menit memakai limiter yang sudah ada (state limiter tetap per instance).

### Seed yang aman

Seed hanya untuk tenant eksplisit `WebSearch:SeedTenantSlug` yang **sudah ada**; tidak memilih tenant pertama. Isi `WebSearch:Providers` lewat konfigurasi/env/user-secrets gateway, memakai field registrasi yang sama. Bila registry tenant sudah pernah berisi provider (termasuk soft-deleted), seed tidak menimpa atau menghidupkannya lagi. Seluruh batch tervalidasi dan disimpan atomik; satu entry invalid membatalkan batch.

```powershell
dotnet user-secrets set "WebSearch:SeedTenantSlug" "dev" --project src/AiGateway.Api
dotnet user-secrets set "WebSearch:Providers:0:Type" "tavily" --project src/AiGateway.Api
dotnet user-secrets set "WebSearch:Providers:0:ApiKey" "<key>" --project src/AiGateway.Api
```

`Tavily:ApiKey`/`Exa:ApiKey`/`Firecrawl:ApiKey` top-level dan key provider pada chatbot tidak dibaca lagi. Chatbot memakai `Ai:BaseUrl`/`Ai:ApiKey` gateway saja; fallback keyless hanya 503 atau kegagalan jaringan/timeout, bukan 400/401/429/500 atau JSON rusak. DuckDuckGo Instant Answer bukan pengganti mesin pencari penuh. Tool `cari_web` memberi title/url/snippet ringkas, `fetch_url` memberi ekstraksi terbatas; hasil adalah data tidak tepercaya dan tidak dipush sebagai pesan tool publik.

Deadline klien chatbot memakai `Ai:TotalRequestTimeoutSeconds` yang sudah ada (default 120 detik), bukan 25 detik yang dapat memotong failover tiga provider × 15 detik. Logger HTTP default dinonaktifkan untuk client search/fallback agar URL kueri dan header tidak tercetak.

### Batas, biaya dan observabilitas

| `WebSearch:*` | Default |
| --- | --- |
| `BudgetThreshold` | 0.95 |
| `CircuitFailureThreshold`, `CircuitCooldownSeconds` | 3, 60 detik |
| `RetryAfterFallbackSeconds` | 60 detik; backoff 2x, cap 32x; header Retry-After menang |
| `QuotaRecheckHours` | 6 (reset manual) |
| `ProviderTimeoutSeconds`, `ProbeLeaseSeconds` | 15, 30 detik; lease minimal timeout + 5 |
| `ProviderMaxResponseBytes` | 4 MiB; body terlalu besar ditolak |
| `RequestsPerMinute` | 60 per tenant, dapat dipersempit policy |

Budget lokal adalah estimasi, bukan saldo/tagihan provider. Request paralel yang sudah berjalan boleh melewati ambang; tidak ada reservasi hard-quota. Reset kalender memakai UTC; rolling berarti periode 30 hari sejak `PeriodStart`; manual tidak auto-reset. Status/counter dan lease probe disimpan SQL sehingga tahan restart dan aman lintas proses; rate limiter keseluruhan gateway tetap batas single-instance yang sudah ada.

Pada reset manual, `/reset` juga memulai periode baru (`UsedThisPeriod=0`, `PeriodStart=now`) tanpa menghapus counter lifetime. Pada policy kalender/rolling, `/reset` hanya membersihkan status error; kredit periode tetap dihitung. Endpoint status mengikuti `BudgetThreshold`, rollover, prioritas disabled/broken dan lease probe yang sedang aktif; timestamp search dikembalikan sebagai UTC berakhiran `Z`.

Cost default 1 per unit: Tavily basic search 1 unit, basic extract 1 per 5 URL sukses; Exa bundled search ≤10 hasil 1 unit, extract per halaman sukses; Firecrawl search 2 per 10 hasil + 1/halaman bila scraping content diminta, scrape per halaman sukses. `CostPerSearch`/`CostPerExtract` mengubah harga unit lokal; Exa tidak memakai unit kredit yang sama dengan provider lain. Spend yang sudah diketahui pada batch extract parsial tetap dihitung, termasuk ketika request dibatalkan.

Konten dinormalisasi dengan cap snippet 1.000, search content 20.000 dan extract 100.000 karakter; `truncated=true` saat teks dipotong. `failures` pada extract menandai URL yang gagal, tanpa body upstream/secret. Default search tidak meminta full content.

Logs hanya metadata provider/operasi/latency/outcome/failover; transisi exhausted/broken diperingatkan sekali, recovery dicatat info. Meter `AiGateway.WebSearch`: `gateway.websearch.requests`, `.success`, `.failures`, `.failovers`; counter persisten juga ada pada endpoint status. `/healthz` tetap memeriksa database, bukan saldo provider.

`ErrorMappingOverride` adalah JSON dengan `exhaustedStatusCodes`, `exhaustedBodySubstrings`, `rateLimitStatusCodes`, `brokenStatusCodes`; rules invalid ditolak saat registrasi. Pesan yang ditampilkan tetap fixed/sanitized, tidak mengambil body provider.

Provider tambahan: tambahkan satu nilai `SearchProviderType` dan satu class `ISearchProvider` di assembly Core. DI menemukan adapter saat startup; named client/anti-SSRF/deadline mengikuti enum. Metadata `DefaultBaseUri` dan cost estimates berada pada adapter; tidak perlu mengubah endpoint/hub atau CHECK type di database.

Dokumentasi resmi yang dipakai: [Tavily search/errors](https://docs.tavily.com/documentation/api-reference/endpoint/search), [Tavily extract](https://docs.tavily.com/documentation/api-reference/endpoint/extract), [Tavily kredit](https://docs.tavily.com/documentation/api-credits), [Exa search](https://exa.ai/docs/reference/search), [Exa contents](https://exa.ai/docs/reference/get-contents), [Exa biaya contents](https://exa.ai/docs/reference/contents), [Firecrawl v2 search](https://docs.firecrawl.dev/api-reference/endpoint/search), [Firecrawl scrape](https://docs.firecrawl.dev/api-reference/endpoint/scrape), [Firecrawl errors](https://docs.firecrawl.dev/api-reference/errors), [Firecrawl biaya search](https://docs.firecrawl.dev/features/search#cost-implications).

Tavily 432/433 dan Exa/Firecrawl 402 dipetakan exhausted; 429 memakai Retry-After; auth error mengikuti rule masing-masing provider (403 site-specific Firecrawl tidak otomatis merusak key). Pengujian audit memakai payload terkontrol, HTTP Kestrel dan SQL terisolasi; belum membuktikan saldo, latency atau key berbayar provider sungguhan.
