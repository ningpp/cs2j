<#
.SYNOPSIS
    Run cs2j `preprocess` one pass at a time and keep a log for every pass.

.DESCRIPTION
    Implements the step-by-step preprocess loop:

      1. build the CLI once (Release) unless -NoBuild is given;
      2. resolve the locked pass order from `preprocess --list-passes`
         (default: implemented passes whose descriptor default is "on"),
         or use -Passes as the selected set;
      3. run pass #1 on -Source, pass #2 on pass #1's intermediate tree,
         pass #3 on pass #2's tree, and so on - one CLI process per pass;
      4. save stdout+stderr and the per-run preprocess-report.json per pass;
      5. stop at the first failing pass, print its log, the first error-looking
         lines and the exact commands to reproduce/resume, then exit 1.

    After fixing the converter, rerun with -StartPass <failed-id>. The script
    rebuilds once, reloads the recorded tree of the previous pass from
    <Destination>\.cs2j-preprocess-steps.json and continues from there. When
    the last pass succeeds the script prints the final preprocessed tree.

    Step mode deliberately runs one pass per process (`--pass <id>`), which is
    the supported single-pass replay workflow. A few passes adapt to their
    chain neighbours; those adaptations are out of scope here.

.PARAMETER Source
    -s: source directory, .csproj, .sln or .slnx (same value as the CLI -s).

.PARAMETER Destination
    -d: destination work root (same value as the CLI -d). Each pass writes its
    own stage tree below this root. The tree name is chosen by the pass
    descriptor and often differs from the pass id (for example:
    local-function-lift -> .cs2j-local-fn-src,
    ref-out-in -> .cs2j-refoutin-src,
    make-readonly -> .cs2j-readonly-src,
    eliminate-goto -> .cs2j-no-goto-src).
    The script never derives the tree name from the pass id: it reads the real
    name from <Destination>\preprocess-report.json and from the
    "[preprocess] output:" line, with a filesystem scan as a defensive
    fallback.

.PARAMETER Passes
    Pass ids to run, reordered into the catalog's locked order. Defaults to all
    implemented default-on passes. Accepts arrays and comma-separated values.

.PARAMETER StartPass
    Resume at this pass id (it must be in the selected list). The input is the
    previous pass' tree recorded by an earlier run. Pass 1 always starts from
    -Source unless -FromTree is given.

.PARAMETER FromTree
    Explicit input tree for the first executed pass. Useful when the recorded
    state is missing. Providing it discards all recorded per-pass trees.

.PARAMETER LogDir
    Parent directory for run logs. A timestamped subdirectory is created for
    every invocation, so earlier logs are never overwritten.

.PARAMETER Profile
    CLI --profile value: default | java-ready | audit.

.PARAMETER NoVerifyBuild
    Pass --no-verify-build to the CLI.

.PARAMETER NoVerifyTests
    Pass --no-verify-tests to the CLI.

.PARAMETER NoBuild
    Skip the Release build of src/CSharpToJava.CLI.

.PARAMETER CliVerbose
    Pass -v (verbose) to the CLI.

.PARAMETER ListOnly
    Print the resolved pass plan and exit without running any pass.

.EXAMPLE
    .\tools\run-preprocess-steps.ps1 -s D:\csharp-sdk\csharp-sdk.sln -d D:\cs\work

.EXAMPLE
    # resume after fixing the converter
    .\tools\run-preprocess-steps.ps1 -s D:\csharp-sdk\csharp-sdk.sln -d D:\cs\work -StartPass make-readonly

