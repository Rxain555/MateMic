<#
  MateMic one-click packaging: publish + build a single-file installer.

  *** THIS FILE IS PURE ASCII ON PURPOSE. DO NOT ADD CHINESE TEXT TO IT. ***
  Windows PowerShell 5.1 reads a .ps1 without a UTF-8 BOM as ANSI, which corrupts
  non-ASCII characters and breaks parsing (errors land on lines that look fine).
  This project's shell tooling cannot set a BOM, so the fix is: no non-ASCII here.
  If you do need Chinese comments, save the file as UTF-8 WITH BOM and verify with:
      Format-Hex .\make-package.ps1 | Select-Object -First 1     # expect EF BB BF
  (Background: D:\DSH\知识库\经验\Windows脚本编码.md)

  LAYOUT-AGNOSTIC VERSION
  -----------------------
  This replaces 打包\make-package.ps1 after the workspace restructure. It detects both
  layouts, so it works before AND after the move:

    OLD (repo root = D:\DSH)             NEW (repo root = D:\DSH\项目库\MateMic\源码)
      D:\DSH\MateMic\                      ...\源码\MateMic\
      D:\DSH\MateMic.Windows\              ...\源码\MateMic.Windows\
      D:\DSH\打包\                          ...\源码\打包\      <- this file lives here
      D:\DSH\dist\ + D:\DSH\安装包\         ...\构建产物\dist\ + ...\发布包\

  How it tells them apart: it checks whether <parent of 打包>\MateMic\MateMic.csproj
  exists (new layout) or whether that file is two levels higher (old layout).

  Chinese path literals (打包 / 构建产物 / 发布包 / 本地工具 / 安装包) are unavoidable, but
  they are resolved from $PSScriptRoot and simple string joins, and every path used for
  I/O is passed through -LiteralPath equivalents, so no wildcard/parse trouble.

  Usage (same as before):
      .\make-package.ps1                 # portable zip only (fast)
      .\make-package.ps1 -WithSetup      # also compile the installer (needs Inno Setup 6)
      .\make-package.ps1 -SkipPublish    # repackage what is already in dist\
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$SkipPublish,
    [switch]$WithSetup
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- layout detection
$pkgDir   = $PSScriptRoot                                  # ...\打包
$repoRoot = Split-Path -Parent $pkgDir                     # ...\源码   or   D:\DSH
$isNew    = Test-Path -LiteralPath (Join-Path $repoRoot 'MateMic\MateMic.csproj')
$isOld    = (!$isNew) -and (Test-Path -LiteralPath (Join-Path $repoRoot '..\..\MateMic\MateMic.csproj'))

if ($isNew) {
    $layout      = 'new'
    $projectRoot = $repoRoot
    $parent      = Split-Path -Parent $repoRoot            # ...\MateMic  (the project folder)
    $distRoot    = Join-Path $parent '构建产物'
    $outDir      = Join-Path $parent '发布包'
    $toolsRoot   = Join-Path (Split-Path -Parent (Split-Path -Parent $parent)) '本地工具'
} elseif ($isOld) {
    $layout      = 'old'
    $projectRoot = $repoRoot
    $distRoot    = $repoRoot
    $outDir      = Join-Path $repoRoot '安装包'
    $toolsRoot   = Join-Path $repoRoot '本地工具'
} else {
    throw ("Cannot determine the repository layout. Expected MateMic\MateMic.csproj under " +
           $repoRoot + " (new layout) or under " + (Join-Path $repoRoot '..\..') + " (old layout).")
}

$project    = Join-Path $projectRoot 'MateMic\MateMic.csproj'
$dist       = Join-Path $distRoot 'dist'
$publishDir = Join-Path $dist 'MateMic'

Write-Host '============================================' -ForegroundColor Cyan
Write-Host ' MateMic packaging' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host (" Layout    : " + $layout) -ForegroundColor Gray
Write-Host (" Repo root : " + $repoRoot) -ForegroundColor Gray
Write-Host (" Output    : " + $outDir) -ForegroundColor Gray

if (!(Test-Path -LiteralPath $project)) { throw ("Project not found: " + $project) }

