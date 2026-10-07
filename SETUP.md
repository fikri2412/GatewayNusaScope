# Setup Project — Kerangka Fase 0

Instruksi ini untuk coding agent. Jalankan langkah secara berurutan di PowerShell dari root repo (`C:\TempatAI\ChatBot`). Setelah setiap langkah, cek hasilnya. Bila ada perintah yang gagal, **berhenti**, laporkan error-nya, dan jangan menebak perbaikan yang mengubah stack.

Setelah dokumen ini selesai, lanjutkan sisa Fase 0 sesuai `DESIGN.md` bagian 15.

---

## Langkah 0 — Cek prasyarat

```powershell
dotnet --list-sdks
node -v
dotnet ef --version
```

- Harus ada SDK `9.0.x`. Boleh ada SDK lain (misalnya 10); semua project tetap dikunci ke `net9.0`.
- Node.js minimal versi 20.
- Bila `dotnet ef` belum ada: `dotnet tool install --global dotnet-ef --version 9.*`.
- Bila SDK 9 atau Node tidak ada: berhenti dan minta user memasangnya.

---

## Langkah 1 — Rapikan dokumen

Semua dokumen (`AGENTS.md`, `DESIGN.md`, `SETUP.md`, `DECISIONS.md`) tetap di root repo; tidak ada folder `docs/`. Buat folder kode:

```powershell
New-Item -ItemType Directory -Force src, tests | Out-Null
```

Buat `DECISIONS.md` dengan isi:

```markdown
# Catatan Keputusan

Diisi oleh agent setelah setiap fase. Format:

## YYYY-MM-DD — Fase N

- Yang dikerjakan:
- Keputusan dan alasannya:
- Penyimpangan dari DESIGN.md (bila ada):
- Hal yang perlu dicek manusia:
```

`AGENTS.md` tetap di root.

---

## Langkah 2 — Solution dan project

```powershell
dotnet new sln -n OperatorAi
dotnet new classlib -n OperatorAi.Shared -o src\OperatorAi.Shared -f net9.0
dotnet new webapi   -n OperatorAi.Api -o src\OperatorAi.Api -f net9.0
dotnet new worker   -n OperatorAi.Worker.Operator -o src\OperatorAi.Worker.Operator -f net9.0
dotnet new worker   -n OperatorAi.Worker.Specialist -o src\OperatorAi.Worker.Specialist -f net9.0
dotnet new xunit    -n OperatorAi.Tests -o tests\OperatorAi.Tests -f net9.0

Get-ChildItem -Recurse -Filter *.csproj | ForEach-Object { dotnet sln add $_.FullName }
```

Catatan: SDK 10 membuat file `.slnx` alih-alih `.sln`. Keduanya bisa dipakai; catat mana yang terbentuk di `DECISIONS.md`.

Cek: setiap `.csproj` berisi `<TargetFramework>net9.0</TargetFramework>`.

---

## Langkah 3 — Referensi antarproject

```powershell
$shared = "src\OperatorAi.Shared\OperatorAi.Shared.csproj"
dotnet add src\OperatorAi.Api\OperatorAi.Api.csproj reference $shared
dotnet add src\OperatorAi.Worker.Operator\OperatorAi.Worker.Operator.csproj reference $shared
dotnet add src\OperatorAi.Worker.Specialist\OperatorAi.Worker.Specialist.csproj reference $shared
dotnet add tests\OperatorAi.Tests\OperatorAi.Tests.csproj reference $shared `
    src\OperatorAi.Worker.Operator\OperatorAi.Worker.Operator.csproj `
    src\OperatorAi.Worker.Specialist\OperatorAi.Worker.Specialist.csproj
```

---

## Langkah 4 — Paket NuGet

Paket EF Core **harus versi 9.x**; versi 10 tidak berjalan di `net9.0`. Tambahkan dengan mengedit `.csproj` langsung (jangan `dotnet add package` tanpa versi).

`src\OperatorAi.Shared\OperatorAi.Shared.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="9.0.*" />
  <PackageReference Include="Dapper" Version="2.*" />
  <PackageReference Include="Microsoft.Data.SqlClient" Version="6.*" />
  <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
</ItemGroup>
```

`src\OperatorAi.Api\OperatorAi.Api.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.0.*" />
</ItemGroup>
```

`src\OperatorAi.Worker.Specialist\OperatorAi.Worker.Specialist.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="ClosedXML" Version="0.*" />
  <PackageReference Include="QuestPDF" Version="2025.*" />
</ItemGroup>
```

Lalu:

```powershell
dotnet restore
```

Bila restore gagal karena versi floating tidak ditemukan, pilih versi stabil terbaru yang kompatibel dengan `net9.0`, tulis versi pastinya, dan catat di `DECISIONS.md`.

