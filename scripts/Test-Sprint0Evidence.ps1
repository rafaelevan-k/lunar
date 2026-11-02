param(
    [string]$BaselineRun = '20261026T195334Z',
    [string]$ContinuationRun = 'continuation-20261026T195608Z'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$base = Join-Path $repo "docs\evidence\sprint-0\$BaselineRun"
$current = Join-Path $repo "docs\evidence\sprint-0\$ContinuationRun"
function Assert-Evidence([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Output "PASS: $Message"
}
$baseCommands = @(Get-Content -LiteralPath (Join-Path $base 'commands.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
Assert-Evidence (@($baseCommands | Where-Object { $_.ExitCode -ne 0 }).Count -eq 0) 'Recorded baseline commands succeeded'
foreach ($layer in @('Windows','Desktop')) {
    $build = Get-Content -LiteralPath (Join-Path $base "build-$layer.log") -Raw
    Assert-Evidence ($build -match '0 Warning\(s\)' -and $build -match '0 Error\(s\)') "$layer Release build has zero warnings/errors"
}
$probes = @(Get-Content -LiteralPath (Join-Path $base 'native-probes.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
Assert-Evidence ($probes.Count -eq 5 -and @($probes | Where-Object {$_.Result.Status -ne 'Available'}).Count -eq 0) 'Five real read-only API components Available on this host'
$window = @(Get-Content -LiteralPath (Join-Path $base 'window-smoke.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$render = ($window | Where-Object Operation -eq 'WindowRendered').Result
$exit = ($window | Where-Object Operation -eq 'Exit').Result
Assert-Evidence ($render.AllowsTransparency -and $render.Topmost -and -not $render.IsActive -and $exit.TrayDisposed -and $exit.ApplicationExitCode -eq 0) 'WPF runtime/render/cleanup proof (not interaction proof)'
$experiments = @(Get-Content -LiteralPath (Join-Path $current 'generate.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$density = @($experiments | Where-Object Experiment -eq 'PNG-ResolutionNormalization')
Assert-Evidence (@($experiments | Where-Object Status -eq 'FAIL').Count -eq 0 -and $density.Count -eq 4) 'Converter assertions pass with four density-normalization records'
foreach ($record in $density) {
    $details = $record.Details
    Assert-Evidence ($details.PixelsUnchanged -and [Math]::Abs($details.ActualDpiX - $details.RequestedDpi) -lt 0.02 -and [Math]::Abs($details.ActualDpiY - $details.RequestedDpi) -lt 0.02) "Synthetic PNG $($details.RequestedDpi)-DPI density/pixel proof"
}
$word = Get-Content -LiteralPath (Join-Path $current 'word-reference-result.json') -Raw | ConvertFrom-Json
Assert-Evidence ($word.ExitCode -eq 'TIMEOUT' -and $word.OriginalDocxUnchanged -and -not $word.ReferencePdfExists -and $word.Cleanup -eq 'Stopped exact owned process') 'Word timeout is explicit, no PDF/PASS fabricated; original preserved and owned process cleaned'
$manifest = Get-Content -LiteralPath (Join-Path $current 'fixture-output-manifest.json') -Raw | ConvertFrom-Json
$work = Join-Path $repo "artifacts\sprint-0-converter\$ContinuationRun"
foreach ($file in $manifest) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $work $file.Fixture) -Algorithm SHA256).Hash
    Assert-Evidence ($actual -eq $file.SHA256) "Synthetic artifact hash: $($file.Fixture)"
}
foreach ($script in Get-ChildItem -LiteralPath (Join-Path $repo 'scripts') -Filter '*.ps1') {
    $tokens = $null; $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$parseErrors)
    Assert-Evidence ($parseErrors.Count -eq 0) "PowerShell syntax: $($script.Name)"
}
foreach ($project in Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.csproj' -Recurse) {
    [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
    Assert-Evidence (@($xml.SelectNodes('//ProjectReference')).Count -eq 0) "Disposable spike has no cross-project references: $($project.Name)"
}
$fe = Get-Content -LiteralPath (Join-Path $repo 'FE_TASK.md') -Raw
$be = Get-Content -LiteralPath (Join-Path $repo 'BE_TASK.md') -Raw
Assert-Evidence ([regex]::Matches($fe,'(?m)^\| FE-\d{2} \|[^\r\n]+\| TODO \|').Count -eq 26 -and [regex]::Matches($be,'(?m)^\| BE-\d{2} \|[^\r\n]+\| TODO \|').Count -eq 30) 'All 56 numbered tasks remain TODO'
Assert-Evidence (-not (Test-Path -LiteralPath (Join-Path $repo 'docs\ARCHITECTURE.md')) -and (Get-Content -LiteralPath (Join-Path $repo 'docs\README.md') -Raw).Contains('(../ARCHITECTURE.md)')) 'Root architecture is sole canonical copy and relative link is correct'
$decision = Get-Content -LiteralPath (Join-Path $repo 'docs\ADR\ADR-0005-sprint-0-validation-deferral.md') -Raw -Encoding UTF8
if ($decision.Contains('Status: Accepted')) {
    $closure = Join-Path $repo 'docs\evidence\sprint-0\closure-20261027'
    $approval = Get-Content -LiteralPath (Join-Path $closure 'APPROVAL.md') -Raw -Encoding UTF8
    Assert-Evidence ($decision.Contains('Approver: User') -and $approval.Contains('ya saya setuju, dan tutup sprint 0 dengan kondisi yg saat ini masih terbatas')) 'ADR-0005 acceptance and closure have explicit user approval'
    $status = Get-Content -LiteralPath (Join-Path $repo 'docs\SPRINT_STATUS.md') -Raw -Encoding UTF8
    Assert-Evidence ($status.Contains('DONE for the amended discovery scope') -and $status.Contains('CLOSED') -and $status.Contains('No Sprint 1/BE-01 approval')) 'Discovery closure is scoped; Sprint 1 remains separately authorized'
    $prd = Get-Content -LiteralPath (Join-Path $repo 'PRD.md') -Raw -Encoding UTF8
    $policy = Get-Content -LiteralPath (Join-Path $repo 'AGENTS.md') -Raw -Encoding UTF8
    Assert-Evidence ($prd.Contains('Approved evidence timing, 2026-10-27') -and $policy.Contains('Approved Sprint 0 evidence-timing exception (2026-10-27)')) 'Canonical product and execution gates record the approved exception'
    foreach ($deadline in @('FE-02/05/06','FE-05/BE-08','FE-26/BE-30','Sprint 6')) {
        $decisionDeadline = if ($deadline -eq 'FE-05/BE-08') { 'FE-05 and BE-08' } else { $deadline }
        Assert-Evidence ($prd.Contains($deadline) -and $decision.Contains($decisionDeadline)) "Deferred acceptance obligation retained: $deadline"
    }
    $preserved = Get-Content -LiteralPath (Join-Path $closure 'preserved-before.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in $preserved) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $repo $entry.Path) -Algorithm SHA256).Hash
        if ($hash -ne $entry.SHA256) { throw "Preserved file changed: $($entry.Path)" }
    }
    Write-Output "PASS: $(@($preserved).Count) prototype/project/backlog/architecture/runner/historical evidence files unchanged"
} else {
    Assert-Evidence ($decision.Contains('Status: Proposed') -and $decision.Contains('NOT effective')) 'Gate amendment remains Proposed, no self-approval'
}
$markdown = @(Get-ChildItem -LiteralPath $repo -Filter '*.md') + @(Get-ChildItem -LiteralPath (Join-Path $repo 'docs') -Filter '*.md' -Recurse)
$localLinks = 0
foreach ($document in $markdown) {
    $contents = Get-Content -LiteralPath $document.FullName -Raw
    foreach ($match in [regex]::Matches($contents,'\]\(([^)\s]+)\)')) {
        $target = $match.Groups[1].Value.Split('#')[0]
        if ($target -eq '' -or $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:' -or $target.Contains('<')) { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $document.DirectoryName $target))) { throw "Broken relative link in $($document.Name): $target" }
        $localLinks++
    }
}
Write-Output "PASS: $localLinks relative Markdown file links resolve (anchor targets not tested)"
Write-Output 'No manual UI/target OS/fidelity acceptance is inferred by this evidence check.'
