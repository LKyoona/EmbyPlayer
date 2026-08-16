param(
    [string]$Command = ''
)

# EmbyRemotePlayer 一站式开发工具
#   用法: powershell -ExecutionPolicy Bypass -File dev.ps1 <build|run|pack|verify|clean>
#   不带参数运行时显示交互菜单（或直接双击 dev.cmd）。

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\EmbyRemotePlayer\EmbyRemotePlayer.csproj'
$projectDir = Split-Path -Parent $project
$binExe = Join-Path $projectDir 'bin\Release\net8.0-windows\EmbyRemotePlayer.exe'
$publishDir = Join-Path $root 'publish\win-x64'
$publishExe = Join-Path $publishDir 'EmbyRemotePlayer.exe'
$distDir = Join-Path $root 'dist'

function Get-ProjectVersion {
    $csproj = Get-Content -LiteralPath $project -Raw
    if ($csproj -match '<Version>\s*([^<]+?)\s*</Version>') { return $Matches[1] }
    return '0.0.0'
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$StepName
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$StepName failed (dotnet exit code: $LASTEXITCODE)."
    }
}

function Invoke-Build {
    Write-Host "`n== build：编译 Release ==" -ForegroundColor Cyan
    Invoke-DotNet -StepName 'Restore' -Arguments @('restore', $project)
    Invoke-DotNet -StepName 'Build' -Arguments @('build', $project, '-c', 'Release', '--no-restore')
    Write-Host "BUILD OK: $binExe" -ForegroundColor Green
}

function Invoke-Run {
    Write-Host "`n== run：运行开发版 ==" -ForegroundColor Cyan
    if (-not (Test-Path $binExe)) { Invoke-Build }
    Start-Process -FilePath $binExe -WorkingDirectory (Split-Path -Parent $binExe)
    Write-Host "RUNNING: $binExe" -ForegroundColor Green
}

function Invoke-Watch {
    Write-Host "`n== watch：保存即生效的开发模式 ==" -ForegroundColor Cyan
    Write-Host '保存 C#/XAML 文件后自动增量编译并热应用；无法热应用的改动会自动重启应用。' -ForegroundColor DarkGray
    Write-Host '按 Ctrl+C 退出（应用会一起关闭）。' -ForegroundColor DarkGray
    & dotnet watch run --project $project --restart-on-rude-edit
    if ($LASTEXITCODE -ne 0) { throw "watch failed (dotnet exit code: $LASTEXITCODE)." }
}

function Invoke-Pack {
    $version = Get-ProjectVersion
    Write-Host "`n== pack：打包 win-x64 自包含（v$version）==" -ForegroundColor Cyan

    Invoke-DotNet -StepName 'Restore' -Arguments @('restore', $project, '-r', 'win-x64')
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    Invoke-DotNet -StepName 'Publish' -Arguments @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        '-o', $publishDir
    )
    if (-not (Test-Path $publishExe)) {
        throw "Publish finished without producing $publishExe"
    }

    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    $zip = Join-Path $distDir ("EmbyRemotePlayer-v{0}-win-x64.zip" -f $version)
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal
    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host ("PACK OK: {0} ({1} MB)" -f $zip, $size) -ForegroundColor Green
    Write-Host "可直接运行: $publishExe"
}

