@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$code = (Get-Content -Path '%~f0' -Encoding UTF8 | Select-Object -Skip 5 | Out-String); Invoke-Command -ScriptBlock ([scriptblock]::Create($code)) -ArgumentList '%*'"
exit /b %errorlevel%
param(
    [string]$Command = '',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = (Get-Location).Path
$project = Join-Path $root 'src\EmbyRemotePlayer\EmbyRemotePlayer.csproj'
$publishDir = Join-Path $root 'publish\win-x64'
$publishExe = Join-Path $publishDir 'EmbyRemotePlayer.exe'
$distDir = Join-Path $root 'dist'

function Get-ProjectVersion {
    $csproj = Get-Content -LiteralPath $project -Raw -Encoding UTF8
    if ($csproj -match '<Version>\s*([^<]+?)\s*</Version>') { return $Matches[1] }
    return '0.0.0'
}

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

function Invoke-PublishOnly {
    param([bool]$IsFrameworkDependent = $false)
    $version = Get-ProjectVersion
    $selfContained = if ($IsFrameworkDependent) { 'false' } else { 'true' }
    $modeText = if ($IsFrameworkDependent) { '框架依赖模式' } else { '独立免安装自包含模式' }
    Write-Host "`n== [PROD] 发布 win-x64 ($modeText, v$version) ==" -ForegroundColor Cyan

    Invoke-DotNet -StepName '还原依赖' -Arguments @('restore', $project, '-r', 'win-x64')

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    Invoke-DotNet -StepName '发布程序' -Arguments @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $selfContained,
        '--no-restore',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        '-o', $publishDir
    )

    if (-not (Test-Path $publishExe)) {
        throw "发布完成但未生成主程序: $publishExe"
    }

    Write-Host "`nPUBLISH SUCCESS: $publishExe" -ForegroundColor Green
    Write-Host "输出目录: $publishDir"
}

function Invoke-Pack {
    $version = Get-ProjectVersion
    Write-Host "`n== [PROD] 完整打包 win-x64 自包含分发包 (v$version) ==" -ForegroundColor Cyan

    Invoke-PublishOnly -IsFrameworkDependent $false

    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    $zip = Join-Path $distDir ("EmbyRemotePlayer-v{0}-win-x64.zip" -f $version)
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Write-Host "正在压缩分发包: $zip ..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal
    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)

    Write-Host "`nPACK SUCCESS: $zip ($size MB)" -ForegroundColor Green
    Write-Host "可直接分发给用户的 Zip 压缩包已准备完毕。" -ForegroundColor Green
}

