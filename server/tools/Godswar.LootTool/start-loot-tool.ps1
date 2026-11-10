param(
    [string]$ConnectionString,
    [string]$ClientRoot = 'D:\Godswar Origin',
    [switch]$SelfTest,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Godswar.LootTool.csproj'
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Godswar.LootTool.exe'

if (-not $NoBuild) {
    Write-Host '构建 Godswar.LootTool ...'
    dotnet build $project --configuration Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
}

if (-not (Test-Path $exe)) { throw "找不到可执行文件：$exe" }

# Start-Process does not quote array elements, so paths with spaces have to be
# quoted explicitly here; otherwise "D:\Godswar Origin" arrives as two arguments.
$argumentLine = @()
if ($ConnectionString) { $argumentLine += "--connection-string `"$ConnectionString`"" }
if ($ClientRoot)       { $argumentLine += "--client-root `"$ClientRoot`"" }
if ($SelfTest)         { $argumentLine += '--selftest' }
$argumentLine = $argumentLine -join ' '

if ($SelfTest) {
    & $exe @arguments
    exit $LASTEXITCODE
}

Start-Process -FilePath $exe -ArgumentList $argumentLine | Out-Null
Write-Host 'Godswar 掉落表编辑工具已启动。'
