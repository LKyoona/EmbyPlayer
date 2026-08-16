param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\EmbyRemotePlayer\EmbyRemotePlayer.csproj'
$output = Join-Path $root 'publish\win-x64'

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

try {
    if (Test-Path $output) {
        Remove-Item $output -Recurse -Force
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null

    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

    Invoke-DotNet -StepName 'Restore' -Arguments @(
        'restore', $project,
        '-r', 'win-x64'
    )

    Invoke-DotNet -StepName 'Publish' -Arguments @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $selfContained,
        '--no-restore',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        '-o', $output
    )

    $exe = Join-Path $output 'EmbyRemotePlayer.exe'
    if (-not (Test-Path $exe)) {
        throw "Publish finished without producing $exe"
    }

    Write-Host "`nPUBLISH SUCCEEDED" -ForegroundColor Green
    Write-Host "Output: $output"
    Write-Host "Run:    $exe"
    exit 0
}
catch {
    Write-Host "`nPUBLISH FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
