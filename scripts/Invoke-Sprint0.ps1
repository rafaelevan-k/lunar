param([switch]$WindowSmoke)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Portable .NET 10 SDK is missing; see docs/SPIKE_RESULTS.md.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:NUGET_PACKAGES = Join-Path $repo '.tools\nuget'
$run = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$out = Join-Path $repo "docs\evidence\sprint-0\$run"
New-Item -ItemType Directory -Force $out | Out-Null

function Invoke-RecordedDotnet([string]$Name, [string[]]$Arguments) {
    $lines = & $dotnet @Arguments 2>&1
    $code = $LASTEXITCODE
    $safe = ($lines | Out-String).Replace($repo, '<repo>').Replace($env:USERPROFILE, '<profile>')
    $safe | Set-Content -LiteralPath (Join-Path $out "$Name.log") -Encoding UTF8
    [ordered]@{ Timestamp = [DateTimeOffset]::UtcNow.ToString('o'); Command = "dotnet $($Arguments -join ' ')";
        ExitCode = $code; Artifact = "$Name.log" } | ConvertTo-Json -Compress |
        Add-Content -LiteralPath (Join-Path $out 'commands.jsonl') -Encoding UTF8
    Write-Output "$Name exit=$code"
    if ($code -ne 0) { throw "Command failed; inspect $Name.log in the evidence run." }
}

Push-Location $repo
try {
    Invoke-RecordedDotnet 'sdk-info' @('--info')
    foreach ($project in @('Windows', 'Desktop')) {
        $path = "src/Lunar.$project/Lunar.$project.Spike.csproj"
        Invoke-RecordedDotnet "restore-$project" @('restore', $path)
        Invoke-RecordedDotnet "build-$project" @('build', $path, '-c', 'Release', '--no-restore')
    }
    $probeDll = Join-Path $repo 'src\Lunar.Windows\bin\Release\net10.0-windows10.0.19041.0\Lunar.Windows.Spike.dll'
    $probe = Start-Process -FilePath $dotnet -ArgumentList @(('"' + $probeDll + '"')) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $out 'native-probes.jsonl') -RedirectStandardError (Join-Path $out 'native-probes.stderr.log')
    $null = $probe.Handle # Retain the native process handle so PS 5.1 can read its exit code.
    try { Wait-Process -Id $probe.Id -Timeout 45 -ErrorAction Stop }
    catch {
        if (-not $probe.HasExited) { Stop-Process -Id $probe.Id -ErrorAction SilentlyContinue }
        throw 'Native probe did not finish within 45 seconds; inspect evidence.'
    }
    if ($probe.ExitCode -ne 0) { throw "Native probe process exit=$($probe.ExitCode)" }
    Write-Output "native-probes exit=$($probe.ExitCode); individual component status must be reviewed"
    if ($WindowSmoke) {
        $desktopDll = Join-Path $repo 'src\Lunar.Desktop\bin\Release\net10.0-windows\Lunar.Desktop.Spike.dll'
        $windowLog = Join-Path $out 'window-smoke.jsonl'
        # Interactive prototype: deliberately visible for the five-second window spike.
        $window = Start-Process -FilePath $dotnet -ArgumentList @(('"' + $desktopDll + '"'), '--smoke', ('"' + $windowLog + '"')) -PassThru
        $null = $window.Handle
        try { Wait-Process -Id $window.Id -Timeout 30 -ErrorAction Stop }
        catch {
            if (-not $window.HasExited) { Stop-Process -Id $window.Id -ErrorAction SilentlyContinue }
            throw 'Window smoke did not finish within 30 seconds; inspect evidence.'
        }
        if ($window.ExitCode -ne 0) { throw "Window smoke process exit=$($window.ExitCode)" }
        Write-Output "window-smoke exit=$($window.ExitCode); this is runtime instrumentation, not a manual visual PASS"
    }
    Write-Output "Evidence: docs/evidence/sprint-0/$run"
}
finally { Pop-Location }
