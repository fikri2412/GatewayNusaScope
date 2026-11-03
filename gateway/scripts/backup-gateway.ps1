<#
.SYNOPSIS
  Backup AI Gateway (GB-06): database COPY_ONLY + CHECKSUM + RESTORE VERIFYONLY, zip binari, dan manifest.

.DESCRIPTION
  Langkah (semua memakai autentikasi Windows; script TIDAK menerima atau menulis password/secret):
    1. BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM (tidak mengganggu rantai backup lain)
    2. RESTORE VERIFYONLY ... WITH CHECKSUM terhadap berkas yang baru dibuat
    3. (opsional, -RestoreTest) RESTORE ke database sementara <nama>_verify_<waktu>, hitung tabel, lalu DROP
    4. (opsional, -AppDir) zip folder binari/wwwroot. Berkas rahasia dikecualikan dan TIDAK ikut zip:
       appsettings.Production.json, *.pfx, *.pem, *.key, *.p12
    5. manifest (<dasar>.manifest.txt): waktu, nama database/server, ukuran, SHA-256, dan pengingat pemulihan

  PERINGATAN pemulihan (juga ditulis di manifest): key provider tersimpan terenkripsi lewat Data Protection, dan kunci
  Data Protection ada di database yang dienkripsi DPAPI mesin (Windows) atau sertifikat (Security:KeyCertificateThumbprint).
  Memulihkan database di mesin lain TANPA sertifikat/kunci DPAPI yang sama membuat semua key provider tidak terbaca.
  Backup sertifikat itu SENDIRI dan simpan terpisah dari backup database. Connection string, Jwt:SigningKey, dan secret
  lain ada di user-secrets/variabel lingkungan/appsettings.Production.json dan TIDAK tercakup oleh backup ini.

.PARAMETER Server        Instance SQL Server (bawaan (localdb)\MSSQLLocalDB).
.PARAMETER Database      Nama database gateway (bawaan AiGateway).
.PARAMETER BackupDir     Folder keluaran (bawaan %LOCALAPPDATA%\NusaScope\backups); dibuat bila belum ada.
.PARAMETER Label         Penanda pada nama berkas, mis. before-GB02 (huruf/angka/-/_ saja).
.PARAMETER AppDir        Folder binari/wwwroot yang dizip (opsional).
.PARAMETER RestoreTest   Pulihkan ke database sementara dan periksa, lalu hapus.
.PARAMETER TrustServerCertificate  Teruskan -C ke sqlcmd (instance jarak jauh dengan sertifikat yang tidak dipercaya; tidak perlu untuk LocalDB).

.EXAMPLE
  .\gateway\scripts\backup-gateway.ps1 -Label before-GB02 -AppDir C:\AiGateway -RestoreTest
#>
[CmdletBinding()]
param(
    [string]$Server = '(localdb)\MSSQLLocalDB',
    [string]$Database = 'AiGateway',
    [string]$BackupDir = (Join-Path $env:LOCALAPPDATA 'NusaScope\backups'),
    [string]$Label = 'manual',
    [string]$AppDir,
    [switch]$RestoreTest,
    [switch]$TrustServerCertificate
)

$ErrorActionPreference = 'Stop'

if ($Database -notmatch '^[A-Za-z0-9_\-]{1,100}$') { throw 'Nama database hanya boleh huruf, angka, - dan _.' }
if ($Label -notmatch '^[A-Za-z0-9_\-]{1,40}$') { throw 'Label hanya boleh huruf, angka, - dan _ (maks 40).' }
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw 'sqlcmd tidak ditemukan di PATH.' }
if ($AppDir -and -not (Test-Path -LiteralPath $AppDir)) { throw "AppDir tidak ditemukan: $AppDir" }

# Jalankan T-SQL lewat sqlcmd (autentikasi Windows, berhenti pada galat); mengembalikan baris keluaran.
function Invoke-Sql([string]$Query, [switch]$Raw) {
    $sqlArgs = @('-S', $Server, '-E', '-b', '-Q', $Query)
    if ($TrustServerCertificate) { $sqlArgs += '-C' }
    if ($Raw) { $sqlArgs += @('-h', '-1', '-W', '-s', '|') }
    $output = & sqlcmd @sqlArgs
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd gagal (exit $LASTEXITCODE): $($output -join ' ')" }
    return $output
}

function Escape-SqlString([string]$Value) { return $Value.Replace("'", "''") }

New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$baseName = "$Database-$Label-$stamp"
$bak = Join-Path $BackupDir "$baseName.bak"
$zip = Join-Path $BackupDir "$baseName-binaries.zip"
$manifest = Join-Path $BackupDir "$baseName.manifest.txt"
if (Test-Path -LiteralPath $bak) { throw "Berkas backup sudah ada: $bak" }

Write-Host "[1/4] BACKUP DATABASE [$Database] COPY_ONLY, CHECKSUM -> $bak"
Invoke-Sql "BACKUP DATABASE [$Database] TO DISK = N'$(Escape-SqlString $bak)' WITH COPY_ONLY, CHECKSUM, INIT, STATS = 10;" | Out-Host

Write-Host '[2/4] RESTORE VERIFYONLY ... WITH CHECKSUM'
Invoke-Sql "RESTORE VERIFYONLY FROM DISK = N'$(Escape-SqlString $bak)' WITH CHECKSUM;" | Out-Host

