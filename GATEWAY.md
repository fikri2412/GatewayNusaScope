# AI Gateway — Rancangan Produk

Dokumen ini adalah spesifikasi gateway AI sebagai produk mandiri yang bisa dijual atau di-handover. Status: **DRAFT untuk direview**; bagian bertanda `[USULAN]` belum disepakati.

Gateway dikerjakan **sebelum** chatbot (`DESIGN.md`). Chatbot nanti menjadi klien pertama gateway lewat endpoint OpenAI-compatible. Satu fase per sesi, sama seperti `DESIGN.md`.

Nama kerja: **AI Gateway**, namespace `AiGateway`. `[USULAN]` nama produk final menyusul.

---

## 1. Ringkasan

Gateway adalah satu pintu antara aplikasi pelanggan dan provider AI. Tugasnya: mengautentikasi aplikasi, membatasi pemakaian, meneruskan permintaan ke provider, mencatat pemakaian dan biaya, dan menyediakan halaman manajemen.

Model bisnis: **multi-tenant (SaaS)**. Satu instalasi melayani banyak perusahaan (tenant). Tiap tenant punya project, API key, provider, dan kebijakan sendiri, dan tidak boleh melihat data tenant lain.

**BYOK:** tiap tenant memasukkan key provider miliknya sendiri. Platform tidak menyediakan key provider. Key OpenCode di lingkungan development hanya untuk uji.

Istilah:

| Istilah | Arti |
| --- | --- |
| Platform | Pengelola instalasi gateway (kita atau pembeli yang menjalankannya) |
| Tenant | Satu perusahaan pelanggan |
| Project | Aplikasi atau departemen di dalam tenant; unit penagihan dan kuota |
| API key | Kredensial yang dipakai aplikasi untuk memanggil gateway; milik satu project |
| Provider | Koneksi ke penyedia AI (base URL + key milik tenant) |
| Model | Nama model yang dilihat klien (alias), dipetakan ke provider + model asli |

---

## 2. Stack

Sama dengan `AGENTS.md`: .NET 9, ASP.NET Core Minimal API, EF Core 9 + Dapper, SQL Server, React + Vite + TypeScript. Tanpa Docker, Redis, atau message broker.

Paket tambahan yang dibutuhkan (catat di `DECISIONS.md` saat dipakai): `Microsoft.AspNetCore.Authentication.JwtBearer` (login admin). Enkripsi key provider memakai ASP.NET Core Data Protection (sudah ada di framework).

Gateway **tidak mereferensikan** `OperatorAi.Shared` dan tidak tahu soal chatbot.

---

## 3. Struktur

```text
gateway/
├─ AiGateway.slnx
├─ src/
│  ├─ AiGateway.Core/        # entity, DbContext, engine kebijakan, kripto, kontrak
│  └─ AiGateway.Api/         # data plane, admin API, melayani UI statis, background jobs
├─ tests/AiGateway.Tests/
└─ web/                      # UI admin (React + Vite + TS); build ke Api/wwwroot
```

Database terpisah: `AiGateway` di SQL Server yang sama saat development.

---

## 4. Arsitektur

Tiga bidang dalam satu proses:

1. **Data plane** (dipanggil aplikasi pelanggan), auth API key:
   - `POST /v1/chat/completions` format OpenAI, non-streaming dulu.
   - `GET /v1/models` daftar model yang boleh dipakai key itu.
2. **Control plane** (dipanggil UI admin), auth JWT: `/admin/api/...` CRUD provider, model, project, key, kebijakan, laporan.
3. **Platform plane** (pengelola instalasi), auth JWT role `platform_admin`: `/platform/api/...` kelola tenant dan plan.

Plus `GET /healthz` tanpa auth, dan background service untuk rekap dan retensi.

### Alur satu permintaan data plane

```text
1. Baca Authorization: Bearer gw_<prefix>_<secret>
2. Cari api_keys by key_prefix; bandingkan SHA-256 (constant-time)
   tolak 401 bila tidak ada / dicabut / kedaluwarsa; 403 bila IP tidak diizinkan
3. Muat project dan tenant; tolak 403 bila tenant/project suspended
4. Cari model alias milik tenant; tolak 404/403 bila tidak ada, disabled, atau tidak diizinkan kebijakan
5. Terapkan kebijakan paling spesifik (key > project > tenant):
   - max_tokens dipotong ke batas
   - rate limit per menit (penghitung memori); 429 + Retry-After
   - kuota harian/bulanan dan anggaran dari usage_daily; 429 dengan alasan
6. Dekripsi key provider, panggil upstream (timeout, tanpa redirect)
7. Catat usage_logs + upsert usage_daily (juga untuk permintaan ditolak/gagal)
8. Kembalikan respons upstream; error dibentuk ulang ke format error OpenAI
```

