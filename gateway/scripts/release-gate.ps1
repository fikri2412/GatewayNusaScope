<#
.SYNOPSIS
  Gate rilis lokal AI Gateway (GB-06): build tanpa warning, seluruh test, drift migration, dan build/lint UI.

.DESCRIPTION
  Menjalankan semua pemeriksaan di bawah, melaporkan hasil tiap langkah, dan keluar dengan kode 1 bila ada yang gagal:
    1. build   : dotnet build -warnaserror untuk gateway (dan chatbot kecuali -SkipChatbot)
    2. migrasi : setiap migration punya Designer ber-[Migration]/[DbContext] yang cocok dengan nama berkas, lalu
                 dotnet ef migrations has-pending-model-changes untuk GatewayDbContext dan AppDbContext (chatbot)
    3. test    : SEMUA project test (ditemukan otomatis) dijalankan; jumlah test DIJUMLAHKAN dari berkas TRX lintas project.
                 Tidak ada angka minimum yang ditanam; gate gagal bila ada test gagal, ada yang dilewati (kecuali
                 -AllowSkippedTests), tidak ada satu pun test yang berjalan, atau sebuah project tidak menghasilkan TRX.
    4. web     : npm ci, lint, dan build UI gateway (gateway/web) kecuali -SkipWeb

  Script ini tidak menulis secret dan tidak menyentuh database selain yang dipakai test (LocalDB uji). Test xUnit perlu
  SQL Server LocalDB; jalankan di mesin/agent lokal (agent cloud tidak menjalankan test, lihat TESTING.md).

.PARAMETER Configuration
  Konfigurasi build (bawaan Release).
.PARAMETER SkipTests
  Lewati langkah test (mis. untuk pemeriksaan cepat). Gate hasilnya TIDAK boleh dipakai sebagai stempel rilis.
.PARAMETER SkipWeb
  Lewati build/lint UI.
.PARAMETER SkipChatbot
  Hanya gateway: tanpa build chatbot, tanpa cek migration AppDbContext, tanpa test chatbot.
.PARAMETER AllowSkippedTests
  Test yang dilewati tidak menggagalkan gate (bawaan: gagal, supaya test yang diam-diam dimatikan terlihat).
.PARAMETER ResultsDir
  Folder hasil TRX (bawaan: folder temp unik per jalan).

.EXAMPLE
  .\gateway\scripts\release-gate.ps1
.EXAMPLE
  .\gateway\scripts\release-gate.ps1 -SkipChatbot -SkipWeb
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipWeb,
    [switch]$SkipChatbot,
    [switch]$AllowSkippedTests,
    [string]$ResultsDir = (Join-Path ([IO.Path]::GetTempPath()) ("release-gate-" + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gateway = Join-Path $repo 'gateway'
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$Name, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Step = $Name; Ok = $Ok; Detail = $Detail })
    $mark = if ($Ok) { 'OK  ' } else { 'FAIL' }
    Write-Host ("[{0}] {1} - {2}" -f $mark, $Name, $Detail) -ForegroundColor $(if ($Ok) { 'Green' } else { 'Red' })
}

# Jalankan perintah native; true bila exit code 0. Keluarannya tampil di konsol (Out-Host) dan tidak ikut nilai kembalian.
function Invoke-Native([string]$Exe, [string[]]$Arguments, [string]$WorkingDirectory = $repo) {
    Push-Location $WorkingDirectory
    try {
        & $Exe @Arguments | Out-Host
        return ($LASTEXITCODE -eq 0)
    } finally {
        Pop-Location
    }
}

