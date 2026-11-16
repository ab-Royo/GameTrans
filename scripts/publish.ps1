# 构建并发布 GameTrans（win-x64 / 自包含单文件）
# 用法： powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# Windows PowerShell 5.1 下 [CmdletBinding()] 的脚本在参数绑定阶段 $PSScriptRoot 还是空值，
# 因此输出目录在这里（绑定之后）再算默认值。
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $repo 'dist' }
$proj = Join-Path $repo 'src\GameTrans\GameTrans.csproj'

Write-Host '== 环境检查 ==' -ForegroundColor Cyan
$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0) { throw '未找到 .NET SDK，请安装 .NET 9 SDK。' }
$sdks | Write-Host
Write-Host "项目：$proj"
Write-Host "输出：$Output"

Write-Host "`n== 构建 ==" -ForegroundColor Cyan
dotnet build $proj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }

Write-Host "`n== 发布 ==" -ForegroundColor Cyan
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
dotnet publish $proj -c $Configuration -r win-x64 --self-contained true --nologo -o $Output
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }

$exe = Join-Path $Output 'GameTrans.exe'
if (-not (Test-Path $exe)) { throw "未生成 $exe" }
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host "`n== 自检 ==" -ForegroundColor Cyan
$testDir = Join-Path ([System.IO.Path]::GetTempPath()) ('gametrans-selftest-' + (Get-Date -Format 'HHmmss'))
$proc = Start-Process -FilePath $exe -ArgumentList '--selftest', $testDir -PassThru -Wait
Get-Content (Join-Path $testDir 'report.txt') -Encoding UTF8 | ForEach-Object { Write-Host $_ }

Write-Host "`n完成：$exe（$sizeMb MB，自检退出码 $($proc.ExitCode)）" -ForegroundColor Green
if ($proc.ExitCode -ne 0) { exit 1 }