Bila upstream gagal (5xx, timeout) dan model punya `fallback_model_id`, coba model cadangan sekali.

---

## 5. Isolasi tenant

- Semua tabel milik tenant punya `tenant_id NOT NULL`.
- EF Core **global query filter** memakai tenant dari konteks request. Query Dapper wajib menyertakan `tenant_id`.
- Tenant selalu berasal dari token/API key, **tidak pernah** dari body, query, atau header yang dikirim klien.
- Id yang diterima dari klien (project, model, provider) selalu divalidasi milik tenant yang sama.
- Pengerasan (G4): SQL Server Row-Level Security dengan `SESSION_CONTEXT('tenant_id')` sebagai lapis kedua.
- **Wajib ada test lintas tenant** untuk setiap endpoint admin: tenant A tidak bisa membaca, mengubah, atau menghapus data tenant B (hasil `404`).

---

## 6. Database

Database terpisah dari chatbot: `AiGateway` (connection string `ConnectionStrings:Gateway`, migration dan login SQL sendiri). Tidak ada foreign key atau join ke database lain.

Aturan skema:

- Nama tabel dan kolom snake_case. Id internal `BIGINT IDENTITY`. Waktu `DATETIME2` UTC (`SYSUTCDATETIME()`).
- Tabel yang tampil di API (`tenants`, `projects`, `api_keys`, `providers`, `models`) punya `public_id UNIQUEIDENTIFIER` (unik, default `NEWID()`); API hanya memakai `public_id`, tidak pernah `id`.
- Tabel konfigurasi punya `row_version ROWVERSION` (optimistic concurrency) dan soft delete `deleted_at` (providers, models, projects), agar `usage_logs` tetap punya rujukan.
- Nilai berstatus/tipe dibatasi `CHECK`. Uang `DECIMAL`, bukan float: harga `DECIMAL(18,6)` per 1 juta token, biaya `DECIMAL(18,8)`, `currency` default `USD`.
- Semua tabel milik tenant punya `tenant_id` dan indeksnya diawali `tenant_id`.
- `usage_logs` tanpa foreign key (ditulis tiap permintaan); integritas dijaga aplikasi.
- DDL acuan: tabel di bawah. EF migration harus menghasilkan skema setara.

### Tingkat 1 (G0-G3)

**Tenant dan identitas**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `plans` | `id`, `name` (unik), `max_projects`, `max_api_keys`, `max_requests_per_month`, `max_tokens_per_month`, `created_at` | `NULL` = tanpa batas |
| `tenants` | `id`, `public_id`, `name`, `slug` (unik), `status` (`active`/`suspended`), `plan_id`, `created_at` | |
| `users` | `id`, `tenant_id` (NULL untuk platform admin), `email` (unik global), `password_hash`, `display_name`, `role` (`platform_admin`/`owner`/`admin`/`viewer`), `is_active`, `failed_login_count`, `locked_until`, `security_stamp`, `last_login_at`, `created_at` | Satu user = satu tenant. Hash via `PasswordHasher<T>` |
| `refresh_tokens` | `id`, `user_id`, `token_hash` (unik), `expires_at`, `revoked_at`, `replaced_by_id`, `ip`, `user_agent`, `created_at` | Hanya hash |
| `user_tokens` | `id`, `user_id`, `purpose` (`invite`/`reset_password`), `token_hash` (unik), `expires_at`, `used_at`, `created_at` | Hanya hash; sekali pakai |

**Master data katalog (tingkat platform, tanpa `tenant_id`, dikelola `platform_admin`)**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `provider_templates` | `id`, `code` (unik, mis. `opencode`, `opencode-go`), `name`, `type` (`openai`), `default_base_url`, `auth_header` (default `Authorization`), `auth_prefix` (default `Bearer `), `models_path` (NULL bila provider tidak punya endpoint daftar model), `sync_kind` (`opencode_config` / NULL), `sync_url`, `enabled`, `created_at` | Resep koneksi ke satu penyedia. Template `opencode` bawaan; `opencode-go` dibuat otomatis oleh sinkronisasi |
| `catalog_models` | `id`, `template_id`, `upstream_model`, `display_name`, `api_family` (`openai_chat`/`openai_responses`/`anthropic_messages`/`google_generate`), `context_window`, `max_input_tokens`, `max_output_tokens`, `input_price_per_1m`, `output_price_per_1m`, `cache_read_price_per_1m`, `cache_write_price_per_1m`, `extra_tiers_json` (tier prompt panjang), `currency`, `supports_tools`, `supports_vision`, `supports_reasoning`, `source` (`seed`/`discovered`/`manual`), `enabled`, `created_at` | Daftar model yang dikenal per template; harga boleh NULL. Unik `(template_id, upstream_model)`. Gateway baru meneruskan `openai_chat`; model keluarga lain tersimpan tapi tidak ditawarkan saat impor |

