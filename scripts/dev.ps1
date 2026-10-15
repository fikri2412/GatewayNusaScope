<#
.SYNOPSIS
  Menjalankan Operator AI untuk pengembangan: cek secret, terapkan migration, lalu API (5080), web (5175), dan gateway (5090)
  masing-masing di jendela PowerShell sendiri. Worker TIDAK dijalankan di sini: dispatcher di API menyalakannya per tiket.

.PARAMETER NoGateway
  Lewati gateway (pakai bila sudah berjalan di tempat lain).

.EXAMPLE
  .\scripts\dev.ps1
#>
param([switch]$NoGateway)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$api = Join-Path $root 'src/OperatorAi.Api'

# Secret yang wajib ada di user-secrets API (nilainya tidak pernah dicetak).
$secrets = dotnet user-secrets list --project $api
foreach ($key in 'Auth:JwtKey', 'Internal:Key', 'Ai:ApiKey') {
    if (-not ($secrets | Select-String -SimpleMatch "$key =")) {
        throw "Secret '$key' belum diisi. Contoh: dotnet user-secrets set `"$key`" `"<nilai>`" --project src/OperatorAi.Api (lihat README.md)"
    }
}

# Port web mengikuti vite.config.ts; origin yang sama diizinkan CORS API.
foreach ($port in 5080, 5175) {
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        throw "Port $port sudah dipakai proses lain. Hentikan dulu, lalu jalankan ulang."
    }
}

Write-Host 'Menerapkan migration database...'
dotnet ef database update --project (Join-Path $root 'src/OperatorAi.Shared') --startup-project $api
if ($LASTEXITCODE -ne 0) { throw 'dotnet ef database update gagal.' }

function Start-Window($title, $dir, $command) {
    Start-Process powershell -ArgumentList '-NoExit', '-Command', "`$Host.UI.RawUI.WindowTitle = '$title'; Set-Location '$dir'; $command"
}

if (-not $NoGateway) {
    # Environment Development diperlukan gateway untuk seed dev dan appsettings.Development.
    Start-Window 'gateway :5090' (Join-Path $root 'gateway') '$env:ASPNETCORE_ENVIRONMENT = "Development"; dotnet run --project src/AiGateway.Api'
}
# dotnet run (bukan apphost .exe, yang diblokir di sebagian mesin); build API ikut membangun DLL kedua worker.
# Api__BaseUrl diwarisi worker yang dinyalakan dispatcher dan menimpa user-secrets (mis. alamat IIS Express), jadi notify worker pasti sampai ke API ini.
Start-Window 'api :5080' $root '$env:Api__BaseUrl = "http://localhost:5080"; dotnet run --project src/OperatorAi.Api'
if (-not (Test-Path (Join-Path $root 'web/node_modules'))) { Push-Location (Join-Path $root 'web'); npm install; Pop-Location }
# VITE_API_URL di proses menimpa web/.env.development (yang bisa menunjuk ke IIS Express/Visual Studio), jadi web pasti memanggil API di atas.
Start-Window 'web :5175' (Join-Path $root 'web') '$env:VITE_API_URL = "http://localhost:5080"; npm run dev'

Write-Host 'Selesai dijalankan. Buka http://localhost:5175 lalu daftar akun baru. Tutup jendela masing-masing untuk berhenti.'
