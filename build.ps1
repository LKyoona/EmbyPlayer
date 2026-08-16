$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\EmbyRemotePlayer\EmbyRemotePlayer.csproj'

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
    Invoke-DotNet -StepName 'SDK check' -Arguments @('--info')
    Invoke-DotNet -StepName 'Restore' -Arguments @('restore', $project)
    Invoke-DotNet -StepName 'Build' -Arguments @('build', $project, '-c', 'Release', '--no-restore')
    Write-Host "`nBUILD SUCCEEDED" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "`nBUILD FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
