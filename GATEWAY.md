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

Paket tambahan yang dibutuhkan (catat di `DECISIONS.md` saat dipakai): `Microsoft.AspNetCore.Authentication.JwtBearer` (login admin) dan `Microsoft.Extensions.Hosting.WindowsServices` (lifetime Windows Service agar terpasang lewat `scripts/install-service.ps1`). Enkripsi key provider memakai ASP.NET Core Data Protection (sudah ada di framework).

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
├─ web/                      # UI admin (React + Vite + TS); build ke Api/wwwroot
├─ sql/                      # tenant-security.sql (RLS, G4)
├─ scripts/                  # install-service.ps1 (Windows Service)
├─ examples/                 # contoh integrasi curl / Python / .NET
└─ README.md                 # panduan pasang dan operasional (G5)
```

Database terpisah: `AiGateway` di SQL Server yang sama saat development.

---

## 4. Arsitektur

Tiga bidang dalam satu proses:

1. **Data plane** (dipanggil aplikasi pelanggan), auth API key:
   - `POST /v1/chat/completions` format OpenAI, respons utuh atau streaming SSE (`stream: true`).
   - `GET /v1/models` daftar model enabled yang boleh dipakai key itu, dengan capability tri-state, readiness konfigurasi, dan batas output efektif.
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
5. Terapkan semua kebijakan yang berlaku (tenant, project, key) secara terpisah —
   project/key tidak bisa melonggarkan batas tenant:
   - daftar model = irisan semua daftar; batas max_tokens per request = yang terkecil
   - rate limit per menit (penghitung memori); 429 + Retry-After
   - kuota harian/bulanan dan anggaran dihitung per scope dari usage_daily; 429 dengan alasan
6. Dekripsi key provider, panggil upstream (timeout, tanpa redirect)
7. Bila `stream: true`: teruskan SSE event demi event; fallback antar route hanya sampai event pertama terkirim,
   kegagalan setelah itu terminal; event error dikirim bila deadline/downstream masih mengizinkan, lalu tutup tanpa [DONE]
8. Catat usage_logs + upsert usage_daily (juga untuk permintaan ditolak/gagal); untuk stream dicatat saat berakhir.
   Usage hilang/parsial/gagal ditandai usage_incomplete; angka yang diketahui bukan tagihan final
9. Kembalikan respons upstream — untuk stream sudah berjalan sejak langkah 7; error dibentuk ulang ke format error OpenAI
```

Bila upstream gagal (5xx, timeout), route lain untuk model yang sama dicoba menurut urutan `priority`
(kecil dulu, target berprioritas sama dibagi menurut `weight`); jumlah percobaan dan pemakaian
cadangan tercatat di `usage_logs.attempts`/`fallback_used`. Kesalahan permintaan (4xx) tidak dicoba
ulang ke route lain. Untuk streaming, fallback hanya berlaku sebelum event pertama diteruskan ke klien —
setelah itu stream berjalan di route yang sama sampai selesai atau gagal.

### Katalog model untuk chatbot (6A2)

`/v1/models` mempertahankan `id/object/created/owned_by`, lalu menambah `capabilities:{tools,streaming,vision}` (bool/null), `readiness:{available,reason}` dan `limits:{max_output_tokens}`. Eligibility memakai `ForwardingTargets` yang sama dengan ChatProxy: route/provider enabled, provider belum dihapus, credential active. Bukan health probe; alias belum ready tetap terlihat, reason aman `no_available_provider`. Limit adalah minimum model dan semua policy aktif yang berlaku, bukan kapasitas konteks atau sisa kuota.

`models.supports_tools/supports_streaming/supports_vision` nullable (bit NULL): null = belum dikonfirmasi, bukan tebakan dari alias. Model legacy/custom/discover tetap unknown; import satu route dari katalog memakai flag tools **dan** vision katalog, streaming unknown. `supports_vision` (kolom baru, migration `G6CVisionCapabilities`) adalah konfirmasi admin per model bahwa target menerima gambar inline — terpisah dari metadata `catalog_models.supports_vision` milik katalog provider. Baris model yang sudah ada sebelum migration tetap NULL = unknown sampai admin mengonfirmasi. Create model menerima `supportsVision`; patch memakai pasangan `supportsVision`/`clearSupportsVision` (clear → NULL) seperti tools/streaming; halaman Model memakai editor tri-state yang sama (Belum dikonfirmasi/Didukung/Tidak didukung). Admin mengonfirmasi seluruh set target, termasuk route fallback nonaktif. Perubahan target `(providerId,upstreamModel)` atau BaseUrl provider menghapus ketiga konfirmasi secara atomik; weight/priority baru, rename, enable/disable, atau rotasi credential tidak menghapusnya. Type provider tidak memiliki API mutasi; katalog family berlaku pada import berikutnya.