# Version comes from the project so file names can never drift apart.
$version = '0.0.0'
$m = Select-String -LiteralPath $project -Pattern '<Version>([^<]+)</Version>'
if ($m) { $version = $m.Matches[0].Groups[1].Value.Trim() }
Write-Host " Version   : $version"

# Warn before step 1 deletes a data\ folder that may hold real user settings.
$userData = Join-Path $publishDir 'data'
if (!$SkipPublish -and (Test-Path -LiteralPath $userData)) {
    Write-Host ''
    Write-Host '  [!] The publish target already contains a data\ folder:' -ForegroundColor Yellow
    Write-Host ("      " + $userData) -ForegroundColor Yellow
    Write-Host '      Step 1 deletes it. Copy it out first if it holds real settings,' -ForegroundColor Yellow
    Write-Host '      or use -SkipPublish to repackage what is already there.' -ForegroundColor Yellow
    Write-Host '      (User data must not live in build output: 知识库\经验\便携式数据目录.md)' -ForegroundColor Gray
}

# ---------------------------------------------------------------- 1. publish
if ($SkipPublish) {
    Write-Host '[1/5] Skipping publish (-SkipPublish)'
} else {
    Write-Host "[1/5] Publishing ($Configuration / $RuntimeIdentifier) ..." -ForegroundColor Cyan
    if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    # -r is mandatory: without a RID the ONNX Runtime package emits native libraries for
    # every platform and the payload grows from ~13.7 MB to ~207.7 MB (measured).
    dotnet publish $project -c $Configuration -r $RuntimeIdentifier --self-contained false -o $publishDir --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}

if (!(Test-Path -LiteralPath (Join-Path $publishDir 'MateMic.exe'))) {
    throw "MateMic.exe not found in $publishDir - run without -SkipPublish first."
}

