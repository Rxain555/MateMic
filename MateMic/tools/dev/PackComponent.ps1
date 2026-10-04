# 把一个模型文件打成 MateMic 组件（清单 + 分段），供上传到有单文件大小限制的网盘。
#
# 用法：
#   .\PackComponent.ps1 -Source <模型文件> -Id <组件标识> -Name <显示名> [-Version 1.0.0]
#                       [-Kind engine|model] [-Engine rvc] [-MaxPartMB 90] [-OutDir <目录>]
#
# 产物：<OutDir>\<Id>.part1 ... partN 与 manifest.json
#   - MaxPartMB 是**单段上限**，脚本会按"段数 = ceil(总大小 / 上限)"均分，保证每段都不超上限；
#   - 清单里同时记录每段与拼接后整文件的 SHA-256，安装器逐段与整体双重校验。
#
# 注意：本脚本不关心许可，只负责打包。分发的合法性由调用者负责。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Id,
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Version = '1.0.0',
    [ValidateSet('engine', 'model')][string]$Kind = 'engine',
    [string]$Engine = 'rvc',
    [int]$MaxPartMB = 90,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Source)) { throw "找不到源文件：$Source" }
$sourceItem = Get-Item -LiteralPath $Source
$source = $sourceItem.FullName

if ($sourceItem.PSIsContainer) {
    # 目录：先打成一个"不压缩"的 zip 载荷（ONNX 压缩不了多少，store 更快也更省心）
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipName = "$Id.zip"
    $zipPath = Join-Path $OutDir $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $source, $zipPath, [System.IO.Compression.CompressionLevel]::NoCompression, $false)
    Write-Host "目录已打成 zip 载荷：$zipName（$([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)) MB）"
    $source = $zipPath
    $size = [int64](Get-Item -LiteralPath $zipPath).Length
}
else {
    $size = [int64]$sourceItem.Length      # 注意：不要用 $source.Length —— 那是路径字符串的长度
}

$fileName = Split-Path -Leaf $source

if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $source) ($Id + '-component') }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 均分：段数按上限算，再平均分配，避免最后一段只剩几字节
$maxBytes = [int64]$MaxPartMB * 1MB
$partCount = [int][Math]::Ceiling($size / [double]$maxBytes)
if ($partCount -lt 1) { $partCount = 1 }
$partBytes = [int64][Math]::Ceiling($size / [double]$partCount)

Write-Host "打包 $fileName（$([math]::Round($size / 1MB, 1)) MB）→ $partCount 段，每段约 $([math]::Round($partBytes / 1MB, 1)) MB"

$sha = [System.Security.Cryptography.SHA256]::Create()
$wholeHash = $null
$parts = @()
$input = [System.IO.File]::OpenRead($source)
try {
    for ($i = 1; $i -le $partCount; $i++) {
        $partName = "$Id.part$i"
        $partPath = Join-Path $OutDir $partName
        $remaining = $size - ($input.Position)
        $take = [Math]::Min($partBytes, $remaining)

        $partSha = [System.Security.Cryptography.SHA256]::Create()
        $output = [System.IO.File]::Create($partPath)
        try {
            $buffer = New-Object byte[] (1MB)
            $left = $take
            while ($left -gt 0) {
                $read = $input.Read($buffer, 0, [int][Math]::Min($buffer.Length, $left))
                if ($read -le 0) { break }
                $output.Write($buffer, 0, $read)
                [void]$partSha.TransformBlock($buffer, 0, $read, $null, 0)
                $left -= $read
            }
            [void]$partSha.TransformFinalBlock((New-Object byte[] 0), 0, 0)
            # 必须在 Dispose 之前取出哈希：Dispose 之后 .Hash 会变成 $null
            $partHashHex = ([BitConverter]::ToString($partSha.Hash) -replace '-', '').ToLowerInvariant()
        }
        finally {
            $output.Close()
            $partSha.Dispose()
        }

        $written = (Get-Item -LiteralPath $partPath).Length
        $parts += [ordered]@{
            name   = $partName
            size   = $written
            sha256 = $partHashHex
        }
        Write-Host ("  段 {0}：{1}（{2} MB）" -f $i, $partName, [math]::Round($written / 1MB, 2))
    }
}
finally {
    $input.Close()
}

# 整文件哈希（直接对源文件算，安装器拼完会再算一次比对）
$wholeHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    id              = $Id
    name            = $Name
    version         = $Version
    kind            = $Kind
    engine          = $(if ($Kind -eq 'engine') { $Engine } else { $null })
    targetDirectory = ''
    fileName        = $fileName
    size            = $size
    sha256          = $wholeHash
    parts           = $parts
}

$json = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $OutDir 'manifest.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "组件已生成：$OutDir"
Write-Host "  整文件 SHA-256：$wholeHash"
Write-Host "  清单：manifest.json"