# Migration yang tidak punya Designer tidak terlihat oleh EF: periksa nama berkas, [Migration("...")], dan [DbContext(typeof(...))].
function Test-MigrationFiles([string]$Directory, [string]$ContextType) {
    $problems = @()
    $files = Get-ChildItem -LiteralPath $Directory -Filter '*.cs' -File
    $migrations = $files | Where-Object { $_.Name -match '^\d{14}_\w+\.cs$' -and $_.Name -notmatch '\.Designer\.cs$' }
    foreach ($m in $migrations) {
        $id = $m.BaseName
        $designer = Join-Path $Directory "$id.Designer.cs"
        if (-not (Test-Path -LiteralPath $designer)) { $problems += "$($m.Name): tidak punya $id.Designer.cs"; continue }
        $text = Get-Content -LiteralPath $designer -Raw
        if ($text -notmatch [regex]::Escape("[Migration(`"$id`")]")) { $problems += "$id.Designer.cs: atribut [Migration(`"$id`")] tidak cocok dengan nama berkas" }
        if ($text -notmatch ('\[DbContext\(typeof\(' + [regex]::Escape($ContextType) + '\)\)\]')) { $problems += "$id.Designer.cs: atribut [DbContext(typeof($ContextType))] tidak ada" }
    }
    $designers = $files | Where-Object { $_.Name -match '^(\d{14}_\w+)\.Designer\.cs$' }
    foreach ($d in $designers) {
        $id = $d.Name -replace '\.Designer\.cs$', ''
        if (-not (Test-Path -LiteralPath (Join-Path $Directory "$id.cs"))) { $problems += "$($d.Name): tidak punya berkas migration $id.cs" }
    }
    if (-not ($files | Where-Object { $_.Name -like '*ModelSnapshot.cs' })) { $problems += 'berkas *ModelSnapshot.cs tidak ditemukan' }
    return ,$problems
}

Write-Host "Release gate - repo: $repo" -ForegroundColor Cyan
Write-Host "Hasil TRX: $ResultsDir"
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

# --- prasyarat ------------------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK tidak ditemukan di PATH.' }
$hasEf = Invoke-Native 'dotnet' @('ef', '--version')
if (-not $hasEf) {
    Add-Result 'prasyarat' $false "dotnet-ef tidak terpasang. Pasang: dotnet tool install --global dotnet-ef --version 9.0.*"
}

# --- 1. build -------------------------------------------------------------------------------------
$solutions = @(Join-Path $gateway 'AiGateway.slnx')
if (-not $SkipChatbot) { $solutions += (Join-Path $repo 'OperatorAi.slnx') }
$buildOk = $true
foreach ($sln in $solutions) {
    $ok = Invoke-Native 'dotnet' @('build', $sln, '-c', $Configuration, '-warnaserror', '--nologo')
    Add-Result "build $(Split-Path $sln -Leaf)" $ok $(if ($ok) { "0 warning, 0 error (-warnaserror)" } else { 'build gagal atau ada warning (diperlakukan sebagai error)' })
    $buildOk = $buildOk -and $ok
}

# --- 2. migration ---------------------------------------------------------------------------------
$contexts = @(
    @{ Name = 'gateway'; Dir = Join-Path $gateway 'src\AiGateway.Core\Data\Migrations'; Type = 'GatewayDbContext';
       Project = Join-Path $gateway 'src\AiGateway.Core'; Startup = Join-Path $gateway 'src\AiGateway.Api' }
)
if (-not $SkipChatbot) {
    $contexts += @{ Name = 'chatbot'; Dir = Join-Path $repo 'src\OperatorAi.Shared\Data\Migrations'; Type = 'AppDbContext';
                    Project = Join-Path $repo 'src\OperatorAi.Shared'; Startup = Join-Path $repo 'src\OperatorAi.Api' }
}
foreach ($c in $contexts) {
    if (Test-Path -LiteralPath $c.Dir) {
        $problems = Test-MigrationFiles $c.Dir $c.Type
        Add-Result "migration $($c.Name): berkas" ($problems.Count -eq 0) $(if ($problems.Count -eq 0) { 'setiap migration punya Designer yang cocok' } else { $problems -join '; ' })
    } else {
        Add-Result "migration $($c.Name): berkas" $false "folder tidak ditemukan: $($c.Dir)"
    }
    if ($buildOk -and $hasEf) {
        # EF memuat host untuk mendapatkan DbContext; connection string tiruan cukup (perintah ini tidak menyambung ke database).
        $saved = $env:ConnectionStrings__Gateway
        $savedEnv = $env:ASPNETCORE_ENVIRONMENT
        if (-not $saved -and $c.Name -eq 'gateway') { $env:ConnectionStrings__Gateway = 'Server=(localdb)\MSSQLLocalDB;Database=AiGateway_ReleaseGate;Trusted_Connection=True;TrustServerCertificate=True' }
        if (-not $savedEnv) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }
        try {
            $ok = Invoke-Native 'dotnet' @('ef', 'migrations', 'has-pending-model-changes', '--project', $c.Project, '--startup-project', $c.Startup, '--configuration', $Configuration, '--no-build')
        } finally {
            $env:ConnectionStrings__Gateway = $saved
            $env:ASPNETCORE_ENVIRONMENT = $savedEnv
        }
        Add-Result "migration $($c.Name): model vs snapshot" $ok $(if ($ok) { 'tidak ada perubahan model tanpa migration' } else { 'ada perubahan model tanpa migration (atau EF gagal memuat host; lihat keluaran di atas)' })
    } else {
        Add-Result "migration $($c.Name): model vs snapshot" $false 'dilewati karena build gagal atau dotnet-ef tidak ada'
    }
}

