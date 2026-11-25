param([ValidateRange(1800, 86400)][int]$Seconds = 1800, [int]$Port = 5288)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root 'src/Hoga.Server/bin/Release/net10.0/Hoga.Server.dll'
$dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
if (!(Test-Path $dll)) { throw 'Build Hoga.Server in Release first.' }
$dir = Join-Path ([IO.Path]::GetTempPath()) ('hoga-measure-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$journal = Join-Path $dir 'market.jsonl'
$metrics = Join-Path $dir 'metrics.jsonl'
$base = "http://127.0.0.1:$Port"
$env:Market__AdminKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$headers = @{ 'X-Admin-Key' = $env:Market__AdminKey }
$env:Market__Speed = '10'
$env:Market__Bots = 'true'
$env:Market__JournalPath = $journal
$env:Logging__LogLevel__Microsoft = 'Warning'
$env:Logging__LogLevel__Microsoft_AspNetCore = 'Warning'
$process = $null
function Start-Market($label) {
    Start-Process $dotnet -ArgumentList @('"' + $dll + '"', '--urls', $base) -WorkingDirectory (Join-Path $root 'src/Hoga.Server') -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $dir "$label.log") -RedirectStandardError (Join-Path $dir "$label.err")
}
function Wait-Market($process, $timeout = 120) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) {
        if ($process.HasExited) { throw "Server exited with $($process.ExitCode); see $dir" }
        try { return Invoke-RestMethod "$base/api/state/hash" -TimeoutSec 2 } catch { Start-Sleep -Milliseconds 100 }
    }
    throw 'Server did not become ready.'
}
function Stop-Market($process) {
    if ($null -ne $process -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
}
try {
    Write-Output "산출물: $dir"
    $process = Start-Market 'run'
    $null = Wait-Market $process
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $peak = 0L
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Server died; see $dir" }
        $working = $process.WorkingSet64
        $peak = [Math]::Max($peak, $working)
        [pscustomobject]@{ Seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 1); WorkingSetBytes = $working; PeakSampleBytes = $peak; JournalBytes = (Get-Item $journal).Length } | ConvertTo-Json -Compress | Tee-Object -FilePath $metrics -Append
        if ($timer.Elapsed.TotalSeconds -ge $Seconds) { break }
        Start-Sleep -Seconds ([Math]::Min(30, [Math]::Ceiling($Seconds - $timer.Elapsed.TotalSeconds)))
    } while ($true)
    Stop-Market $process
    # Measure actual restart on the same journal, not only the in-process simulation.
    $timer.Restart()
    $process = Start-Market 'restart'
    $restored = Wait-Market $process
    if (!$restored.restoredFromJournal) { throw "Server did not restore the journal; see $dir" }
    [pscustomobject]@{ RestartReadyMs = $timer.ElapsedMilliseconds; State = $restored; PeakSampleBytes = $peak; JournalBytes = (Get-Item $journal).Length } | ConvertTo-Json -Compress | Tee-Object -FilePath $metrics -Append
}
finally {
    Stop-Market $process
    Remove-Item Env:Market__AdminKey -ErrorAction SilentlyContinue
}