$restoreNote = 'tidak dijalankan (-RestoreTest tidak diberikan)'
if ($RestoreTest) {
    $verifyDb = "${Database}_verify_$stamp"
    Write-Host "[3/4] RESTORE ke database sementara [$verifyDb], lalu dihapus"
    $files = @(Invoke-Sql "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$(Escape-SqlString $bak)';" -Raw |
        Where-Object { $_ -and $_.Trim() } | ForEach-Object { ,($_.Split('|')) })
    if (-not $files) { throw 'RESTORE FILELISTONLY tidak mengembalikan berkas.' }
    $moves = @()
    $index = 0
    foreach ($f in $files) {
        $logical = $f[0].Trim()
        $type = $f[2].Trim()
        $ext = if ($type -eq 'L') { 'ldf' } else { 'mdf' }
        $target = Join-Path $BackupDir "$verifyDb-$index.$ext"
        $moves += "MOVE N'$(Escape-SqlString $logical)' TO N'$(Escape-SqlString $target)'"
        $index++
    }
    try {
        Invoke-Sql "RESTORE DATABASE [$verifyDb] FROM DISK = N'$(Escape-SqlString $bak)' WITH CHECKSUM, $($moves -join ', '), STATS = 10;" | Out-Host
        $tables = (Invoke-Sql "SET NOCOUNT ON; SELECT COUNT(*) FROM [$verifyDb].sys.tables;" -Raw | Where-Object { $_ -and $_.Trim() } | Select-Object -First 1).Trim()
        $migrations = (Invoke-Sql "SET NOCOUNT ON; SELECT COUNT(*) FROM [$verifyDb].dbo.__EFMigrationsHistory;" -Raw | Where-Object { $_ -and $_.Trim() } | Select-Object -First 1).Trim()
        if ([int]$tables -lt 1) { throw 'Database hasil restore tidak punya tabel.' }
        $restoreNote = "lulus: $tables tabel, $migrations migration tercatat"
        Write-Host "      restore uji: $restoreNote"
    } finally {
        Invoke-Sql "IF DB_ID(N'$(Escape-SqlString $verifyDb)') IS NOT NULL BEGIN ALTER DATABASE [$verifyDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$verifyDb]; END" | Out-Null
    }
} else {
    Write-Host '[3/4] restore uji dilewati (gunakan -RestoreTest untuk pemeriksaan restore sebenarnya)'
}

$zipNote = 'tidak dibuat (-AppDir tidak diberikan)'
if ($AppDir) {
    Write-Host "[4/4] zip binari dari $AppDir (tanpa berkas rahasia) -> $zip"
    $staging = Join-Path ([IO.Path]::GetTempPath()) "gateway-backup-$stamp"
    try {
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        # robocopy: kode keluar < 8 berarti sukses.
        & robocopy $AppDir $staging /E /NFL /NDL /NJH /NJS /NP /XF appsettings.Production.json *.pfx *.pem *.key *.p12 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy gagal (exit $LASTEXITCODE)" }
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
        $zipNote = $zip
    } finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
} else {
    Write-Host '[4/4] zip binari dilewati (gunakan -AppDir)'
}

$bakItem = Get-Item -LiteralPath $bak
$lines = @(
    "AI Gateway backup manifest",
    "dibuat      : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "server      : $Server",
    "database    : $Database",
    "label       : $Label",
    "berkas db   : $($bakItem.FullName)",
    "  ukuran    : $($bakItem.Length) byte",
    "  sha256    : $((Get-FileHash -LiteralPath $bak -Algorithm SHA256).Hash)",
    "  verifikasi: COPY_ONLY + CHECKSUM + RESTORE VERIFYONLY lulus",
    "  restore uji: $restoreNote"
)
if ($AppDir -and (Test-Path -LiteralPath $zip)) {
    $zipItem = Get-Item -LiteralPath $zip
    $lines += @("binari      : $($zipItem.FullName)", "  ukuran    : $($zipItem.Length) byte", "  sha256    : $((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)",
                "  dikecualikan: appsettings.Production.json, *.pfx, *.pem, *.key, *.p12")
} else {
    $lines += "binari      : $zipNote"
}
$lines += @(
    '',
    'PENGINGAT PEMULIHAN',
    '- Kunci Data Protection ada di database dan dienkripsi DPAPI mesin ini atau sertifikat (Security:KeyCertificateThumbprint).',
    '  Memulihkan di mesin lain tanpa sertifikat/kunci DPAPI yang sama membuat key provider tidak terbaca: backup sertifikat terpisah.',
    '- Connection string, Jwt:SigningKey, dan secret lain TIDAK ada di backup ini (user-secrets/variabel lingkungan/appsettings.Production.json).',
    '- Setelah restore: jalankan migration bila skema lebih lama dari binari, ulangi sql/tenant-security.sql bila RLS aktif, lalu cek /healthz/ready.'
)
Set-Content -LiteralPath $manifest -Value $lines -Encoding UTF8

Write-Host ''
Write-Host "Backup selesai: $($bakItem.FullName)" -ForegroundColor Green
Write-Host "Manifest      : $manifest"
Write-Host 'PERINGATAN: backup sertifikat/kunci DPAPI terpisah; secret konfigurasi tidak tercakup (lihat manifest).' -ForegroundColor Yellow
