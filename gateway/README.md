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
| `ConnectionStrings:GatewayPlatform` | koneksi job latar sebagai principal `gateway_platform` (wajib bila RLS aktif) | memakai koneksi aplikasi |
| `Jwt:SigningKey` | kunci tanda tangan access token (wajib, >= 32 karakter) | — |
| `Security:KeyCertificateThumbprint` | sertifikat X.509 untuk enkripsi kunci Data Protection (wajib di luar Windows) | DPAPI (Windows) |
| `Security:AllowedPrivateNetworks` | CIDR/IP/nama host yang boleh dihubungi walau privat (mis. provider internal) | kosong |
| `Security:TrustedProxies` | CIDR/IP reverse proxy yang header `X-Forwarded-For`/`X-Forwarded-Proto`-nya dipercaya (menentukan `client_ip`, allow-list IP API key, dan rate limit login) | hanya loopback |
| `Security:AuthRequestsPerMinute` | rate limit endpoint login per IP | 20 |
| `Security:MaxAdminRequestBytes` | batas body API admin | 1 MB |
| `Proxy:MaxRequestBytes` / `Proxy:MaxResponseBytes` | batas body permintaan klien dan respons upstream | 4 MB / 8 MB |
| `Proxy:UpstreamTimeoutSeconds` | waktu ke header respons upstream **dan** jeda maksimum antar baris saat streaming | 120 s |
| `Proxy:MaxStreamSeconds` | durasi maksimum satu respons SSE (`stream: true`) | 900 |
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

`sql/tenant-security.sql` adalah lapis kedua isolasi tenant di database: FILTER + BLOCK untuk 16 tabel tenant
(11 tabel inti + 5 tabel maintenance), `audit_logs` append-only, dan prosedur bootstrap API key.
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
- Job latar memakai `gateway_platform_user` lewat `ConnectionStrings:GatewayPlatform`. Plane platform HTTP (`/platform/api`) belum memakai koneksi itu: dengan RLS aktif laporan/audit lintas tenant di sana kosong, dan baris audit tanpa tenant (aksi platform, login platform admin, login gagal, accept-invite) ditolak predikat RLS lalu dicatat sebagai Error di log, bukan di `audit_logs`.
- Migration dijalankan oleh principal `gateway_migration` (db_ddladmin).
- Jalankan ulang script setiap kali ada tabel tenant baru (mis. setelah upgrade G4+).

## 6. Operasional harian

- **Worker latar** (`MaintenanceWorker`) berjalan di dalam proses API: retensi `request_bodies`/`usage_logs`/`job_runs`/`webhook_deliveries`,
  evaluasi alert kuota, dan pengiriman webhook. Hasil tiap siklus tercatat di `job_runs`.
- **Isi prompt** hanya disimpan bila `projects.log_content = 1`, dengan `content_retention_days` (atau `Maintenance:DefaultContentRetentionDays`).
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
2. Ganti binari (`dotnet publish` / folder service).
3. Jalankan migration: `dotnet ef database update --project src/AiGateway.Core --startup-project src/AiGateway.Api`
   (atau `Database:AutoMigrate=true` sekali jalan).
4. Jalankan ulang `sql/tenant-security.sql` bila versi baru menambah tabel tenant.
5. Bangun ulang UI (`cd web; npm ci; npm run build`) lalu restart service.
6. Cek `GET /healthz`, login admin, dan `job_runs` setelah satu siklus worker.

## 9. Troubleshooting