function Invoke-Verify {
    $version = Get-ProjectVersion
    Write-Host "`n== [PROD] 自动化质量验证 (v$version) ==" -ForegroundColor Cyan

    if (-not (Test-Path $publishExe)) {
        Write-Host "publish 目录不存在，正在自动执行打包 ..." -ForegroundColor Yellow
        Invoke-Pack
    }
    else {
        $staleVersion = ((Get-Item $publishExe).VersionInfo.ProductVersion -split '\+')[0]
        if ($staleVersion -ne $version) {
            Write-Host "检测到 publish 目录为旧版本 v$staleVersion（当前最新为 v$version），正在重新打包 ..." -ForegroundColor Yellow
            Invoke-Pack
        }
    }

    $script:verifyFailed = $false
    function Check($ok, $name, $detail) {
        if ($ok) {
            Write-Host ("  [PASS] {0}" -f $name) -ForegroundColor Green
        }
        else {
            Write-Host ("  [FAIL] {0} — {1}" -f $name, $detail) -ForegroundColor Red
            $script:verifyFailed = $true
        }
    }

    Write-Host "1/4 文件完整性检查"
    $libvlc = Join-Path $publishDir 'libvlc\win-x64\libvlc.dll'
    $libvlccore = Join-Path $publishDir 'libvlc\win-x64\libvlccore.dll'
    $fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
    Check (Test-Path $publishExe) '主程序 EmbyRemotePlayer.exe 存在' '缺少主执行文件'
    Check ((Test-Path $libvlc) -and (Test-Path $libvlccore)) 'LibVLC 原生动态运行库' '缺少 libvlc 核心 dll，视频播放功能将不可用'
    Check ($fileCount -ge 50) ("发布文件数量 {0}（应 >= 50）" -f $fileCount) '文件数量不完整'

    Write-Host "2/4 版本号对齐检查"
    $exeVersion = (Get-Item $publishExe).VersionInfo.ProductVersion
    $exeVersionBase = ($exeVersion -split '\+')[0]
    Check ($exeVersionBase -eq $version) ("exe 版本 {0} = csproj 版本 {1}" -f $exeVersionBase, $version) '版本号未对齐'

    Write-Host "3/4 分发压缩包检查"
    $zip = Join-Path $distDir ("EmbyRemotePlayer-v{0}-win-x64.zip" -f $version)
    if (Test-Path $zip) {
        $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
        Check ($size -ge 40) ("dist zip 体积 {0} MB（正常应 >= 40 MB）" -f $size) '压缩包体积异常偏小'
    }
    else {
        Write-Host "  [SKIP] dist zip 尚未生成（可通过 pack 命令生成）" -ForegroundColor Yellow
    }

    Write-Host "4/4 8秒冒烟启动测试（验证运行时崩溃）"
    $proc = Start-Process -FilePath $publishExe -WorkingDirectory $publishDir -PassThru
    Start-Sleep -Seconds 8
    if ($proc.HasExited) {
        Check $false ("进程存活状态（实际退出码 {0}）" -f $proc.ExitCode) '程序启动闪退崩溃'
    }
    else {
        $null = $proc.CloseMainWindow()
        if (-not $proc.WaitForExit(5000)) { Stop-Process -Id $proc.Id -Force }
        Check $true '程序启动成功并可正常退出' ''
    }

    if ($script:verifyFailed) {
        throw 'VERIFY FAILED: 存在未通过的质检项，请检查上方红色报错'
    }
    Write-Host "`nVERIFY SUCCESS — 生产打包产物验证通过，随时可发布上线！" -ForegroundColor Green
}

function Invoke-Clean {
    Write-Host "`n== [PROD] 清理发布与分发产物 ==" -ForegroundColor Cyan
    $targets = @(
        $publishDir,
        $distDir
    )
    foreach ($path in $targets) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
            Write-Host "已删除: $path" -ForegroundColor Yellow
        }
    }
    Write-Host "CLEAN SUCCESS: 发布与分发产物已清理" -ForegroundColor Green
}

function Show-Menu {
    Write-Host ""
    Write-Host "==================================================" -ForegroundColor Cyan
    Write-Host "          EmbyRemotePlayer 生产发布 [PROD]        " -ForegroundColor Cyan
    Write-Host "==================================================" -ForegroundColor Cyan
    Write-Host "  [1] pack     完整打包（自包含 win-x64 + 压缩为 dist zip）"
    Write-Host "  [2] verify   质检验证（完整性 / 动态库 / 版本号 / 冒烟启动）"
    Write-Host "  [3] publish  仅发布到 publish 目录（不压 zip）"
    Write-Host "  [4] clean    清理发布与分发产物 (publish / dist)"
    Write-Host "  [0] 退出"
    Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
}

function Invoke-Action {
    param([string]$Name)
    switch ($Name) {
        'pack'    { Invoke-Pack }
        'verify'  { Invoke-Verify }
        'publish' { Invoke-PublishOnly -IsFrameworkDependent $FrameworkDependent }
        'clean'   { Invoke-Clean }
        default   { throw "未知命令: $Name (可用: pack / verify / publish / clean)" }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($Command)) {
        $quit = $false
        while (-not $quit) {
            Show-Menu
            $choice = Read-Host "请选择操作 (0-4)"
            switch ($choice) {
                '1' { Invoke-Action 'pack' }
                '2' { Invoke-Action 'verify' }
                '3' { Invoke-Action 'publish' }
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