Alur tenant: pilih template (atau `custom`) → isi API key milik sendiri (BYOK) → provider terbentuk → **impor model** dari katalog, atau **discover** (gateway memanggil `GET {base_url}/{models_path}` dengan key tenant lalu menawarkan daftar untuk diimpor) → alias model, route, dan harga awal dibuat otomatis dan bisa diubah. Penyedia lain (mis. CommandCode) didaftarkan sebagai provider `custom`: cukup isi URI, API key, dan (bila perlu) nama header auth, lalu daftarkan model secara manual atau lewat discover.

**Konfigurasi AI (milik tenant)**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `providers` | `id`, `public_id`, `tenant_id`, `template_id` (NULL = custom), `name`, `type` (`openai`), `base_url`, `auth_header`, `auth_prefix`, `models_path`, `enabled`, `deleted_at`, `row_version`, `created_at` | Nilai koneksi disalin dari template saat dibuat dan boleh diubah. `base_url` divalidasi anti-SSRF (bagian 8). Unik `(tenant_id, name)` yang belum dihapus |
| `provider_credentials` | `id`, `tenant_id`, `provider_id`, `api_key_encrypted`, `key_hint` (4 karakter terakhir), `status` (`active`/`disabled`), `created_at`, `disabled_at` | Rotasi: buat key baru `active`, lalu `disabled` yang lama. Hanya satu `active` per provider |
| `models` | `id`, `public_id`, `tenant_id`, `alias`, `description`, `max_output_tokens`, `enabled`, `deleted_at`, `row_version`, `created_at` | `alias` unik per tenant; nama yang dilihat klien |
| `model_routes` | `id`, `tenant_id`, `model_id`, `provider_id`, `upstream_model`, `priority` (kecil = dicoba dulu), `weight`, `enabled` | Satu model punya >= 1 target. Target `priority` sama dibagi menurut `weight`; sisanya jadi cadangan |
| `model_prices` | `id`, `tenant_id`, `model_id`, `input_price_per_1m`, `output_price_per_1m`, `cache_read_price_per_1m` (NULL), `cache_write_price_per_1m` (NULL), `min_input_tokens` (0 = tier dasar), `currency`, `effective_from` | Riwayat harga; yang berlaku = kumpulan `effective_from` terbesar yang <= waktu permintaan, lalu tier dengan `min_input_tokens` terbesar yang <= token prompt. Token ter-cache ditagih harga cache read bila ada. Unik `(model_id, effective_from, min_input_tokens)` |

**Akses dan kebijakan**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `projects` | `id`, `public_id`, `tenant_id`, `name`, `status` (`active`/`suspended`), `log_content` (default 0), `content_retention_days`, `deleted_at`, `row_version`, `created_at` | |
| `api_keys` | `id`, `public_id`, `tenant_id`, `project_id`, `name`, `key_prefix` (unik), `key_hash`, `allowed_ips_json`, `expires_at`, `revoked_at`, `last_used_at`, `created_by_user_id`, `created_at` | Key asli ditampilkan sekali saat dibuat |
| `policies` | `id`, `tenant_id`, `scope` (`tenant`/`project`/`key`), `scope_id`, `max_tokens_per_request`, `requests_per_minute`, `daily_token_quota`, `monthly_token_quota`, `monthly_budget`, `allowed_models_json`, `enabled`, `row_version` | Unik `(tenant_id, scope, scope_id)`; `scope_id` selalu terisi (id tenant untuk scope `tenant`). `NULL` = tidak dibatasi. **Semua scope yang berlaku dievaluasi sendiri-sendiri** terhadap pemakaiannya (kuota tenant menghitung seluruh tenant, project hanya project itu, key hanya key itu); permintaan lolos hanya bila lolos di semua. Daftar model = irisan; batas token per request = yang terkecil (juga dibandingkan dengan batas model) |