| Gejala | Sebab / tindakan |
| --- | --- |
| `ConnectionStrings:Gateway belum diisi` | isi user-secrets/variabel lingkungan; saat menjalankan DLL, jalankan dari folder output (appsettings ada di sana) |
| `Access is denied` saat start `.exe` | kebijakan mesin memblokir apphost; jalankan `dotnet AiGateway.Api.dll` dari folder output (atau whitelist exe di antivirus/AppLocker) |
| `Jwt:SigningKey` gagal validasi saat start | kunci kurang dari 32 karakter |
| Semua respons tenant kosong setelah mengaktifkan RLS | login aplikasi belum di `gateway_app` / `SESSION_CONTEXT` tidak terpasang (interceptor) |
| `Could not find stored procedure 'dbo.api_key_bootstrap'` di log | script RLS belum dijalankan (fallback dipakai; jalankan script) |
| Laporan lintas tenant (`/platform/api`) kosong dengan RLS aktif | batas yang diketahui: plane platform HTTP belum memakai principal `gateway_platform` (hanya job latar yang memakainya); lihat DECISIONS G4 |
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
| Fallback | Hanya **sebelum** event pertama terkirim: stream kosong atau event pertama berisi `error` → route berikutnya dicoba; setelah itu tidak ada fallback |
| Konkurensi | Slot `Proxy:MaxConcurrentPerTenant` ditahan selama stream berjalan dan dilepas saat stream berakhir (termasuk klien putus) |

Kegagalan **setelah** stream dimulai (HTTP 200 sudah terkirim): klien menerima satu event terakhir
`data: {"error":{"message","type":"upstream_error","param":null,"code":"..."}}` lalu koneksi ditutup tanpa `[DONE]`.
Kodenya: `upstream_stream_interrupted` (upstream menutup/reset sebelum selesai), `upstream_stream_timeout` (diam lebih lama
dari `Proxy:UpstreamTimeoutSeconds`, atau durasi total melewati `Proxy:MaxStreamSeconds`), atau `upstream_response_too_large`
(melebihi `Proxy:MaxResponseBytes` yang dibaca dari upstream).

Pencatatan pemakaian:

- Satu baris `usage_logs` (+ `usage_daily`) ditulis **saat stream berakhir**, dari chunk usage terakhir: token
  input/output/cache/reasoning, biaya, `finish_reason`, dan `ttfb_ms` = waktu ke event pertama.
- Kegagalan setelah commit: `status=error`, `http_status=200`, kode alasan di `denied_reason`.
- Klien memutus koneksi di tengah stream: upstream dibatalkan, `status=error`, `http_status=499`, tanpa event error.
- Batas yang diketahui: bila upstream tidak pernah mengirim usage (ada peringatan di log) atau klien putus sebelum chunk usage,
  token dan biaya tercatat 0 — pemakaian parsial tidak diestimasi.
- Bila project menyalakan `log_content`, ringkasan stream disimpan di `request_bodies.response_json` sebagai
  `{"streamed":true,"content":"<teks jawaban>",...}` (teks dibatasi 100.000 karakter); pada klien putus di tengah
  stream, isinya adalah teks parsial yang sudah diterima.

Catatan reverse proxy: `X-Accel-Buffering: no` sudah dikirim agar nginx tidak menahan event; proxy lain (IIS/ARR, dan lain-lain)
harus mematikan response buffering/kompresi untuk SSE, dan timeout baca proxy harus lebih besar dari
`Proxy:UpstreamTimeoutSeconds` (jeda antar baris) dan `Proxy:MaxStreamSeconds` (durasi total).

## 11. Contoh integrasi

Ada di `examples/`:

- `examples/curl.md` — panggilan data plane (termasuk streaming dengan `curl -N`) dan admin dengan curl.
- `examples/python/example.py` — Python 3.10+ (hanya stdlib), `python examples/python/example.py <base_url> <api_key> [--reasoning]`.
- `examples/dotnet/` — console .NET 9 (`cd examples/dotnet; dotnet run -- <base_url> <api_key>`).

Semua contoh memanggil `GET /v1/models` dan `POST /v1/chat/completions` (respons utuh, lalu streaming SSE) dengan
API key gateway (`gw_<prefix>_<secret>`).
`GET /v1/models` hanya menampilkan alias yang boleh dipakai key itu menurut kebijakan (irisan scope tenant/project/key);
ketersediaan route upstream tidak ikut disaring — model tanpa route aktif tetap tampil dan panggilan chat-nya gagal
`503 model_unavailable`.
