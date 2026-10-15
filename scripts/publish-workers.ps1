<#
.SYNOPSIS
  Mempublikasikan kedua worker (Operator, Specialist) sebagai Release + ReadyToRun ke artifacts/workers/.
  Worker on-demand memulai proses baru untuk tiap tiket, jadi waktu start (JIT) terbayar di setiap pesan; ReadyToRun
  memangkas ±0,7 dtk per tiket dibanding build Debug yang dipakai default (hasil ukur: IMPROVEMENTS.md bagian 14).

.DESCRIPTION
  Hasilnya dipakai dispatcher lewat Dispatcher:OperatorDll dan Dispatcher:SpecialistDll (path absolut diizinkan).
  Dua key itu bisa diubah dari menu Pengaturan gateway (namespace chatbot) tanpa restart API; nilai baru berlaku untuk
  worker yang dinyalakan sesudahnya. Publikasi TIDAK otomatis mengikuti build: ulangi skrip ini setiap kode worker/Shared berubah.

.PARAMETER Runtime
  RID target (default win-x64). Hanya framework-dependent: dispatcher menjalankan `dotnet <dll>`.

.EXAMPLE
  .\scripts\publish-workers.ps1
#>
param([string]$Runtime = 'win-x64')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts/workers'

foreach ($worker in 'Operator', 'Specialist') {
    $target = Join-Path $out $worker.ToLowerInvariant()
    Write-Host "Publikasi Worker.$worker (ReadyToRun) -> $target"
    dotnet publish (Join-Path $root "src/OperatorAi.Worker.$worker") -c Release -r $Runtime --self-contained false `
        -p:PublishReadyToRun=true -o $target
    if ($LASTEXITCODE -ne 0) { throw "Publikasi Worker.$worker gagal." }
}

Write-Host ''
Write-Host 'Selesai. Pasang di Pengaturan gateway (namespace chatbot) atau appsettings API:'
Write-Host "  Dispatcher:OperatorDll   = $(Join-Path $out 'operator/OperatorAi.Worker.Operator.dll')"
Write-Host "  Dispatcher:SpecialistDll = $(Join-Path $out 'specialist/OperatorAi.Worker.Specialist.dll')"