function Invoke-Verify {
    $version = Get-ProjectVersion
    Write-Host "`n== verify：验证打包产物（v$version）==" -ForegroundColor Cyan

    if (-not (Test-Path $publishExe)) {
        Write-Host 'publish 目录不存在，先执行 pack ...' -ForegroundColor Yellow
        Invoke-Pack
    }
    else {
        $staleVersion = ((Get-Item $publishExe).VersionInfo.ProductVersion -split '\+')[0]
        if ($staleVersion -ne $version) {
            Write-Host "publish 目录是旧版本 v$staleVersion（当前 v$version），重新 pack ..." -ForegroundColor Yellow
            Invoke-Pack
        }
    }

    $script:verifyFailed = $false
    function Check($ok, $name, $detail) {
        if ($ok) { Write-Host ("  [PASS] {0}" -f $name) -ForegroundColor Green }
        else {
            Write-Host ("  [FAIL] {0} — {1}" -f $name, $detail) -ForegroundColor Red
            $script:verifyFailed = $true
        }
    }

    Write-Host '1/4 文件完整性'
    $libvlc = Join-Path $publishDir 'libvlc\win-x64\libvlc.dll'
    $libvlccore = Join-Path $publishDir 'libvlc\win-x64\libvlccore.dll'
    $fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
    Check (Test-Path $publishExe) '主程序 EmbyRemotePlayer.exe' '打包输出缺少主程序'
    Check ((Test-Path $libvlc) -and (Test-Path $libvlccore)) 'LibVLC 原生运行库' '缺少 libvlc\win-x64\libvlc(.core).dll，视频将无法播放'
    Check ($fileCount -ge 50) ('文件数量 {0}（应 >= 50）' -f $fileCount) '文件数异常，publish 目录可能不完整'

    Write-Host '2/4 版本一致性'
    # .NET SDK 会自动在 InformationalVersion 后追加 "+<git commit>"，比较时只看主版本部分。
    $exeVersion = (Get-Item $publishExe).VersionInfo.ProductVersion
    $exeVersionBase = ($exeVersion -split '\+')[0]
    Check ($exeVersionBase -eq $version) ("exe 版本 {0} = csproj 版本 {1}" -f $exeVersionBase, $version) '版本不一致，请重新 pack'

    Write-Host '3/4 分发 zip'
    $zip = Join-Path $distDir ("EmbyRemotePlayer-v{0}-win-x64.zip" -f $version)
    if (Test-Path $zip) {
        $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
        Check ($size -ge 40) ("dist zip 大小 {0} MB（应 >= 40）" -f $size) '自包含包体积异常偏小'
    }
    else {
        Write-Host '  [SKIP] dist zip 不存在（pack 时会自动生成）' -ForegroundColor Yellow
    }

    Write-Host '4/4 冒烟启动（启动 8 秒，确认不崩溃）'
    $proc = Start-Process -FilePath $publishExe -WorkingDirectory $publishDir -PassThru
    Start-Sleep -Seconds 8
    if ($proc.HasExited) {
        Check $false ("进程存活（实际退出码 {0}）" -f $proc.ExitCode) '启动即崩溃，检查 XAML/依赖'
    }
    else {
        $null = $proc.CloseMainWindow()
        if (-not $proc.WaitForExit(5000)) { Stop-Process -Id $proc.Id -Force }
        Check $true '进程存活并正常关闭' ''
    }

    if ($script:verifyFailed) { throw 'VERIFY FAILED：存在未通过的检查项' }
    Write-Host "`nVERIFY OK — 打包产物完整可用" -ForegroundColor Green
}

function Invoke-Clean {
    Write-Host "`n== clean：清理构建产物 ==" -ForegroundColor Cyan
    $targets = @(
        (Join-Path $root 'publish'),
        (Join-Path $root 'dist'),
        (Join-Path $projectDir 'bin'),
        (Join-Path $projectDir 'obj')
    )
    foreach ($path in $targets) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
            Write-Host "REMOVED $path"
        }
    }
    Write-Host 'CLEAN OK' -ForegroundColor Green
}

function Show-Menu {
    Write-Host ''
    Write-Host '======================================' -ForegroundColor Cyan
    Write-Host '       EmbyRemotePlayer 开发工具' -ForegroundColor Cyan
    Write-Host '======================================' -ForegroundColor Cyan
    Write-Host '  [1] build   编译（Release）'
    Write-Host '  [2] run     运行（开发版）'
    Write-Host '  [3] watch   实时开发（保存即生效，推荐）'
    Write-Host '  [4] pack    打包（win-x64 自包含 + dist zip）'
    Write-Host '  [5] verify  验证（完整性 / 版本 / 冒烟启动）'
    Write-Host '  [6] clean   清理构建产物'
    Write-Host '  [0] 退出'
}

function Invoke-Action {
    param([string]$Name)
    switch ($Name) {
        'build' { Invoke-Build }
        'run' { Invoke-Run }
        'watch' { Invoke-Watch }
        'pack' { Invoke-Pack }
        'verify' { Invoke-Verify }
        'clean' { Invoke-Clean }
        default { throw "未知命令: $Name（可用: build / run / watch / pack / verify / clean）" }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($Command)) {
        $quit = $false
        while (-not $quit) {
            Show-Menu
            $choice = Read-Host '请选择'
            switch ($choice) {
                '1' { Invoke-Action 'build' }
                '2' { Invoke-Action 'run' }
                '3' { Invoke-Action 'watch' }
                '4' { Invoke-Action 'pack' }
                '5' { Invoke-Action 'verify' }
                '6' { Invoke-Action 'clean' }
                '0' { $quit = $true }
                default { Write-Host '无效选择，请输入 0-6' -ForegroundColor Yellow }
            }
        }
    }
    else {
        Invoke-Action $Command.Trim().ToLowerInvariant()
    }
}
catch {
    Write-Host "`nFAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
