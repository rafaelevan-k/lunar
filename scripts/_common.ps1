# Shared helpers for build.ps1, test.ps1 and package.ps1. Dot-source it; it does nothing on its own.
# Works in Windows PowerShell 5.1 and PowerShell 7.

Set-StrictMode -Version Latest

$script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Get-RepositoryRoot { $script:RepositoryRoot }

# Picks the .NET installation: an explicit -DotnetRoot wins, then a local .tools\dotnet next to the repository
# (the developer-machine convention in this project), then whatever dotnet is already on PATH or DOTNET_ROOT.
function Initialize-DotnetEnvironment {
    param([string]$DotnetRoot)

    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    if (-not $DotnetRoot) {
        $local = Join-Path $script:RepositoryRoot '.tools\dotnet'
        if (Test-Path (Join-Path $local 'dotnet.exe')) { $DotnetRoot = $local }
    }

    if ($DotnetRoot) {
        if (-not (Test-Path (Join-Path $DotnetRoot 'dotnet.exe'))) {
            throw "No dotnet.exe in '$DotnetRoot'."
        }
        $env:DOTNET_ROOT = $DotnetRoot
        $env:PATH = "$DotnetRoot;$env:PATH"
    }

    $pinned = (Get-Content (Join-Path $script:RepositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $found = (& dotnet --version) 2>$null
    if ($LASTEXITCODE -ne 0 -or $found -ne $pinned) {
        throw "global.json pins SDK $pinned but dotnet reported '$found'. Install that SDK or pass -DotnetRoot."
    }
    Write-Host "Using .NET SDK $found"
}

# Runs dotnet with the given arguments from the repository root and throws when it exits non-zero.
function Invoke-Dotnet {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host ''
    Write-Host "==> $Description"
    Write-Host "    dotnet $($Arguments -join ' ')"
    Push-Location $script:RepositoryRoot
    try {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }
}