.EXAMPLE
    # run only a chosen subset, ordered by the catalog
    .\tools\run-preprocess-steps.ps1 -s D:\csharp-sdk\csharp-sdk.sln -d D:\cs\work -Passes make-readonly,unsafe-ffm
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [Alias('s')]
    [string]$Source,

    [Parameter(Mandatory = $true, Position = 1)]
    [Alias('d')]
    [string]$Destination,

    [Alias('p')]
    [string[]]$Passes,

    [string]$StartPass,

    [string]$FromTree,

    [string]$LogDir,

    [ValidateSet('default', 'java-ready', 'audit')]
    [string]$Profile = 'default',

    [switch]$NoVerifyBuild,

    [switch]$NoVerifyTests,

    [switch]$NoBuild,

    [Alias('v')]
    [switch]$CliVerbose,

    [switch]$ListOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repoRoot 'src\CSharpToJava.CLI\CSharpToJava.CLI.csproj'

function Find-CliDll {
    $binRoot = Join-Path $repoRoot 'src\CSharpToJava.CLI\bin\Release'
    if (-not (Test-Path -LiteralPath $binRoot -PathType Container)) {
        return $null
    }

    $candidate = Get-ChildItem -LiteralPath $binRoot -Filter 'CSharpToJava.CLI.dll' -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object -Property LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        return $null
    }

    return $candidate.FullName
}

function Quote-Arg {
    param([string]$Value)

    if ($Value -match '[\s"]') {
        return '"' + $Value.Replace('"', '\"') + '"'
    }

    return $Value
}

function Format-CliCommand {
    param([string[]]$CommandArgs)

    return (@('dotnet') + ($CommandArgs | ForEach-Object { Quote-Arg $_ })) -join ' '
}

# Native stderr becomes an ErrorRecord in the pipeline; with
# $ErrorActionPreference='Stop' that would abort the script even when the pass
# merely emitted a diagnostic. Capture with Continue and rely on exit code +
# report for success/failure.
function Invoke-Dotnet {
    param(
        [string[]]$CommandArgs,
        [switch]$Quiet
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $captured = @()
    Push-Location $repoRoot
    try {
        if ($Quiet) {
            & dotnet @CommandArgs 2>&1 |
                ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { [string]$_ } } |
                Tee-Object -Variable captured | Out-Null
        }
        else {
            & dotnet @CommandArgs 2>&1 |
                ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { [string]$_ } } |
                Tee-Object -Variable captured | Out-Host
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = @($captured)
    }
}

# Resolve the stage tree a pass actually wrote. Pass ids and stage directory
# names are independent (local-function-lift -> .cs2j-local-fn-src,
# ref-out-in -> .cs2j-refoutin-src, make-readonly -> .cs2j-readonly-src, ...),
# so the reported name wins and the filesystem scan is only a fallback.
function Find-StageTree {
    param(
        [string]$ReportedDirectory,
        [string[]]$KnownBefore,
        [datetime]$StartedAt
    )

    if (-not [string]::IsNullOrWhiteSpace($ReportedDirectory)) {
        if ([System.IO.Path]::IsPathRooted($ReportedDirectory)) {
            $reported = $ReportedDirectory
        }
        else {
            $reported = Join-Path $destPath $ReportedDirectory
        }
        if (Test-Path -LiteralPath $reported -PathType Container) {
            return $reported
        }
    }

    $candidates = @()
    foreach ($dir in Get-ChildItem -LiteralPath $destPath -Directory -Filter '.cs2j-*-src' -ErrorAction SilentlyContinue) {
        if ($KnownBefore -notcontains $dir.Name -or $dir.LastWriteTime -ge $StartedAt) {
            $candidates += $dir
        }
    }

    if ($candidates.Count -eq 0) {
        return $null
    }

    return ($candidates |
        Sort-Object -Property LastWriteTime -Descending |
        Select-Object -First 1).FullName
}