Browser chatbot hanya memanggil `/api/models` terautentikasi; key gateway tetap di server dan scope seluruh user adalah key instalasi. API menerima model eksplisit saat enqueue; Operator/Analyze memakai snapshot workflow, bukan setting `Ai:Model` atau alias pertama. Model dicabut -> terminal, bukan fallback model.

Untuk OpenCode Go, data plane menerima `x-opencode-session` stabil per conversation (satu nilai 1–200 huruf/angka/`._-`) dan User-Agent aplikasi asli. Keduanya diteruskan hanya untuk `opencode.ai/inference/go/`; header session bukan identitas tenant/auth. Chatbot memakai id conversation dan `OperatorAi/1.0`. Uji nyata wajib route Go saja, tanpa Zen fallback: [kontrak Go](https://opencode.ai/docs/go/#where-can-i-use-it).

### Keluarga API upstream: Chat Completions, Anthropic Messages, OpenAI Responses

Klien selalu memakai `POST /v1/chat/completions`. Upstream bisa berbicara tiga family (`ApiFamilies.Forwardable`): `openai_chat` (diteruskan apa adanya), `anthropic_messages`, dan `openai_responses` (keduanya diterjemahkan oleh `IFamilyAdapter`: `AnthropicAdapter`/`ResponsesAdapter` untuk request dan respons utuh, `ChatChunkReader` turunannya untuk stream). Family ditentukan **per route** dari `catalog_models` template provider (`template_id` + `upstream_model`); provider custom tanpa template selalu `openai_chat`, jadi tidak ada kolom/migration baru. Endpoint dari base URL provider: Anthropic → `…/anthropic/v1/messages` (segmen `/openai/` diganti `/anthropic/`; tanpa segmen itu dipakai apa adanya) dengan header `anthropic-version: 2023-06-01`; Responses → `…/openai/v1/responses`. Auth tetap `auth_header`/`auth_prefix` provider. `google_generate` belum diteruskan (tersimpan di katalog, tidak ditawarkan saat impor).

Terjemahan bersama: `system`/`developer` (Anthropic: `system`; Responses: `instructions`), teks dan gambar (inline `data:` base64 untuk png/jpeg/gif/webp, atau URL https yang diteruskan apa adanya — **gateway tidak pernah mengambil URL gambar sendiri**), `tool_calls`/pesan `tool` menjadi blok tool upstream, `tools`/`tool_choice`, `max_tokens`, `temperature`, `top_p`, `user`. Konten tanpa padanan (mis. audio) ditolak 400 `unsupported_request`; field lain (n, penalti, `response_format`) diabaikan; blok thinking/reasoning upstream tidak diteruskan. Khusus Anthropic: `max_tokens` wajib (klien, lalu batas model, lalu 4096), `temperature` dibatasi ≤ 1, `stop` → `stop_sequences`, hasil tool digabung dengan giliran user berikutnya. Khusus Responses: `store:false` dan seluruh percakapan dikirim ulang di `input`; `max_tokens` → `max_output_tokens` (hanya bila dikirim klien/kebijakan); `strict` fungsi false kecuali klien menyebut true (default Responses true, Chat Completions false); `stop` tidak punya padanan dan diabaikan. Usage selalu dilaporkan sebagai OpenAI: Anthropic `prompt_tokens` = `input_tokens` + cache baca + cache tulis, `cached_tokens` = cache baca, dan `prompt_tokens_details.cache_creation_tokens` = cache tulis (tambahan non-standar OpenAI supaya cache tulis ditagih dengan `cache_write_price_per_1m`, bukan harga input; tanpa harga cache tulis jatuh ke harga input, tiap token ditagih sekali); Responses `input_tokens`/`output_tokens` sudah mencakup cache dan reasoning, `reasoning_tokens` dibaca dari `output_tokens_details`. Penolakan Responses mengalir sebagai `delta.refusal` (SSE) / `message.refusal` (REST); status `incomplete` Responses didahulukan dari `tool_calls` (`length`/`content_filter`, karena argumen tool bisa terpotong); `message_delta` Anthropic tanpa `stop_reason` tidak membuat alasan selesai (stream tanpa alasan akhir dianggap terputus, tanpa `[DONE]`). Stream diubah menjadi `chat.completion.chunk` + chunk usage + `[DONE]`; event error pertama (Anthropic `error`, Responses `error`/`response.failed`) masih bisa jatuh ke route berikutnya.

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
| `catalog_models` | `id`, `template_id`, `upstream_model`, `display_name`, `api_family` (`openai_chat`/`openai_responses`/`anthropic_messages`/`google_generate`), `context_window`, `max_input_tokens`, `max_output_tokens`, `input_price_per_1m`, `output_price_per_1m`, `cache_read_price_per_1m`, `cache_write_price_per_1m`, `extra_tiers_json` (tier prompt panjang), `currency`, `supports_tools`, `supports_vision`, `supports_reasoning`, `source` (`seed`/`discovered`/`manual`), `enabled`, `created_at` | Daftar model yang dikenal per template; harga boleh NULL. Unik `(template_id, upstream_model)`. `supports_vision` di sini metadata katalog (seed/sinkronisasi), bukan konfirmasi admin per tenant — konfirmasi itu ada di `models.supports_vision` (nullable). Hanya family `ApiFamilies.Forwardable` (`openai_chat`, `anthropic_messages`, `openai_responses`) yang diteruskan/ditawarkan saat impor; `google_generate` tersimpan tanpa ditawarkan |

Alur tenant: pilih template (atau `custom`) → isi API key milik sendiri (BYOK) → provider terbentuk → **impor model** dari katalog, atau **discover** (gateway memanggil `GET {base_url}/{models_path}` dengan key tenant lalu menawarkan daftar untuk diimpor) → alias model, route, dan harga awal dibuat otomatis dan bisa diubah. Penyedia lain (mis. CommandCode) didaftarkan sebagai provider `custom`: cukup isi URI, API key, dan (bila perlu) nama header auth, lalu daftarkan model secara manual atau lewat discover.

**Konfigurasi AI (milik tenant)**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `providers` | `id`, `public_id`, `tenant_id`, `template_id` (NULL = custom), `name`, `type` (`openai`), `base_url`, `auth_header`, `auth_prefix`, `models_path`, `enabled`, `deleted_at`, `row_version`, `created_at` | Nilai koneksi disalin dari template saat dibuat dan boleh diubah. `base_url` divalidasi anti-SSRF (bagian 8). Unik `(tenant_id, name)` yang belum dihapus |
| `provider_credentials` | `id`, `tenant_id`, `provider_id`, `api_key_encrypted`, `key_hint` (4 karakter terakhir), `status` (`active`/`disabled`), `created_at`, `disabled_at` | Rotasi: buat key baru `active`, lalu `disabled` yang lama. Hanya satu `active` per provider |
| `models` | `id`, `public_id`, `tenant_id`, `alias`, `description`, `max_output_tokens`, `supports_tools`/`supports_streaming`/`supports_vision` (bit NULL), `enabled`, `deleted_at`, `row_version`, `created_at` | Alias unik per tenant; konfirmasi kapabilitas seluruh set route, null = unknown; `G6A2ModelCapabilities` + `G6CVisionCapabilities` (vision, additive; baris lama tetap NULL) |
| `model_routes` | `id`, `tenant_id`, `model_id`, `provider_id`, `upstream_model`, `priority` (kecil = dicoba dulu), `weight`, `enabled` | Satu model punya >= 1 target. Target `priority` sama dibagi menurut `weight`; sisanya jadi cadangan |
| `model_prices` | `id`, `tenant_id`, `model_id`, `input_price_per_1m`, `output_price_per_1m`, `cache_read_price_per_1m` (NULL), `cache_write_price_per_1m` (NULL), `min_input_tokens` (0 = tier dasar), `currency`, `effective_from` | Riwayat harga; yang berlaku = kumpulan `effective_from` terbesar yang <= waktu permintaan, lalu tier dengan `min_input_tokens` terbesar yang <= token prompt. Token ter-cache ditagih harga cache read bila ada. Unik `(model_id, effective_from, min_input_tokens)` |

**Akses dan kebijakan**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `projects` | `id`, `public_id`, `tenant_id`, `name`, `status` (`active`/`suspended`), `log_content` (default 0), `content_retention_days`, `deleted_at`, `row_version`, `created_at` | |
| `api_keys` | `id`, `public_id`, `tenant_id`, `project_id`, `name`, `key_prefix` (unik), `key_hash`, `allowed_ips_json`, `expires_at`, `revoked_at`, `last_used_at`, `created_by_user_id`, `created_at` | Key asli ditampilkan sekali saat dibuat |
| `policies` | `id`, `tenant_id`, `scope` (`tenant`/`project`/`key`), `scope_id`, `max_tokens_per_request`, `requests_per_minute`, `daily_token_quota`, `monthly_token_quota`, `monthly_budget`, `allowed_models_json`, `enabled`, `row_version` | Unik `(tenant_id, scope, scope_id)`; `scope_id` selalu terisi (id tenant untuk scope `tenant`). `NULL` = tidak dibatasi. **Semua scope yang berlaku dievaluasi sendiri-sendiri** terhadap pemakaiannya (kuota tenant menghitung seluruh tenant, project hanya project itu, key hanya key itu); permintaan lolos hanya bila lolos di semua. Daftar model = irisan; batas token per request = yang terkecil (juga dibandingkan dengan batas model) |
| `app_settings` | `id`, `tenant_id`, `namespace`, `key`, `value`, `value_type` (`int`/`bool`/`text`), `description`, `updated_by_user_id`, `updated_at`, `row_version` | Pengaturan key/value per tenant untuk aplikasi klien (G6); unik `(tenant_id, namespace, key)`; maks 200 per tenant; bukan tempat secret |
| `app_status_reports` | tenant/project/key server-side, namespace, versi/hash, JSON nilai/metadata aman, status sinkron, rejected/ignored/reason, counts queue/job, active policies, interval, observed_at, received_at | Satu laporan terbaru per `(tenant_id, api_key_id, namespace)`; migration `G6A1AppStatusReports`, tenant filter + RLS; tanpa pesan/file/teks prompt/secret |

**Pemakaian**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `usage_logs` | `id`, `tenant_id`, `project_id`, `api_key_id`, `model_id`, `provider_id`, `request_id` (GUID), `status` (`ok`/`denied`/`error`), `http_status`, `denied_reason`, `finish_reason`, `input_tokens`, `output_tokens`, `cached_tokens`, `reasoning_tokens`, `input_price_used`, `output_price_used`, `cost`, `currency`, `usage_incomplete`, `latency_ms`, `ttfb_ms`, `attempts`, `fallback_used`, `upstream_status`, `end_user` (dari field `user` di request), `tags`, `client_ip`, `user_agent`, `created_at` | Metadata saja. `usage_incomplete` membedakan angka tercatat dari pemakaian lengkap; penolakan/tanpa attempt false. Indeks `(tenant_id, created_at)` dan `(tenant_id, project_id, created_at)` |
| `usage_daily` | PK `(tenant_id, project_id, api_key_id, model_id, day)`, `requests`, `denied`, `errors`, `input_tokens`, `output_tokens`, `cost`, `incomplete_usage_requests` | Di-upsert tiap permintaan; dasar kuota/laporan atas token diketahui, bukan estimasi. Count incomplete tetap utuh saat retensi; total bulan = jumlah hari |

**Sistem**

| Tabel | Kolom | Catatan |
| --- | --- | --- |
| `audit_logs` | `id`, `tenant_id`, `user_id`, `action`, `entity`, `entity_id`, `detail_json`, `ip`, `created_at` | Append-only; login DB aplikasi hanya `INSERT` (G4) |
| `data_protection_keys` | `id`, `friendly_name`, `xml` | Penyimpan kunci ASP.NET Data Protection; ikut backup |
| `platform_settings` | `key` (PK), `value_json`, `updated_at` | Pengaturan global instalasi |

### Tingkat 2 (G4)

`request_bodies` (`usage_log_id`, `tenant_id`, `request_json`, `response_json`, `expires_at`; hanya bila `projects.log_content = 1`), `alert_rules`, `alert_events`, `webhooks`, `webhook_deliveries`, `job_runs` (platform, tanpa `tenant_id`).

### Ditunda

`provider_health` (circuit breaker), `user_mfa`, `guardrail_rules`, `response_cache`, `prompt_templates`, `invoices`.

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
- **Privasi:** isi prompt dan respons tidak disimpan kecuali project menyalakan `log_content`; tersimpan dengan `expires_at` dan dihapus otomatis. Bila pencatatan isi aktif, isi `image_url` berbentuk data URL gambar diganti penanda `[inline image omitted]` sebelum body disimpan — upstream tetap menerima gambar asli.
- **Batas permintaan:** ukuran body permintaan (`Proxy:MaxRequestBytes`, default 4 MB; per byte, termasuk chunked/IIS), ukuran body respons (`Proxy:MaxResponseBytes`), deadline per route (`Proxy:UpstreamTimeoutSeconds`: headers + body REST sukses/error + event SSE pertama sebelum commit; sesudah commit menjadi idle antar baris), durasi total pompa SSE (`Proxy:MaxStreamSeconds`, default 900; read/write/flush/error terminal), dan konkurensi tenant (`Proxy:MaxConcurrentPerTenant`, default 64; kelebihan `429 concurrency_limit_exceeded`). Kelebihan body `413 request_too_large`. Deadline gateway tetap timeout meski pembatalan write menyebabkan RequestAborted; disconnect klien yang lebih dahulu 499.
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

### Pasca-G5 — Streaming
- [x] `stream: true` (SSE): fallback hanya sebelum event pertama, error setelah commit terminal tanpa `[DONE]`, usage known/incomplete dicatat saat berakhir, dan deadline `Proxy:MaxStreamSeconds` mencakup downstream. Error event best effort dalam deadline, bukan alasan menahan koneksi setelah batas habis. Operasional di `gateway/README.md`; hasil GA-01–GA-04 di `IMPROVEMENTS.md` §18.

### G6 — Pengaturan aplikasi per tenant (sisi gateway dan chatbot selesai)
Menu **Pengaturan** di UI gateway untuk konfigurasi aplikasi klien (mis. chatbot), dibaca aplikasi itu lewat API key-nya. Contoh pertama: `Dispatcher:MaxConcurrentOperators` dan `MaxConcurrentSpecialists` milik chatbot (`DESIGN.md` bagian 4 dan 6.1). Bukan tempat secret: nilai disimpan polos dan tampil di UI.
- [x] Tabel `app_settings` (`namespace` ≤ 50, `key` ≤ 100, `value` ≤ 2000, tipe `int`/`bool`/`text`, description ≤ 200, versi/audit), migration `G6AppSettings`; perluasan 6A1 menambah `app_status_reports` dan policy `app_status_isolation_policy`. RLS kini mencakup 18 tabel tenant; ulangi script setelah migration.
- [x] API admin: `GET /admin/api/settings?namespace=`, `PUT` dan `DELETE /admin/api/settings/{namespace}/{key}`. Tulis: owner/admin; baca: viewer. Nilai divalidasi menurut `value_type` (int 32-bit, bool true/false); maks 200 pengaturan per tenant; tiap perubahan tercatat di `audit_logs` (nilai lama dan baru, dipotong 200 karakter).
- [x] API klien: `GET /client/api/settings?namespace=chatbot` dengan `Authorization: Bearer gw_...` (key yang sama dengan data plane; key dicabut, kedaluwarsa, tenant disuspend, atau IP tidak diizinkan → ditolak seperti biasa; tidak dihitung sebagai pemakaian AI). Respons berisi item dan `version` (hash isi); `ETag`/`If-None-Match` → `304` agar polling murah.
- [x] UI: menu **Pengaturan** (daftar per namespace dengan filter; tambah/ubah/hapus dengan input sesuai tipe; viewer hanya baca) sesuai gaya UI yang ada.
- [x] Test: isolasi lintas tenant (baca dan tulis), matriks peran, validasi tipe/batas, audit, autentikasi key di API klien, `304`, dan RLS (host ber-RLS serta cakupan policy).
- [x] Chatbot polling default 15 detik, allowlist atomik untuk Dispatcher + Operator + Queue + output/detail/prompt + budget AI HTTP/SSE + upload/lampiran + dokumen/vision/watchdog. Kandidat invalid ditolak utuh; last-known-good/lokal dan hapus kembali ke baseline. Snapshot `ai`/`upload`/`document` ikut hash dan diwariskan ke worker/job/callback/retry, bukan appsettings worker. Key/range/cutover pasca-6C ada di `IMPROVEMENTS.md` §16.
- [x] `POST /client/api/app-status` memakai authenticator key yang sama (scope tenant/project/key berasal server), body ≤ 64 KiB, allowlist nilai/metadata, counts nonnegatif, dan ≤128 kelompok kebijakan aktif; tidak dicatat sebagai AI usage.
- [x] `GET /admin/api/app-status?namespace=chatbot` tenant-viewer: configured vs applied, source/hash, rejected/ignored, counts, active policies, receivedAt server. UI Pengaturan menampilkan segar/stale, metadata read-only, dan reload laporan. `stale = age > max(60 detik, 3 × interval laporan)`; belum melapor tidak berarti worker mati/count nol.
- [x] Report hingga 29 nilai kontrol aman dan 30 metadata key termasuk prompt redacted; tidak ada DLL, isi prompt/file/user content/secret. Mengikuti poller API, bukan per-message critical path/per-worker. Satu instalasi API per key; bukan attestation worker/riwayat monitoring. Perluasan runtime memakai store/schema status yang sama, tanpa migration gateway baru.

**Selesai bila:** mengubah `MaxConcurrentOperators` di menu Pengaturan mengubah batas proses chatbot dalam satu siklus polling tanpa restart, dan tenant lain tidak bisa membaca atau mengubahnya. **Terbukti:** browser UI + API/dispatcher + proses worker nyata di database smoke ber-RLS; batas 1 → 3 dalam 13,8 dtk dengan PID API tetap, tenant B tidak mengubah nilai tenant A, gateway offline memakai nilai terakhir/lokal, dan hapus pengaturan mengembalikan batas lokal 4.

### Ekstensi 6C — kapabilitas vision model (chatbot PDF/gambar)

Chatbot 6C (PDF teks/scan + PNG/JPEG) memakai gateway apa adanya untuk model vision:

- `models.supports_vision` nullable lewat migration additive `20261012090000_G6CVisionCapabilities` (beserta designer + snapshot); create/patch admin menerima `supportsVision`/`clearSupportsVision`, dan invalidasi konfirmasi saat target route atau BaseUrl provider berubah mencakup route fallback nonaktif (bagian 4).
- `/v1/models` membawa `capabilities.vision`; chatbot hanya menerima lampiran gambar standalone bila `supportsVision == true` (server menolak bypass UI), dan job/callback/retry memakai snapshot capability saat enqueue (bagian 10).
- Adapter Anthropic/Responses mempertahankan bagian teks + gambar untuk format yang didokumentasikan; gateway tidak mengambil URL gambar sendiri (bagian 4).
- Status rollout: diimplementasikan dan **hanya diterapkan ke database gateway privat lab 6C** (migration terpasang; tri-state terbukti dari browser: ya → tidak → belum dikonfirmasi → ya). Database/binari deployment utama user belum di-upgrade; langkah upgrade ada di `gateway/README.md` §8. Acceptance 6C ada di `IMPROVEMENTS.md` (§8 dan §15 "Hasil implementasi dan verifikasi 6C"). Renderer PNG 6D berjalan di worker chatbot (bukan gateway) dan tidak mengubah kontrak gateway (`IMPROVEMENTS.md` §19); rendering native Linux belum diverifikasi.

Setelah G5: Fase 2 chatbot (`DESIGN.md`) dengan `Ai:BaseUrl` mengarah ke gateway.

---

## 10. Dampak ke chatbot

- Tabel `ai_models`, `ai_policies`, `ai_calls` **tidak dibuat** di database chatbot; pemakaian AI tercatat di gateway.
- `Ai:ApiKey` di worker chatbot berisi API key gateway, bukan key provider.
- `DESIGN.md` §4, §5, §8, §9, §10 sudah diperbarui di Fase 2 chatbot (2026-10-09): format OpenAI, tanpa `ai_calls`.
- 6C (PDF/gambar): lampiran gambar standalone (PNG/JPEG) ditolak `400` di chatbot bila model belum `supportsVision == true`, dan maksimum 6 lampiran per pesan. Snapshot model (termasuk `supportsVision`) diambil saat enqueue lalu diwariskan ke job/callback/retry, jadi perubahan tri-state di gateway tidak mengubah pekerjaan berjalan. PDF dirender/dievaluasi di worker chatbot; gateway hanya meneruskan bagian gambar yang dikirim chatbot (bagian 4).

---

## 11. Pertanyaan terbuka

1. Nama produk dan namespace final.
2. Angka default plan dan kebijakan (rate limit, kuota).
3. ~~Streaming (`stream: true`) masuk fase mana; sekarang ditunda sampai setelah G2.~~ **Selesai pasca-G5**: SSE di `POST /v1/chat/completions` (lihat bagian 4); upstream Anthropic Messages dan OpenAI Responses sudah diterjemahkan (bagian 4), sisa adaptor ada di poin 4.
4. Adaptor upstream `google_generate`, format klien kedua (Anthropic Messages atau Responses di sisi klien), dan provider non-OpenAI lain; ditunda.
5. Penagihan (invoice, pembayaran) di luar cakupan; gateway hanya menyediakan data pemakaian dan biaya.
