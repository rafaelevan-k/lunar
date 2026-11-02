param([switch]$WordReference)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Verified portable SDK missing' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:NUGET_PACKAGES = Join-Path $repo '.tools\nuget'
$run = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$out = Join-Path $repo "docs\evidence\sprint-0\continuation-$run"
$work = Join-Path $repo "artifacts\sprint-0-converter\continuation-$run"
New-Item -ItemType Directory -Path $out | Out-Null

function Record-Command([string]$Name, [string]$Command, $Code) {
    [ordered]@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');Command=$Command;ExitCode=$Code;Artifact=$Name} |
        ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $out 'commands.jsonl') -Encoding UTF8
}
function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    $lines = & $dotnet @Arguments 2>&1
    $code = $LASTEXITCODE
    ($lines | Out-String).Replace($repo,'<repo>').Replace($env:USERPROFILE,'<profile>') |
        Set-Content -LiteralPath (Join-Path $out $Name) -Encoding UTF8
    Record-Command $Name "dotnet $($Arguments -join ' ')" $code
    Write-Output "$Name exit=$code"
    if ($code -ne 0) { throw "Command failed: $Name" }
}

Push-Location $repo
try {
    Invoke-Dotnet 'sdk-info.log' @('--info')
    $project = 'src/Lunar.Conversion/Spike/Lunar.Conversion.Spike.csproj'
    Invoke-Dotnet 'restore-locked.log' @('restore',$project,'--locked-mode')
    Invoke-Dotnet 'build.log' @('build',$project,'-c','Release','--no-restore')
    $dll = Join-Path $repo 'src\Lunar.Conversion\Spike\bin\Release\net10.0-windows10.0.19041.0\Lunar.Conversion.Spike.dll'
    $process = Start-Process -FilePath $dotnet -WindowStyle Hidden -PassThru `
        -ArgumentList @(('"'+$dll+'"'),'generate',('"'+$work+'"')) `
        -RedirectStandardOutput (Join-Path $out 'generate.jsonl') -RedirectStandardError (Join-Path $out 'generate.stderr.log')
    $null = $process.Handle
    if (-not $process.WaitForExit(60000)) {
        Stop-Process -Id $process.Id
        Record-Command 'generate.jsonl' 'dotnet <converter-spike.dll> generate <new-synthetic-workspace>' 'TIMEOUT'
        throw 'Synthetic generator exceeded 60 seconds'
    }
    Record-Command 'generate.jsonl' 'dotnet <converter-spike.dll> generate <new-synthetic-workspace>' $process.ExitCode
    if ($process.ExitCode -ne 0) { throw "Synthetic generator exit=$($process.ExitCode)" }
    Copy-Item -LiteralPath (Join-Path $work 'manifest.json') -Destination (Join-Path $out 'fixture-output-manifest.json')
    if ($WordReference) {
        if (@(Get-Process WINWORD -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Close Word before requesting an isolated reference experiment' }
        $beforeHash = (Get-FileHash -LiteralPath (Join-Path $work 'fixture-layout.docx') -Algorithm SHA256).Hash
        $referenceScript = Join-Path $PSScriptRoot 'Export-WordDiscoveryReference.ps1'
        $reference = Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$referenceScript+'"'),'-Workspace',('"'+$work+'"')) `
            -RedirectStandardOutput (Join-Path $out 'word-reference.jsonl') -RedirectStandardError (Join-Path $out 'word-reference.stderr.log')
        $null = $reference.Handle
        $finished = $reference.WaitForExit(60000)
        $referenceCode = if ($finished) { $reference.ExitCode } else { 'TIMEOUT' }
        if (-not $finished) { Stop-Process -Id $reference.Id }
        # Never terminate a user's Word session: require the child's proven PID and exact start time.
        $ownedFile = Join-Path $work 'word-owned-process.json'
        $cleanup = 'No owned process record'
        if (Test-Path -LiteralPath $ownedFile) {
            $owned = Get-Content -LiteralPath $ownedFile -Raw | ConvertFrom-Json
            $ownedWord = Get-Process -Id $owned.ProcessId -ErrorAction SilentlyContinue
            if ($null -eq $ownedWord) { $cleanup = 'Owned process already exited' }
            elseif ($ownedWord.ProcessName -eq 'WINWORD' -and
                $ownedWord.StartTime.ToUniversalTime().ToString('o') -eq $owned.StartTimeUtc) {
                Stop-Process -Id $ownedWord.Id
                $ownedWord.WaitForExit(5000) | Out-Null
                $cleanup = 'Stopped exact owned process'
            } else { throw 'Word ownership no longer matches; no process terminated' }
        }
        $unchanged = (Get-FileHash -LiteralPath (Join-Path $work 'fixture-layout.docx') -Algorithm SHA256).Hash -eq $beforeHash
        [ordered]@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');ExitCode=$referenceCode;TimeoutSeconds=60;
            Cleanup=$cleanup;OriginalDocxUnchanged=$unchanged;ReferencePdfExists=(Test-Path -LiteralPath (Join-Path $work 'word-reference.pdf'))} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'word-reference-result.json') -Encoding UTF8
        Record-Command 'word-reference.jsonl' 'powershell -File scripts/Export-WordDiscoveryReference.ps1 -Workspace <new-synthetic-workspace> (60-second owned-process guard)' $referenceCode
        if (-not $unchanged) { throw 'Original synthetic DOCX changed' }
        if ($referenceCode -ne 0) { throw "Word reference $referenceCode; inspect phase evidence" }
        Invoke-Dotnet 'render-reference.jsonl' @($dll,'render-reference',$work)
    }
}
finally {
    Write-Output "Evidence: docs/evidence/sprint-0/continuation-$run"
    Write-Output "Synthetic workspace: artifacts/sprint-0-converter/continuation-$run"
    Pop-Location
}
