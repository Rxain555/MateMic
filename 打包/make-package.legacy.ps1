<#
  MateMic one-click packaging: publish + build a single-file installer.

  Output:
      <repoRoot>\安装包\                <- self-extracting installer, ready to double-click
          MateMic-<version>-setup.exe
          MateMic-<version>-portable.zip
      <repoRoot>\dist\MateMic\          <- raw published files

  Uses IExpress (built into Windows) so no extra tooling is required.
  If Inno Setup 6 (ISCC.exe) is installed, it is preferred instead: it produces a proper
  Chinese wizard. Simply install Inno Setup and re-run this script.

  NOTE: this file is saved WITH a UTF-8 BOM on purpose. Windows PowerShell 5.1 reads a
  .ps1 without a BOM as ANSI, which corrupts the Chinese text below and breaks parsing.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $repoRoot 'MateMic\MateMic.csproj'
$dist      = Join-Path $repoRoot 'dist'
$publishDir = Join-Path $dist 'MateMic'
# The user asked for the finished installer to live in its own folder in the workspace.
$outDir    = Join-Path $repoRoot '安装包'

Write-Host '============================================' -ForegroundColor Cyan
Write-Host ' MateMic packaging' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan

# Version is read from the project so names can never drift apart.
$version = '0.0.0'
$m = Select-String -Path $project -Pattern '<Version>([^<]+)</Version>'
if ($m) { $version = $m.Matches[0].Groups[1].Value.Trim() }
Write-Host "Version: $version"