**Pemakaian**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `usage_logs` | `id`, `tenant_id`, `project_id`, `api_key_id`, `model_id`, `provider_id`, `request_id` (GUID), `status` (`ok`/`denied`/`error`), `http_status`, `denied_reason`, `finish_reason`, `input_tokens`, `output_tokens`, `cached_tokens`, `reasoning_tokens`, `input_price_used`, `output_price_used`, `cost`, `currency`, `latency_ms`, `ttfb_ms`, `attempts`, `fallback_used`, `upstream_status`, `end_user` (dari field `user` di request), `tags`, `client_ip`, `user_agent`, `created_at` | Metadata saja. Indeks `(tenant_id, created_at)` dan `(tenant_id, project_id, created_at)` |
| `usage_daily` | PK `(tenant_id, project_id, api_key_id, model_id, day)`, `requests`, `denied`, `errors`, `input_tokens`, `output_tokens`, `cost` | Di-upsert tiap permintaan; dasar pengecekan kuota dan laporan. Total bulan = jumlah hari |

**Sistem**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `audit_logs` | `id`, `tenant_id`, `user_id`, `action`, `entity`, `entity_id`, `detail_json`, `ip`, `created_at` | Append-only; login DB aplikasi hanya `INSERT` (G4) |
| `data_protection_keys` | `id`, `friendly_name`, `xml` | Penyimpan kunci ASP.NET Data Protection; ikut backup |
| `platform_settings` | `key` (PK), `value_json`, `updated_at` | Pengaturan global instalasi |

### Tingkat 2 (G4)

`request_bodies` (`usage_log_id`, `tenant_id`, `request_json`, `response_json`, `expires_at`; hanya bila `projects.log_content = 1`), `alert_rules`, `alert_events`, `webhooks`, `webhook_deliveries`, `provider_health` (circuit breaker), `job_runs`, `user_mfa`.

### Ditunda

`guardrail_rules`, `response_cache`, `prompt_templates`, `invoices`.

Konstanta (di `AiGateway.Core`), tanpa string literal tersebar: `Roles`, `TenantStatuses`, `ProjectStatuses`, `UsageStatuses`, `PolicyScopes`, `TokenPurposes`.

---

## 7. Manajemen

### Peran

| Role | Hak |
| --- | --- |
| `platform_admin` | Kelola tenant dan plan, suspend tenant, lihat pemakaian semua tenant (tanpa isi prompt) |
| `owner` | Semua di dalam tenant, termasuk kelola user |
| `admin` | Provider, model, project, key, kebijakan |
| `viewer` | Hanya lihat konfigurasi dan laporan |

### Fitur UI admin

- Dashboard: permintaan, token, biaya, error, ditolak (hari ini, 7 hari, bulan berjalan).
- Providers: tambah, uji koneksi, ubah key (key tidak pernah ditampilkan lagi, hanya `key_hint`), nonaktifkan.
- Models: alias, pemetaan, harga, cadangan.
- Projects: buat, suspend, opsi simpan isi + masa simpan.
- API keys: buat (tampil sekali), cabut, kedaluwarsa, batasan IP.
- Kebijakan: per tenant, project, atau key.
- Pemakaian: filter per project, model, rentang waktu; ekspor CSV.
- Audit log dan manajemen user tenant.
- Platform: daftar tenant, plan, suspend.

---

## 8. Keamanan

- **API key:** format `gw_<prefix>_<secret>` dengan secret acak 32 byte. Disimpan sebagai SHA-256, dibandingkan secara constant-time. Log tidak pernah memuat key.
- **Key provider:** dienkripsi dengan Data Protection sebelum disimpan; kunci Data Protection disimpan di folder terproteksi atau database, di luar repo. Respons API tidak pernah mengembalikan key provider.
- **Anti-SSRF** (penting karena tenant mengisi `base_url`): hanya `https`; tolak host yang me-resolve ke loopback, link-local, atau jaringan privat kecuali platform admin mengizinkan eksplisit lewat konfigurasi; tanpa redirect; resolve ulang saat dipanggil.
- **Login admin:** kunci akun sementara setelah N gagal, rate limit endpoint login, JWT berumur pendek + refresh token, `security_stamp` membatalkan semua token.
- **Privasi:** isi prompt dan respons tidak disimpan kecuali project menyalakan `log_content`; tersimpan dengan `expires_at` dan dihapus otomatis.
- **Batas permintaan:** ukuran body maksimal, timeout upstream, jumlah permintaan bersamaan per tenant.
- **Secret** hanya lewat `dotnet user-secrets` atau variabel lingkungan; tidak ada di file ter-commit.
- **Audit:** setiap perubahan konfigurasi, login, pembuatan/pencabutan key tercatat di `audit_logs`.
- Validasi semua input admin dan input klien sebagai tidak dipercaya; query selalu berparameter.

