@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$code = (Get-Content -Path '%~f0' -Encoding UTF8 | Select-Object -Skip 5 | Out-String); Invoke-Command -ScriptBlock ([scriptblock]::Create($code)) -ArgumentList '%*'"
exit /b %errorlevel%
param(
    [string]$Command = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = (Get-Location).Path
$project = Join-Path $root 'src\EmbyRemotePlayer\EmbyRemotePlayer.csproj'
$projectDir = Split-Path -Parent $project
$binExe = Join-Path $projectDir 'bin\Release
et8.0-windows\EmbyRemotePlayer.exe'

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$StepName
    )
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$StepName 失败 (dotnet 退出码: $LASTEXITCODE)。"
    }
}

function Invoke-Build {
    Write-Host "`n== [DEV] 编译 Release ==" -ForegroundColor Cyan
    Invoke-DotNet -StepName 'SDK 检查' -Arguments @('--info')
    Invoke-DotNet -StepName '还原依赖' -Arguments @('restore', $project)
    Invoke-DotNet -StepName '编译项目' -Arguments @('build', $project, '-c', 'Release', '--no-restore')
    Write-Host "BUILD SUCCESS: $binExe" -ForegroundColor Green
}

function Invoke-Run {
    Write-Host "`n== [DEV] 运行开发版 ==" -ForegroundColor Cyan
    if (-not (Test-Path $binExe)) {
        Invoke-Build
    }
    Write-Host "启动应用程序: $binExe" -ForegroundColor Green
    Start-Process -FilePath $binExe -WorkingDirectory (Split-Path -Parent $binExe)
}

function Invoke-Watch {
    Write-Host "`n== [DEV] 实时开发模式（热重载） ==" -ForegroundColor Cyan
    Write-Host "保存 C#/XAML 文件后自动增量编译并热生效；无法热重载的改动会自动重启。" -ForegroundColor DarkGray
    Write-Host "按 Ctrl+C 可退出开发模式。" -ForegroundColor DarkGray
    & dotnet watch run --project $project --restart-on-rude-edit
    if ($LASTEXITCODE -ne 0) { throw "watch 失败 (dotnet 退出码: $LASTEXITCODE)。" }
}

function Invoke-Clean {
    Write-Host "`n== [DEV] 清理本地构建缓存 ==" -ForegroundColor Cyan
    $targets = @(
        (Join-Path $projectDir 'bin'),
        (Join-Path $projectDir 'obj'),
        (Join-Path $root 'publish')
    )
    foreach ($path in $targets) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
            Write-Host "已删除: $path" -ForegroundColor Yellow
        }
    }
    Write-Host "CLEAN SUCCESS: 构建缓存已全部清空" -ForegroundColor Green
}

function Show-Menu {
    Write-Host ""
    Write-Host "==================================================" -ForegroundColor Cyan
    Write-Host "          EmbyRemotePlayer 开发控制台 [DEV]       " -ForegroundColor Cyan
    Write-Host "==================================================" -ForegroundColor Cyan
    Write-Host "  [1] watch   实时开发（修改代码自动热重载，推荐）"
    Write-Host "  [2] run     运行开发版（若未编译则自动编译）"
    Write-Host "  [3] build   编译 Release 版本"
    Write-Host "  [4] clean   清理本地编译缓存 (bin/obj/publish)"
    Write-Host "  [0] 退出"
    Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
}

function Invoke-Action {
    param([string]$Name)
    switch ($Name) {
        'watch' { Invoke-Watch }
        'run'   { Invoke-Run }
        'build' { Invoke-Build }
        'clean' { Invoke-Clean }
        default { throw "未知命令: $Name (可用: watch / run / build / clean)" }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($Command)) {
        $quit = $false
        while (-not $quit) {
            Show-Menu
            $choice = Read-Host "请选择操作 (0-4)"
            switch ($choice) {
                '1' { Invoke-Action 'watch' }
                '2' { Invoke-Action 'run' }
                '3' { Invoke-Action 'build' }
                '4' { Invoke-Action 'clean' }
                '0' { $quit = $true }
                default { Write-Host "无效选项，请输入 0-4" -ForegroundColor Yellow }
            }
        }
    }
    else {
        Invoke-Action $Command.Trim().ToLowerInvariant()
    }
}
catch {
    Write-Host "`n[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