# ---------------------------------------------------------------- 1. publish
if ($SkipPublish) {
    Write-Host '[1/5] Skipping publish (-SkipPublish)'
} else {
    Write-Host "[1/5] Publishing ($Configuration / $RuntimeIdentifier) ..." -ForegroundColor Cyan
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    dotnet publish $project -c $Configuration -r $RuntimeIdentifier `
        --self-contained false -o $publishDir --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}

if (!(Test-Path (Join-Path $publishDir 'MateMic.exe'))) {
    throw "MateMic.exe not found in $publishDir - run without -SkipPublish first."
}

# ---------------------------------------------------------------- 2/3. trim + models
# -SkipPublish means "repackage what is already in dist": dist has been trimmed and has the
# models copied in already, so re-running these steps would only risk deleting them
# (that is exactly how an earlier version wiped its own models folder).
if ($SkipPublish) {
    Write-Host '[2/5] Skipping trim (-SkipPublish)'
    Write-Host '[3/5] Skipping model collection (-SkipPublish)'
} else {
    Write-Host '[2/5] Trimming (debug symbols, link-time import libraries) ...' -ForegroundColor Cyan
    foreach ($pattern in @('*.pdb', 'onnxruntime.lib', 'onnxruntime_providers_shared.lib')) {
        Get-ChildItem -Path $publishDir -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item $_.FullName -Force }
    }

    Write-Host '[3/5] Collecting denoise models ...' -ForegroundColor Cyan
    $modelsTarget = Join-Path $publishDir 'models'
    New-Item -ItemType Directory -Force -Path $modelsTarget | Out-Null

    $modelSources = @(
        (Join-Path $repoRoot 'MateMic\models'),
        (Join-Path $repoRoot 'MateMic\bin\Release\net9.0-windows\data\models'),
        (Join-Path $repoRoot 'MateMic\bin\Debug\net9.0-windows\data\models')
    ) | Where-Object { Test-Path $_ }

    $modelCount = 0
    foreach ($source in $modelSources) {
        Get-ChildItem -Path $source -Filter '*.onnx' -File -ErrorAction SilentlyContinue | ForEach-Object {
            $target = Join-Path $modelsTarget $_.Name
            if (!(Test-Path $target)) { Copy-Item $_.FullName $target; $modelCount++ }
        }
    }

    if ($modelCount -eq 0) {
        Write-Host '  WARNING: no .onnx model bundled.' -ForegroundColor Yellow
        Write-Host '  Put models into MateMic\models\ to ship them.' -ForegroundColor Yellow
    } else {
        Write-Host "  Bundled $modelCount model(s):" -ForegroundColor Green
        Get-ChildItem $modelsTarget -Filter '*.onnx' | ForEach-Object {
            Write-Host ("    - {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
        }
    }
}

# ---------------------------------------------------------------- 4. Inno Setup
# (Kept as step 4 of 5; there is no separate "assemble payload" step any more - Inno Setup
#  reads dist\MateMic directly.)
Write-Host '[4/5] Building installer ...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$setupName = "MateMic-$version-setup.exe"
$setupPath = Join-Path $outDir $setupName
if (Test-Path $setupPath) { Remove-Item $setupPath -Force }

# ISCC.exe is looked up in the usual install locations, plus the copies that live inside
# this repository (Inno Setup is kept under 本地工具\ so it never gets committed).
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    (Join-Path $repoRoot '本地工具\Inno Setup 6\ISCC.exe'),
    (Join-Path $repoRoot 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (!$iscc) {
    # Last resort: search a few levels deep, but skip the big directories so this stays fast.
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $repoRoot '本地工具'))) {
        if (!$root -or !(Test-Path $root)) { continue }
        $hit = Get-ChildItem $root -Filter 'ISCC.exe' -Recurse -Depth 3 -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) { $iscc = $hit.FullName; break }
    }
}

if (!$iscc) {
    Write-Host '  Inno Setup compiler (ISCC.exe) not found - installer NOT built.' -ForegroundColor Yellow
    Write-Host '  Install Inno Setup 6 and run again; the portable zip below still works.' -ForegroundColor Yellow
} else {
    Write-Host "  Using: $iscc"

    # Simplified Chinese does NOT ship with Inno Setup 6: it lives on
    # https://jrsoftware.org/files/istrans/. Include it when present.
    $isl = @(
        (Join-Path $PSScriptRoot 'ChineseSimplified.isl'),
        (Join-Path (Split-Path $iscc -Parent) 'Languages\ChineseSimplified.isl')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    $iss = Join-Path $PSScriptRoot 'MateMic.iss'
    $isccArgs = @("/DMySourceDir=$publishDir", "/DMyOutputDir=$outDir", "/DMyAppVersion=$version")
    if ($isl) {
        Write-Host '  Chinese language file found - wizard will offer Chinese.' -ForegroundColor Green
        $isccArgs += '/DHaveChinese=1'
        $isccArgs += "/DChineseIslPath=$isl"
    } else {
        Write-Host '  NOTE: ChineseSimplified.isl not found; wizard falls back to English.' -ForegroundColor Yellow
        Write-Host '  For a Chinese wizard, download ChineseSimplified.isl from' -ForegroundColor Yellow
        Write-Host '  https://jrsoftware.org/files/istrans/ and put it in installer\ or' -ForegroundColor Yellow
        Write-Host '  <Inno Setup dir>\Languages\, then run this script again.' -ForegroundColor Yellow
    }

    & $iscc @isccArgs $iss
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  Inno Setup compilation FAILED (see messages above).' -ForegroundColor Red
    } elseif (Test-Path $setupPath) {
        Write-Host ("  OK: {0} ({1:N1} MB)" -f $setupName, ((Get-Item $setupPath).Length / 1MB)) -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- 6. portable zip
Write-Host '[5/5] Building portable zip ...' -ForegroundColor Cyan
$zipName = "MateMic-$version-portable.zip"
$zipPath = Join-Path $outDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host ("  OK: {0} ({1:N1} MB)" -f $zipName, ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green

Write-Host ''
Write-Host '============================================' -ForegroundColor Green
Write-Host (" Done. Output: {0}" -f $outDir) -ForegroundColor Green
Write-Host '============================================' -ForegroundColor Green
Get-ChildItem $outDir | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