Aktifkan nullable dan implicit usings di semua `.csproj` (biasanya sudah default dari template; pastikan ada):

```xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
```

---

## Langkah 5 — Bersihkan template

- Hapus `src\OperatorAi.Shared\Class1.cs`.
- Hapus `tests\OperatorAi.Tests\UnitTest1.cs`, ganti dengan satu test placeholder yang lulus (`SmokeTests.cs`).
- Di `src\OperatorAi.Api\Program.cs`: hapus endpoint contoh `weatherforecast` dan record `WeatherForecast`. Sisakan builder, `app.Run()`, dan OpenAPI bawaan.
- Hapus file `.http` bawaan di project API.
- Di `src\OperatorAi.Api\Properties\launchSettings.json`: profil `http` memakai `"applicationUrl": "http://localhost:5080"`. Hapus profil `https` agar tidak membingungkan.
- Di kedua worker: ganti nama `Worker.cs` menjadi `OperatorWorker.cs` dan `SpecialistWorker.cs` (nama kelas ikut), perbarui `Program.cs`. Isi `ExecuteAsync` cukup log "started" lalu `Task.Delay` dalam loop sampai dibatalkan.
- Buat folder di Shared (dengan file `.gitkeep` bila kosong): `Data`, `Queue`, `Contracts`, `Ai`, `Notify`, `Storage`.
- Buat folder `Tools` di worker operator, serta `Handlers`, `Excel`, `Pdf` di worker spesialis.

---

## Langkah 6 — Konfigurasi awal

Buat `appsettings.Development.json` di API dan kedua worker dengan isi konfigurasi dari `DESIGN.md` bagian 4. Sesuaikan:

- `Storage:RootPath` = `C:\\TempatAI\\ChatBot\\storage`
- Connection string: gunakan instance SQL Server yang tersedia. Cek dengan `sqllocaldb info` (LocalDB) atau tanyakan ke user nama instance SQL Express-nya. Jangan menebak; bila ragu, tanyakan.

Inisialisasi user-secrets (tanpa mengisi nilai rahasia, user yang mengisi sendiri):

```powershell
dotnet user-secrets init --project src\OperatorAi.Api
dotnet user-secrets init --project src\OperatorAi.Worker.Operator
dotnet user-secrets init --project src\OperatorAi.Worker.Specialist
```

Tulis di `DECISIONS.md` daftar perintah `dotnet user-secrets set` yang harus dijalankan user (lihat `DESIGN.md` bagian 4). **Jangan** mengisi API key sendiri.

---

## Langkah 7 — Frontend

```powershell
npm create vite@latest web -- --template react-ts
cd web
npm install
npm install @microsoft/signalr
Set-Content .env.development "VITE_API_URL=http://localhost:5080"
cd ..
```

- Pastikan dev server Vite berjalan di port `5173` (default). Bila perlu, set `server.port = 5173` dan `strictPort = true` di `vite.config.ts`.
- Template mungkin memasang React 19, bukan 18. Itu diterima; catat versinya di `DECISIONS.md`.
- Hapus isi demo (counter, logo) di `App.tsx` dan CSS bawaan; ganti dengan halaman sederhana bertuliskan "Operator AI".

---

## Langkah 8 — Git

```powershell
dotnet new gitignore
Add-Content .gitignore "`n# Operator AI`nstorage/`nnode_modules/`n.env.local`nweb/dist/"
git init
```

---

## Langkah 9 — Verifikasi kerangka

```powershell
dotnet build
dotnet test
cd web; npm run build; cd ..
```

Ketiganya harus sukses tanpa error. Warning nullable dari template boleh dibereskan sekalian.

---

## Langkah 10 — Lanjut sisa Fase 0

Kerjakan bagian Fase 0 di `DESIGN.md` bagian 15 yang belum tercakup di atas:

- Entity dan `AppDbContext` dengan nama tabel/kolom snake_case sesuai bagian 5.
- Konstanta `Statuses`, `QueueTypes`, `JobTypes`.
- Options class untuk setiap section konfigurasi, di-bind dan divalidasi saat startup (`ValidateOnStart`).
- Daftarkan `AppDbContext` di API.
- Migration awal di `src/OperatorAi.Shared/Data/Migrations`:

```powershell
dotnet ef migrations add Initial --project src\OperatorAi.Shared --startup-project src\OperatorAi.Api --output-dir Data\Migrations
dotnet ef database update --project src\OperatorAi.Shared --startup-project src\OperatorAi.Api
```

- Verifikasi semua tabel ada (`conversations`, `messages`, `attachments`, `queue_items`, `jobs`, `ai_calls`).
- Tulis ringkasan Fase 0 di `DECISIONS.md`, termasuk apa saja yang perlu dilakukan user secara manual (mengisi secrets).
