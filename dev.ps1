param(
    [string]$Command = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$project = Join-Path $root 'src\EmbyRemotePlayer\EmbyRemotePlayer.csproj'
$debugExe = Join-Path $root 'artifacts\bin\EmbyRemotePlayer\debug\EmbyRemotePlayer.exe'
$releaseExe = Join-Path $root 'artifacts\bin\EmbyRemotePlayer\release\EmbyRemotePlayer.exe'
$distDir = Join-Path $root 'dist'
$artifactsDir = Join-Path $root 'artifacts'

function Invoke-Build {
    param([string]$Configuration = 'Debug')
    Write-Host "`n== [BUILD] 增量编译 ($Configuration) ==" -ForegroundColor Cyan
    & dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "编译失败 (退出码: $LASTEXITCODE)" }
    Write-Host "[SUCCESS] 产物已输出至 artifacts/bin/EmbyRemotePlayer/$($Configuration.ToLowerInvariant())" -ForegroundColor Green
}

function Invoke-Run {
    Write-Host "`n== [RUN] 启动应用程序 ==" -ForegroundColor Cyan
    $targetExe = if (Test-Path $releaseExe) { $releaseExe } else { $debugExe }
    if (-not (Test-Path $targetExe)) {
        Invoke-Build -Configuration 'Debug'
        $targetExe = $debugExe
    }
    Write-Host "启动: $targetExe" -ForegroundColor Green
    Start-Process -FilePath $targetExe -WorkingDirectory (Split-Path -Parent $targetExe)
}

function Invoke-Watch {
    Write-Host "`n== [WATCH] 实时热重载开发模式 ==" -ForegroundColor Cyan
    Write-Host "保存 XAML 或 C# 业务代码即刻生效；按 Ctrl+C 退出。" -ForegroundColor DarkGray
    & dotnet watch --project $project run
    if ($LASTEXITCODE -ne 0) { throw "watch 退出 (退出码: $LASTEXITCODE)" }
}

function Invoke-Pack {
    Write-Host "`n== [PACK] 极速免安装打包 ==" -ForegroundColor Cyan
    $outputDir = Join-Path $distDir 'app'
    if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
    & dotnet publish $project -c Release -o $outputDir
    if ($LASTEXITCODE -ne 0) { throw "发布失败 (退出码: $LASTEXITCODE)" }
    Write-Host "`n[SUCCESS] 绿色免安装包已生成 -> dist\app\EmbyRemotePlayer.exe" -ForegroundColor Green
}

function Invoke-Clean {
    Write-Host "`n== [CLEAN] 秒级清理构建产物 ==" -ForegroundColor Cyan
    $targets = @(
        $artifactsDir,
        $distDir,
        (Join-Path $root 'publish'),
        (Join-Path $root 'src\EmbyRemotePlayer\bin'),
        (Join-Path $root 'src\EmbyRemotePlayer\obj')
    )
    foreach ($path in $targets) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
            Write-Host "已删除: $path" -ForegroundColor Yellow
        }
    }
    Write-Host "[SUCCESS] 源码与根目录已恢复 100% 纯净状态。" -ForegroundColor Green
}

function Show-Usage {
    Write-Host ""
    Write-Host "EmbyRemotePlayer 现代工程化命令行 [DEV]" -ForegroundColor Cyan
    Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "用法: dev <命令>"
    Write-Host ""
    Write-Host "命令列表:" -ForegroundColor White
    Write-Host "  watch   实时热重载开发 (修改 XAML/C# 即刻生效，推荐)" -ForegroundColor Green
    Write-Host "  run     极速启动应用程序 (自动按需编译)"
    Write-Host "  build   执行增量构建 (输出至 artifacts/)"
    Write-Host "  pack    极速发布绿色测试包 (输出至 dist/app，约3秒)" -ForegroundColor Cyan
    Write-Host "  clean   秒级清空全部构建产物 (artifacts/dist)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "快捷示例:" -ForegroundColor DarkGray
    Write-Host "  dev watch   # 边改边看效果" -ForegroundColor DarkGray
    Write-Host "  dev pack    # 极速出包" -ForegroundColor DarkGray
    Write-Host "  dev clean   # 恢复完全干净源码" -ForegroundColor DarkGray
    Write-Host ""
}

$cmd = $Command.Trim().ToLowerInvariant()
switch ($cmd) {
    'watch' { Invoke-Watch }
    'run'   { Invoke-Run }
    'build' { Invoke-Build }
    'pack'  { Invoke-Pack }
    'clean' { Invoke-Clean }
    ''      { Show-Usage }
    default {
        Write-Host "未知命令: $Command" -ForegroundColor Red
        Show-Usage
        exit 1
    }
}
