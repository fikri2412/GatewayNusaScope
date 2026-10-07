<#
.SYNOPSIS
  Pasang AI Gateway sebagai Windows Service (atau lepaskan).

.DESCRIPTION
  Melakukan dotnet publish, membuat service Windows dengan sc.exe, dan menyalakannya.
  Secret TIDAK ditulis oleh script ini: isi lewat variabel lingkungan service
  (ConnectionStrings__Gateway, Jwt__SigningKey, Seed__AdminEmail, Seed__AdminPassword, ...)
  atau appsettings.Production.json di folder publish (tanpa secret di repo).

.EXAMPLE
  .\install-service.ps1 -ServiceName AiGateway -Port 5090 -PublishDir C:\AiGateway
.EXAMPLE
  .\install-service.ps1 -Uninstall -ServiceName AiGateway
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'AiGateway',
    [int]$Port = 5090,
    [string]$PublishDir = "$env:ProgramData\AiGateway",
    [string]$Project = "$PSScriptRoot\..\src\AiGateway.Api\AiGateway.Api.csproj",
    [string]$Configuration = 'Release',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PublishDir 'AiGateway.Api.exe'

if ($Uninstall) {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force
        sc.exe delete $ServiceName | Out-Null
        Write-Host "Service $ServiceName dihapus."
    } else {
        Write-Host "Service $ServiceName tidak ada."
    }
    return
}

if (-not (Test-Path $Project)) { throw "Project tidak ditemukan: $Project" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK tidak ditemukan di PATH.' }

Write-Host "Publish ke $PublishDir ..."
dotnet publish $Project -c $Configuration -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish gagal.' }

# UI harus sudah di-build ke wwwroot (gateway/web: npm ci && npm run build) sebelum publish.
if (-not (Test-Path (Join-Path $PublishDir 'wwwroot\index.html'))) {
    Write-Warning 'wwwroot\index.html tidak ada: UI belum di-build (cd web; npm ci; npm run build).'
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Environment=Development dimatikan; produksi memakai appsettings.Production.json + variabel lingkungan.
sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= "AI Gateway" | Out-Null
sc.exe description $ServiceName "AI Gateway (multi-tenant AI gateway)" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# URL dan environment untuk service (secret menyusul lewat registry/Environment atau appsettings.Production.json).
$env:SERVICE_NAME = $ServiceName
$env:PORT = "$Port"
reg add "HKLM\SYSTEM\CurrentControlSet\Services\$ServiceName" /v Environment /t REG_MULTI_SZ /d "ASPNETCORE_URLS=http://localhost:$Port`0ASPNETCORE_ENVIRONMENT=Production" /f | Out-Null

Start-Service -Name $ServiceName
Start-Sleep -Seconds 3
$status = (Get-Service -Name $ServiceName).Status
Write-Host "Service ${ServiceName}: $status (http://localhost:$Port/healthz)"
Write-Host 'Jangan lupa isi secret: ConnectionStrings__Gateway, Jwt__SigningKey, Seed__AdminEmail, Seed__AdminPassword,'
Write-Host '(bila RLS aktif) ConnectionStrings__GatewayPlatform, dan jalankan sql\tenant-security.sql.'