# --- 3. test --------------------------------------------------------------------------------------
if ($SkipTests) {
    Add-Result 'test' $false 'dilewati dengan -SkipTests: hasil gate ini bukan stempel rilis'
} elseif (-not $buildOk) {
    Add-Result 'test' $false 'dilewati karena build gagal'
} else {
    $testRoots = @(Join-Path $gateway 'tests')
    if (-not $SkipChatbot) { $testRoots += (Join-Path $repo 'tests') }
    $projects = @()
    foreach ($root in $testRoots) { if (Test-Path -LiteralPath $root) { $projects += Get-ChildItem -LiteralPath $root -Filter '*.csproj' -Recurse -File } }
    if ($projects.Count -eq 0) { Add-Result 'test' $false 'tidak ada project test ditemukan' }
    $total = 0; $passed = 0; $failed = 0; $skipped = 0; $missing = @()
    foreach ($p in $projects) {
        $trx = "$($p.BaseName).trx"
        Invoke-Native 'dotnet' @('test', $p.FullName, '-c', $Configuration, '--no-build', '--nologo', '--logger', "trx;LogFileName=$trx", '--results-directory', $ResultsDir) | Out-Null
        $file = Join-Path $ResultsDir $trx
        if (-not (Test-Path -LiteralPath $file)) { $missing += $p.BaseName; continue }
        [xml]$doc = Get-Content -LiteralPath $file
        $counters = $doc.TestRun.ResultSummary.Counters
        $total += [int]$counters.total
        $passed += [int]$counters.passed
        $failed += [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
        $skipped += [int]$counters.notExecuted
        Write-Host ("  {0}: total {1}, lulus {2}, gagal {3}, dilewati {4}" -f $p.BaseName, $counters.total, $counters.passed, $counters.failed, $counters.notExecuted)
    }
    $detail = "project=$($projects.Count); total=$total; lulus=$passed; gagal=$failed; dilewati=$skipped"
    $ok = ($missing.Count -eq 0) -and ($failed -eq 0) -and ($total -gt 0) -and ($passed + $failed + $skipped -eq $total) -and ($AllowSkippedTests -or $skipped -eq 0)
    if ($missing.Count -gt 0) { $detail += "; tanpa TRX: $($missing -join ', ')" }
    if ($total -gt 0 -and ($passed + $failed + $skipped -ne $total)) { $detail += '; hitungan TRX tidak konsisten (total != lulus+gagal+dilewati)' }
    Add-Result 'test (jumlah lintas project)' $ok $detail
}

# --- 4. web ---------------------------------------------------------------------------------------
if ($SkipWeb) {
    Write-Host '[SKIP] web - dilewati dengan -SkipWeb' -ForegroundColor Yellow
} else {
    $web = Join-Path $gateway 'web'
    $ok = Invoke-Native 'npm' @('ci', '--no-audit', '--no-fund') $web
    if ($ok) { $ok = Invoke-Native 'npm' @('run', 'lint') $web }
    Add-Result 'web: npm ci + lint' $ok $(if ($ok) { 'lint bersih' } else { 'npm ci atau lint gagal' })
    $ok = Invoke-Native 'npm' @('run', 'build') $web
    Add-Result 'web: build' $ok $(if ($ok) { 'tsc + vite build lulus' } else { 'build gagal' })
}

# --- ringkasan ------------------------------------------------------------------------------------
Write-Host ''
Write-Host 'Ringkasan' -ForegroundColor Cyan
$results | ForEach-Object { Write-Host ("  {0,-5} {1}" -f $(if ($_.Ok) { 'OK' } else { 'FAIL' }), $_.Step) }
$bad = @($results | Where-Object { -not $_.Ok })
if ($bad.Count -gt 0) {
    Write-Host ("GATE GAGAL: {0} dari {1} langkah." -f $bad.Count, $results.Count) -ForegroundColor Red
    exit 1
}
Write-Host ("GATE LULUS: {0} langkah." -f $results.Count) -ForegroundColor Green
exit 0