try {
    if (-not (Test-Path -LiteralPath $cliProject -PathType Leaf)) {
        throw "CLI project not found: $cliProject"
    }

    $sourcePath = (Resolve-Path -LiteralPath $Source -ErrorAction Stop).Path
    $sourceIsDirectory = Test-Path -LiteralPath $sourcePath -PathType Container
    $sourceExtension = [System.IO.Path]::GetExtension($sourcePath).ToLowerInvariant()
    if (-not $sourceIsDirectory -and @('.sln', '.slnx', '.csproj') -notcontains $sourceExtension) {
        throw "Source must be a directory, .csproj, .sln or .slnx: $sourcePath"
    }

    $destPath = [System.IO.Path]::GetFullPath($Destination)

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    if ([string]::IsNullOrWhiteSpace($LogDir)) {
        $runLogDir = Join-Path $destPath ('_preprocess-logs\' + $stamp)
    }
    else {
        $runLogDir = Join-Path ([System.IO.Path]::GetFullPath($LogDir)) $stamp
    }
    New-Item -ItemType Directory -Force -Path $runLogDir | Out-Null

    Write-Host "[cs2j] repo root : $repoRoot"
    Write-Host "[cs2j] source    : $sourcePath"
    Write-Host "[cs2j] destination: $destPath"
    Write-Host "[cs2j] logs      : $runLogDir"

    # ---- build -----------------------------------------------------------
    if (-not $NoBuild) {
        Write-Host ''
        Write-Host '[build] dotnet build -c Release src/CSharpToJava.CLI'
        $buildResult = Invoke-Dotnet -CommandArgs @('build', $cliProject, '-c', 'Release', '-nologo', '-v', 'minimal')

        $buildLog = Join-Path $runLogDir '00-build.log'
        if ($buildResult.Output.Count -gt 0) {
            $buildResult.Output | Out-File -LiteralPath $buildLog -Encoding utf8
        }
        if ($buildResult.ExitCode -ne 0) {
            Write-Host ''
            Write-Host "ERROR: CLI build failed (exit $($buildResult.ExitCode)). Log: $buildLog" -ForegroundColor Red
            exit 2
        }
    }

    $cliDll = Find-CliDll
    if ([string]::IsNullOrEmpty($cliDll)) {
        Write-Host 'ERROR: CSharpToJava.CLI.dll (Release) not found; build the CLI or drop -NoBuild.' -ForegroundColor Red
        exit 2
    }
    Write-Host "[cs2j] cli       : $cliDll"

    # ---- pass catalog ----------------------------------------------------
    $listResult = Invoke-Dotnet -CommandArgs @($cliDll, 'preprocess', '--list-passes') -Quiet
    $listOutput = $listResult.Output

    $listLog = Join-Path $runLogDir '00-list-passes.log'
    if ($listOutput.Count -gt 0) {
        $listOutput | Out-File -LiteralPath $listLog -Encoding utf8
    }
    if ($listResult.ExitCode -ne 0) {
        Write-Host "ERROR: 'preprocess --list-passes' failed (exit $($listResult.ExitCode)). Log: $listLog" -ForegroundColor Red
        exit 2
    }

    $catalog = @()
    foreach ($line in $listOutput) {
        $text = [string]$line
        if ($text -match '^\s*#') {
            continue
        }
        if ($text -match '^(?<id>[A-Za-z0-9][A-Za-z0-9\-]*)\s+(?<stage>\S+)\s+(?<status>\S+)\s+(?<default>on|off)(\s|$)') {
            $catalog += [pscustomobject]@{
                Id      = $Matches['id']
                Stage   = $Matches['stage']
                Status  = $Matches['status']
                Default = $Matches['default']
            }
        }
    }

    if ($catalog.Count -eq 0) {
        Write-Host "ERROR: could not parse the pass catalog. Inspect $listLog" -ForegroundColor Red
        exit 2
    }

    $orderIndex = @{}
    $statusById = @{}
    for ($i = 0; $i -lt $catalog.Count; $i++) {
        $orderIndex[$catalog[$i].Id] = $i
        $statusById[$catalog[$i].Id] = $catalog[$i].Status
    }

    if ($Passes -and $Passes.Count -gt 0) {
        $requested = @()
        foreach ($entry in $Passes) {
            foreach ($part in ([string]$entry -split ',')) {
                $trimmed = $part.Trim()
                if ($trimmed.Length -gt 0) {
                    $requested += $trimmed
                }
            }
        }
    }
    else {
        $requested = @($catalog |
            Where-Object { $_.Status -eq 'Implemented' -and $_.Default -eq 'on' } |
            ForEach-Object { $_.Id })
    }

    $requested = @($requested | Select-Object -Unique)
    if ($requested.Count -eq 0) {
        Write-Host 'ERROR: the selected pass list is empty.' -ForegroundColor Red
        exit 2
    }

    foreach ($passId in $requested) {
        if (-not $orderIndex.ContainsKey($passId)) {
            Write-Host "ERROR: unknown pass id '$passId'. Run: dotnet `"$cliDll`" preprocess --list-passes" -ForegroundColor Red
            exit 2
        }
        if ($statusById[$passId] -ne 'Implemented') {
            Write-Host "ERROR: pass '$passId' is registered as '$($statusById[$passId])' and cannot run." -ForegroundColor Red
            exit 2
        }
    }

    $passIds = @($requested | Sort-Object -Property { $orderIndex[$_] })

    $startIndex = 0
    if (-not [string]::IsNullOrWhiteSpace($StartPass)) {
        $startIndex = -1
        for ($i = 0; $i -lt $passIds.Count; $i++) {
            if ($passIds[$i] -eq $StartPass) {
                $startIndex = $i
                break
            }
        }
        if ($startIndex -lt 0) {
            Write-Host "ERROR: -StartPass '$StartPass' is not in the selected list:" -ForegroundColor Red
            Write-Host ('  ' + ($passIds -join ', '))
            exit 2
        }
    }

    Write-Host ''
    Write-Host ("[plan] {0} pass(es){1}" -f $passIds.Count,
        $(if ($startIndex -gt 0) { ", starting at #$($startIndex + 1) '$($passIds[$startIndex])'" } else { '' }))
    if ($ListOnly) {
        for ($i = 0; $i -lt $passIds.Count; $i++) {
            Write-Host ("  {0,2}. {1}" -f ($i + 1), $passIds[$i])
        }
        exit 0
    }

    # ---- state -----------------------------------------------------------
    # okInputs maps a pass id to the exact -s value the CLI handed to the next
    # pass in one chain run ([preprocess] output line): usually the mapped
    # entry file inside the pass' stage tree.
    # trees maps a pass id to the stage directory name the descriptor actually
    # used, which is not necessarily ".cs2j-<pass-id>-src".
    $statePath = Join-Path $destPath '.cs2j-preprocess-steps.json'
    $okInputs = @{}
    $stageTrees = @{}
    $freshStart = ($startIndex -eq 0 -and [string]::IsNullOrWhiteSpace($FromTree))

    if (-not $freshStart -and (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        try {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            $stateSource = $state.PSObject.Properties['source']
            $stateInputs = $state.PSObject.Properties['okInputs']
            $stateTrees = $state.PSObject.Properties['trees']
            if ($null -ne $stateSource -and [string]$stateSource.Value -eq $sourcePath) {
                if ($null -ne $stateInputs -and $null -ne $stateInputs.Value) {
                    foreach ($property in $stateInputs.Value.PSObject.Properties) {
                        $okInputs[$property.Name] = [string]$property.Value
                    }
                }
                if ($null -ne $stateTrees -and $null -ne $stateTrees.Value) {
                    foreach ($property in $stateTrees.Value.PSObject.Properties) {
                        $stageTrees[$property.Name] = [string]$property.Value
                    }
                }
            }
            else {
                Write-Host "[state] ignoring $statePath (different source)."
            }
        }
        catch {
            Write-Warning "Could not read state file '$statePath': $($_.Exception.Message)"
        }
    }

    function Save-State {
        $payload = [pscustomobject]@{
            schemaVersion = 1
            source        = $sourcePath
            destination   = $destPath
            updatedAt     = (Get-Date).ToString('o')
            okInputs      = $okInputs
            trees         = $stageTrees
        }
        New-Item -ItemType Directory -Force -Path $destPath | Out-Null
        $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding utf8
    }

    $currentInput = $sourcePath
    if (-not [string]::IsNullOrWhiteSpace($FromTree)) {
        $okInputs = @{}
        $stageTrees = @{}
        $currentInput = (Resolve-Path -LiteralPath $FromTree -ErrorAction Stop).Path
        Write-Host "[state] explicit -FromTree: $currentInput"
        Save-State
    }
    elseif ($freshStart) {
        $okInputs = @{}
        $stageTrees = @{}
        Save-State
    }
    else {
        $previousPass = $passIds[$startIndex - 1]
        $previousOutput = $null
        if ($okInputs.ContainsKey($previousPass)) {
            $previousOutput = [string]$okInputs[$previousPass]
        }
        if (([string]::IsNullOrEmpty($previousOutput) -or -not (Test-Path -LiteralPath $previousOutput)) -and
            $stageTrees.ContainsKey($previousPass)) {
            $previousTree = [string]$stageTrees[$previousPass]
            if (Test-Path -LiteralPath $previousTree -PathType Container) {
                $previousOutput = $previousTree
                Write-Host "[state] previous entry missing; using stage tree of '$previousPass': $previousTree" -ForegroundColor Yellow
            }
        }
        if ([string]::IsNullOrEmpty($previousOutput) -or -not (Test-Path -LiteralPath $previousOutput)) {
            Write-Host "ERROR: no usable recorded output for previous pass '$previousPass'." -ForegroundColor Red
            if ($stageTrees.ContainsKey($previousPass)) {
                Write-Host ("  recorded stage tree: " + $stageTrees[$previousPass]) -ForegroundColor Yellow
            }
            Write-Host 'Rerun from an earlier pass, or pass -FromTree <previous stage tree/entry>.' -ForegroundColor Yellow
            exit 2
        }
        $currentInput = $previousOutput
        Write-Host "[state] resume input ($previousPass): $currentInput"
    }

    # ---- run -------------------------------------------------------------
    $results = @()
    $finalTree = $null

    function Write-Summary {
        param([string]$Status)

        $summaryPath = Join-Path $runLogDir 'summary.txt'
        $lines = @()
        $lines += '# cs2j preprocess steps'
        $lines += "source:      $sourcePath"
        $lines += "destination: $destPath"
        $lines += "status:      $Status"
        $lines += ''
        $lines += ('{0,-8} {1,3}  {2,-34} {3,9}  {4}' -f 'result', '#', 'pass', 'seconds', 'tree')
        foreach ($row in $results) {
            $treeText = $row.Tree
            if ([string]::IsNullOrEmpty($treeText)) {
                $treeText = '-'
            }
            $lines += ('{0,-8} {1,3}  {2,-34} {3,9:N1}  {4}' -f $row.Status, $row.Step, $row.Pass, $row.Seconds, $treeText)
        }
        $lines += ''
        $lines += "logs: $runLogDir"
        $lines | Set-Content -LiteralPath $summaryPath -Encoding utf8
        return $summaryPath
    }

    for ($i = $startIndex; $i -lt $passIds.Count; $i++) {
        $passId = $passIds[$i]
        $stepNo = $i + 1
        $stepLabel = '{0:D2}-{1}' -f $stepNo, $passId
        $stepLog = Join-Path $runLogDir ($stepLabel + '.log')
        $stepReportCopy = Join-Path $runLogDir ($stepLabel + '.report.json')
        $reportPath = Join-Path $destPath 'preprocess-report.json'

        $cliArgs = @(
            $cliDll,
            'preprocess',
            '-s', $currentInput,
            '-d', $destPath,
            '--pass', $passId
        )
        if ($Profile -ne 'default') {
            $cliArgs += @('--profile', $Profile)
        }
        if ($NoVerifyBuild) {
            $cliArgs += '--no-verify-build'
        }
        if ($NoVerifyTests) {
            $cliArgs += '--no-verify-tests'
        }
        if ($CliVerbose) {
            $cliArgs += '-v'
        }

        $commandText = Format-CliCommand -CommandArgs $cliArgs

        @(
            "# cs2j preprocess step $stepNo/$($passIds.Count): $passId",
            "# started: $(Get-Date -Format o)",
            "# input:   $currentInput",
            "# output:  $destPath",
            "# command: $commandText"
        ) | Out-File -LiteralPath $stepLog -Encoding utf8

        Write-Host ''
        Write-Host ("[{0}/{1}] pass {2}" -f $stepNo, $passIds.Count, $passId) -ForegroundColor Cyan
        Write-Host ("        input: {0}" -f $currentInput)
        Write-Host ("        log:   {0}" -f $stepLog)

        if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
            Remove-Item -LiteralPath $reportPath -Force
        }

        $stageDirsBefore = @(Get-ChildItem -LiteralPath $destPath -Directory -Filter '.cs2j-*-src' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Name })
        $stepStartedAt = Get-Date

        $stepOutput = @()
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $stepResult = Invoke-Dotnet -CommandArgs $cliArgs
            $stepOutput = $stepResult.Output
            $stepExit = $stepResult.ExitCode
        }
        finally {
            $stopwatch.Stop()
        }

        if ($stepOutput.Count -gt 0) {
            $stepOutput | Out-File -LiteralPath $stepLog -Append -Encoding utf8
        }
        @(
            '',
            "# finished: $(Get-Date -Format o)",
            "# exit code: $stepExit",
            "# elapsed: $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s"
        ) | Out-File -LiteralPath $stepLog -Append -Encoding utf8

        $passSummary = ''
        $reportOk = $false
        $reportedStageDir = ''

        if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
            try {
                $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
                $entries = @($report.passes)
                if ($entries.Count -gt 0) {
                    $entry = $entries[$entries.Count - 1]
                    if ($null -ne $entry.intermediateDirectory) {
                        $reportedStageDir = [string]$entry.intermediateDirectory
                    }
                    $passSummary = [string]$entry.summary
                    $reportOk = (([string]$entry.status) -eq 'ok')
                }
                Copy-Item -LiteralPath $reportPath -Destination $stepReportCopy -Force
            }
            catch {
                Write-Warning "Could not read '$reportPath': $($_.Exception.Message)"
            }
        }

        # The descriptor owns the stage directory name; it is not derived from
        # the pass id. Prefer the reported name, fall back to a filesystem scan.
        $passTree = Find-StageTree -ReportedDirectory $reportedStageDir -KnownBefore $stageDirsBefore -StartedAt $stepStartedAt
        if ($null -ne $passTree -and -not [string]::IsNullOrWhiteSpace($reportedStageDir) -and
            (Split-Path -Leaf $passTree) -ne $reportedStageDir) {
            Write-Host ("        [warn] report named '{0}' but resolved '{1}'" -f $reportedStageDir, (Split-Path -Leaf $passTree)) -ForegroundColor Yellow
        }
        if ($null -eq $passTree -and $stepExit -eq 0 -and $reportOk) {
            Write-Warning 'pass succeeded but no stage tree could be resolved'
        }

        $passed = ($stepExit -eq 0) -and $reportOk -and ($null -ne $passTree)
        $statusText = 'failed'
        if ($passed) {
            $statusText = 'ok'
        }

        $results += [pscustomobject]@{
            Step     = $stepNo
            Pass     = $passId
            Status   = $statusText
            Seconds  = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
            Input    = $currentInput
            Tree     = $passTree
            Log      = $stepLog
            ExitCode = $stepExit
        }

        if ($passed) {
            # The chain feeds the next pass exactly this path; use it so the
            # replay keeps the same entry file (.sln/.csproj) or directory.
            $nextInput = $null
            foreach ($line in $stepOutput) {
                $text = [string]$line
                if ($text -match '^\[preprocess\] output:\s*(.+)$') {
                    $nextInput = $Matches[1].Trim()
                }
            }
            if ([string]::IsNullOrEmpty($nextInput) -or -not (Test-Path -LiteralPath $nextInput)) {
                $nextInput = $passTree
            }
            if (-not $nextInput.StartsWith($passTree, [System.StringComparison]::OrdinalIgnoreCase)) {
                Write-Host ("        [warn] next input is outside the stage tree: {0}" -f $nextInput) -ForegroundColor Yellow
            }

            $okInputs[$passId] = $nextInput
            $stageTrees[$passId] = $passTree
            Save-State
            $currentInput = $nextInput
            $finalTree = $passTree
            Write-Host ("        OK in {0:N1}s" -f $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
            Write-Host ("        stage tree: {0}" -f (Split-Path -Leaf $passTree))
            if (-not [string]::IsNullOrWhiteSpace($passSummary)) {
                Write-Host ("        {0}" -f $passSummary)
            }
            continue
        }

        # ---- first failure: report and stop ------------------------------
        $errorLines = @()
        foreach ($line in $stepOutput) {
            $text = [string]$line
            if ($text -match '(?i)(:\s*error\b|\[error\]|error\s+CS\d+|CS2J\d{4})' -and $text -notmatch '(?i)\b0\s+error') {
                $errorLines += $text
                if ($errorLines.Count -ge 10) {
                    break
                }
            }
        }

        $summaryPath = Write-Summary -Status "FAILED at '$passId'"

        Write-Host ''
        Write-Host ('=' * 78) -ForegroundColor Red
        Write-Host ("FAILED at pass '{0}' (step {1}/{2}), exit code {3}" -f $passId, $stepNo, $passIds.Count, $stepExit) -ForegroundColor Red
        Write-Host ('=' * 78) -ForegroundColor Red
        Write-Host ("log:  {0}" -f $stepLog)
        if ($null -ne $passTree) {
            Write-Host ("tree: {0} (may be incomplete)" -f $passTree)
        }
        if ($errorLines.Count -gt 0) {
            Write-Host ''
            Write-Host 'first error lines:' -ForegroundColor Yellow
            foreach ($errorLine in $errorLines) {
                Write-Host ("  " + $errorLine) -ForegroundColor Yellow
            }
        }
        else {
            Write-Host ''
            Write-Host 'no error-looking line found; inspect the log above.' -ForegroundColor Yellow
        }

        Write-Host ''
        Write-Host 'reproduce this pass:' -ForegroundColor Yellow
        Write-Host ("  " + $commandText)

        $resumeArgs = @(
            '-NoProfile', '-File', ('"' + $PSCommandPath + '"'),
            '-Source', ('"' + $sourcePath + '"'),
            '-Destination', ('"' + $destPath + '"')
        )
        if ($Passes -and $Passes.Count -gt 0) {
            $resumeArgs += @('-Passes', ('"' + ($passIds -join ',') + '"'))
        }
        $resumeArgs += @('-StartPass', $passId)
        if ($Profile -ne 'default') {
            $resumeArgs += @('-Profile', $Profile)
        }
        if ($NoVerifyBuild) {
            $resumeArgs += '-NoVerifyBuild'
        }
        if ($NoVerifyTests) {
            $resumeArgs += '-NoVerifyTests'
        }
        if ($CliVerbose) {
            $resumeArgs += '-CliVerbose'
        }
        Write-Host ''
        Write-Host 'resume after the fix:' -ForegroundColor Yellow
        Write-Host ('  powershell ' + ($resumeArgs -join ' '))
        Write-Host ''
        Write-Host ("summary: {0}" -f $summaryPath)
        Write-Host ''
        exit 1
    }

    $summaryPath = Write-Summary -Status 'OK'
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Green
    Write-Host ("ALL {0} PASSES OK" -f $passIds.Count) -ForegroundColor Green
    Write-Host ('=' * 78) -ForegroundColor Green
    Write-Host ("final tree: {0}" -f $finalTree)
    Write-Host ("logs:       {0}" -f $runLogDir)
    Write-Host ("summary:    {0}" -f $summaryPath)
    exit 0
}
catch {
    Write-Host ''
    Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    exit 2
}
