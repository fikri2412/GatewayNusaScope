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
- [10. Contoh integrasi](#10-contoh-integrasi)

---

## 1. Prasyarat

| Kebutuhan | Versi | Catatan |
| --- | --- | --- |
| .NET SDK | 9.0.x | `dotnet --list-sdks` |
| SQL Server | 2019+ / LocalDB | Database terpisah `AiGateway` |
| Node.js | 20+ | hanya untuk membangun UI (`gateway/web`) |
| Windows | 10/Server 2019+ | untuk DPAPI & Windows Service; Linux butuh sertifikat (lihat §3) |

## 2. Pasang

```powershell
cd gateway
dotnet build                                    # 0 warning (warnings-as-errors)
dotnet test                                     # 219 test; butuh LocalDB
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
| `Security:AuthRequestsPerMinute` | rate limit endpoint login per IP | 20 |
| `Security:MaxAdminRequestBytes` | batas body API admin | 1 MB |
| `Proxy:MaxRequestBytes` / `Proxy:UpstreamTimeoutSeconds` | batas body dan timeout upstream | 4 MB / 120 s |
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

## 5. Row-Level Security (opsional, disarankan di produksi)

`sql/tenant-security.sql` adalah lapis kedua isolasi tenant di database: FILTER + BLOCK untuk semua tabel tenant,
`audit_logs` append-only, dan prosedur bootstrap API key. Idempoten, satu batch, jalankan **setelah** migration:

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
- Job latar dan laporan lintas tenant memakai `gateway_platform_user` lewat `ConnectionStrings:GatewayPlatform`.
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
5. Bangun ulang UI (`web/npm run build`) lalu restart service.
6. Cek `GET /healthz`, login admin, dan `job_runs` setelah satu siklus worker.

## 9. Troubleshooting

| Gejala | Sebab / tindakan |
| --- | --- |
| `ConnectionStrings:Gateway belum diisi` | isi user-secrets/variabel lingkungan; saat menjalankan DLL, jalankan dari folder output (appsettings ada di sana) |
| `Access is denied` saat start `.exe` | kebijakan mesin memblokir apphost; jalankan `dotnet AiGateway.Api.dll` dari folder output (atau whitelist exe di antivirus/AppLocker) |
| `Jwt:SigningKey` gagal validasi saat start | kunci kurang dari 32 karakter |
| Semua respons tenant kosong setelah mengaktifkan RLS | login aplikasi belum di `gateway_app` / `SESSION_CONTEXT` tidak terpasang (interceptor) |
| `Could not find stored procedure 'dbo.api_key_bootstrap'` di log | script RLS belum dijalankan (fallback dipakai; jalankan script) |
| Laporan lintas tenant kosong dengan RLS aktif | principal belum anggota `gateway_platform`; isi `ConnectionStrings:GatewayPlatform` |
| Key provider tidak bisa didekripsi setelah pindah mesin | kunci Data Protection berbeda; restore sertifikat/kunci yang sama (§7) |
| `invalid_base_url` saat menambah provider | SSRF guard: harus `https` absolut tanpa user/query/fragment dan bukan alamat privat (kecuali diizinkan di `Security:AllowedPrivateNetworks`) |

## 10. Contoh integrasi

Ada di `examples/`:

- `examples/curl.md` — panggilan data plane dan admin dengan curl.
- `examples/python/example.py` — Python 3 (hanya stdlib), `python example.py <base_url> <api_key>`.
- `examples/dotnet/` — console .NET 9 (`dotnet run -- <base_url> <api_key>`).

Semua contoh memanggil `POST /v1/chat/completions` dan `GET /v1/models` dengan API key gateway (`gw_<prefix>_<secret>`).