---

## 9. Fase

Setiap fase: kerjakan tugasnya, jalankan `dotnet build`, `dotnet test`, dan `npm run build` di `gateway/web` bila disentuh, cek kriteria selesai, catat di `DECISIONS.md`.

### G0 — Fondasi
- [x] Solution `gateway/` dengan `Core`, `Api`, `Tests`; `web/` Vite React TS.
- [x] Entity, `GatewayDbContext`, konfigurasi snake_case, migration awal, database `AiGateway`.
- [x] Options dan validasi startup; konstanta.
- [x] Seed platform admin dari konfigurasi/secret (tanpa password di file); `/healthz`.

**Selesai bila:** build dan test lulus, semua tabel ada, gateway start, `/healthz` 200.

### G1 — Proxy inti
- [x] Provider, model, project, API key (`ProvisioningService`; seed dev lewat `DevSeed:UpstreamApiKey`).
- [x] Autentikasi API key, `POST /v1/chat/completions`, `GET /v1/models`, format error OpenAI.
- [x] Dekripsi key provider, forwarding, fallback antar route, `usage_logs` + `usage_daily`.
- [x] Katalog master data + sinkronisasi dari OpenCode Console API (`GET /console/api/v2/config`).
- [x] Uji nyata ke OpenCode `deepseek-v4.1-flash` dan `glm-5.3-flash` lewat gateway.

**Selesai bila:** curl dengan API key gateway mendapat jawaban model; key salah ditolak 401; usage tercatat.

### G2 — Kebijakan
- [x] Engine kebijakan (tenant, project, key dievaluasi bersama), rate limit per menit, kuota token harian/bulanan, anggaran bulanan, daftar model.
- [x] Fallback antar route (G1), batas plan tenant (request/token per bulan, jumlah project dan key aktif).
- [x] Test: tiap jenis penolakan, irisan daftar model, batas token terkecil, kuota per scope, plan, rate limit (sliding window).

**Selesai bila:** setiap batas menghasilkan penolakan dengan alasan yang benar, tercatat di `usage_logs`.

### G3 — Management
- [x] Login admin (JWT + refresh), role, kunci akun.
- [x] API admin dan platform; test isolasi lintas tenant untuk semua endpoint.
- [x] UI admin sesuai bagian 7.

**Selesai bila:** seluruh konfigurasi bisa dikelola dari UI tanpa menyentuh database, dan test lintas tenant lulus.

### G4 — Pengerasan
- [x] Enkripsi key provider dengan Data Protection, **dan lindungi kunci Data Protection itu sendiri** (saat ini tersimpan sebagai XML polos di `data_protection_keys`: pakai sertifikat atau DPAPI), anti-SSRF, RLS SQL Server.
- [x] `audit_logs`, retensi log dan `request_bodies`, rekap, alert batas kuota.
- [x] Rate limit login, batas body/timeout, metrik dasar.

**Selesai bila:** uji keamanan manual (SSRF, IDOR lintas tenant, brute force login, key bocor di log) lulus.

### G5 — Handover
- [x] Dokumentasi instalasi dan operasional, skrip instal (Windows Service), data awal, backup/restore, panduan upgrade.
- [x] Contoh integrasi (curl, .NET, Python).

**Selesai bila:** orang di luar tim bisa memasang dan menjalankannya dari dokumen. (Deliverable ada; uji pasang di mesin bersih belum dijalankan — lihat `DECISIONS.md` G5.)

Setelah G5: Fase 2 chatbot (`DESIGN.md`) dengan `Ai:BaseUrl` mengarah ke gateway.

---

## 10. Dampak ke chatbot

- Tabel `ai_models`, `ai_policies`, `ai_calls` **tidak dibuat** di database chatbot; pemakaian AI tercatat di gateway.
- `Ai:ApiKey` di worker chatbot berisi API key gateway, bukan key provider.
- `DESIGN.md` §5, §9, §10 diperbarui saat Fase 2 dimulai.

---

## 11. Pertanyaan terbuka

1. Nama produk dan namespace final.
2. Angka default plan dan kebijakan (rate limit, kuota).
3. Streaming (`stream: true`) masuk fase mana; sekarang ditunda sampai setelah G2.
4. Format klien kedua (Anthropic Messages) dan provider non-OpenAI; ditunda.
5. Penagihan (invoice, pembayaran) di luar cakupan; gateway hanya menyediakan data pemakaian dan biaya.