# ---------------------------------------------------------------- 2/3. trim + models
# -SkipPublish means "repackage what is already in dist": it has been trimmed and already
# holds the models, so re-running these steps would only risk deleting them (that is how an
# earlier version wiped its own models folder).
if ($SkipPublish) {
    Write-Host '[2/5] Skipping trim (-SkipPublish)'
    Write-Host '[3/5] Skipping model collection (-SkipPublish)'
} else {
    Write-Host '[2/5] Trimming (debug symbols, link-time import libraries) ...' -ForegroundColor Cyan
    foreach ($pattern in @('*.pdb', 'onnxruntime.lib', 'onnxruntime_providers_shared.lib')) {
        Get-ChildItem -LiteralPath $publishDir -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }

    Write-Host '[3/5] Collecting denoise models ...' -ForegroundColor Cyan
    $modelsTarget = Join-Path $publishDir 'models'
    New-Item -ItemType Directory -Force -Path $modelsTarget | Out-Null

    # Checked in both layouts because the build output folder moved.
    # $distRoot covers the NEW layout, where the build output (构建产物\) is a SIBLING of the
    # repo root (源码\), not a child of it - looking under $projectRoot finds nothing there.
    # The $projectRoot entries are kept for the old layout and for per-project bin\ folders.
    $modelSources = @(
        (Join-Path $projectRoot 'MateMic\models'),
        (Join-Path $distRoot 'Release\bin\Release\net9.0-windows\data\models'),
        (Join-Path $distRoot 'Release\data\models'),
        (Join-Path $distRoot 'Debug\bin\Debug\net9.0-windows\data\models'),
        (Join-Path $distRoot 'Debug\data\models'),
        (Join-Path $projectRoot '构建产物\Release\bin\Release\net9.0-windows\data\models'),
        (Join-Path $projectRoot '构建产物\Release\data\models'),
        (Join-Path $projectRoot '构建产物\Debug\bin\Debug\net9.0-windows\data\models'),
        (Join-Path $projectRoot '构建产物\Debug\data\models'),
        (Join-Path $projectRoot 'MateMic\bin\Release\net9.0-windows\data\models'),
        (Join-Path $projectRoot 'MateMic\bin\Debug\net9.0-windows\data\models')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    $modelCount = 0
    foreach ($source in $modelSources) {
        Get-ChildItem -LiteralPath $source -Filter '*.onnx' -File -ErrorAction SilentlyContinue | ForEach-Object {
            $target = Join-Path $modelsTarget $_.Name
            if (!(Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $_.FullName -Destination $target; $modelCount++ }
        }
    }

    if ($modelCount -eq 0) {
        Write-Host '  WARNING: no .onnx model bundled.' -ForegroundColor Yellow
        Write-Host ("  Put models into " + (Join-Path $projectRoot 'MateMic\models') + " to ship them.") -ForegroundColor Yellow
    } else {
        Write-Host "  Bundled $modelCount model(s):" -ForegroundColor Green
        Get-ChildItem -LiteralPath $modelsTarget -Filter '*.onnx' | ForEach-Object {
            Write-Host ("    - {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
        }
    }
}

# ---------------------------------------------------------------- 4. Inno Setup
Write-Host '[4/5] Building installer ...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$setupName = "MateMic-$version-setup.exe"
$setupPath = Join-Path $outDir $setupName
if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath -Force }

# ISCC.exe: normal install locations first, then the copy kept in the workspace 本地工具\
# (not committed; ~32 MB). $toolsRoot is computed above for both layouts.
$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $toolsRoot 'Inno Setup 6\ISCC.exe'),
    (Join-Path $repoRoot 'Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (!$iscc -and (Test-Path -LiteralPath $toolsRoot)) {
    # Last resort inside the workspace tools folder only (a full disk search is too slow).
    $hit = Get-ChildItem -LiteralPath $toolsRoot -Filter 'ISCC.exe' -Recurse -Depth 3 -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if ($hit) { $iscc = $hit.FullName }
}

if (!$iscc) {
    Write-Host '  Inno Setup compiler (ISCC.exe) NOT found -> installer NOT built.' -ForegroundColor Yellow
    Write-Host ("  Looked in Program Files, LOCALAPPDATA and " + $toolsRoot) -ForegroundColor Yellow
    Write-Host '  The portable zip below is still produced.' -ForegroundColor Yellow
} else {
    Write-Host "  Using: $iscc"

    # Inno Setup 6 does not ship Simplified Chinese; the project keeps a copy next to this
    # script (打包\ChineseSimplified.isl). Fall back to English when neither copy exists.
    $isl = @(
        (Join-Path $pkgDir 'ChineseSimplified.isl'),
        (Join-Path (Split-Path -Parent $iscc) 'Languages\ChineseSimplified.isl')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    $iss = Join-Path $pkgDir 'MateMic.iss'
    $isccArgs = @("/DMySourceDir=$publishDir", "/DMyOutputDir=$outDir", "/DMyAppVersion=$version")
    if ($isl) {
        Write-Host '  Chinese language file found - wizard will offer Chinese.' -ForegroundColor Green
        $isccArgs += '/DHaveChinese=1'
        $isccArgs += "/DChineseIslPath=$isl"
    } else {
        Write-Host '  NOTE: ChineseSimplified.isl not found; wizard falls back to English.' -ForegroundColor Yellow
    }

    # -WithSetup is accepted for backward compatibility with existing callers; the
    # installer is always attempted when a compiler is available (that was the old
    # behaviour too, the switch was only ever documentation).
    & $iscc @isccArgs $iss
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  Inno Setup compilation FAILED (see messages above).' -ForegroundColor Red
    } elseif (Test-Path -LiteralPath $setupPath) {
        Write-Host ("  OK: {0} ({1:N1} MB)" -f $setupName, ((Get-Item -LiteralPath $setupPath).Length / 1MB)) -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- 5. portable zip
Write-Host '[5/5] Building portable zip ...' -ForegroundColor Cyan
$zipName = "MateMic-$version-portable.zip"
$zipPath = Join-Path $outDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host ("  OK: {0} ({1:N1} MB)" -f $zipName, ((Get-Item -LiteralPath $zipPath).Length / 1MB)) -ForegroundColor Green

Write-Host ''
Write-Host '============================================' -ForegroundColor Green
Write-Host (" Done. Output: {0}" -f $outDir) -ForegroundColor Green
Write-Host '============================================' -ForegroundColor Green
Get-ChildItem -LiteralPath $outDir | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
