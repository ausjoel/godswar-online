param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'dist'),
    [ValidateSet('self-contained', 'runtime')]
    [string]$Mode = 'self-contained',
    [switch]$NoVerify,
    [switch]$SkipGmCopy
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Godswar.LootTool.csproj'
$exe = Join-Path $OutputPath 'Godswar.LootTool.exe'

$selfContained = if ($Mode -eq 'self-contained') { 'true' } else { 'false' }

Write-Host "发布 Godswar.LootTool（$Mode，win-x64 单文件）..."
# Compression is a self-contained-only option: passing it for a framework
# dependent publish fails the build with NETSDK1176.
$publishArgs = @(
    $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', $selfContained,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishTrimmed=false',
    '--nologo', '-v', 'q',
    '--output', $OutputPath
)
if ($Mode -eq 'self-contained') {
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
}

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }

# Carry the operator's saved connection/client settings into the new folder so
# the published EXE starts already configured.
$existing = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\loot-tool.settings.json'
$target = Join-Path $OutputPath 'loot-tool.settings.json'
if ((Test-Path $existing) -and -not (Test-Path $target)) {
    Copy-Item $existing $target
    Write-Host '已把现有连接设置复制到发布目录。'
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host "完成：$exe（$sizeMb MB）"

# The pet features ship under the operator-facing name. It is the same published
# single-file payload, so both entry points stay in lockstep; the original
# Godswar.LootTool.exe is left exactly where it was.
if (-not $SkipGmCopy) {
    $gmExe = Join-Path $OutputPath 'GM工具.exe'
    Copy-Item $exe $gmExe -Force
    Write-Host "完成：$gmExe（与上面是同一份发布物）"
}

if (-not $NoVerify) {
    Write-Host '正在自测这个 EXE（连数据库跑读写回环）...'
    & $exe --selftest
    if ($LASTEXITCODE -ne 0) {
        throw "EXE 自测失败（退出码 $LASTEXITCODE）。"
    }
}
