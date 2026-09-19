param(
    [Parameter(Mandatory = $true)][int[]]$ProcessIds,
    [ValidateRange(1, 300)][int]$DurationSeconds = 30,
    [ValidateRange(200, 5000)][int]$IntervalMilliseconds = 1000,
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
# Read-only, bounded sampling. This script never launches, stops or configures an app.
$targets = @($ProcessIds | Sort-Object -Unique | ForEach-Object {
    $process = Get-Process -Id $_ -ErrorAction Stop
    $started = $process.StartTime.ToUniversalTime()
    [pscustomobject]@{
        Process = $process
        Id = $process.Id
        Name = $process.ProcessName
        StartedUtc = $started.ToString('o')
        LastCpuTicks = $process.TotalProcessorTime.Ticks
        Exited = $false
    }
})
if ($targets.Count -eq 0) { throw 'Specify at least one running process.' }
$samples = [Collections.Generic.List[object]]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()
$lastSeconds = 0.0
$cpuSeconds = 0.0
$logicalProcessors = [Environment]::ProcessorCount
try {
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $remainingMs = [int][Math]::Ceiling(($DurationSeconds - $clock.Elapsed.TotalSeconds) * 1000)
        Start-Sleep -Milliseconds ([Math]::Max(1, [Math]::Min($IntervalMilliseconds, $remainingMs)))
        $workingBytes = 0L; $privateBytes = 0L; $deltaTicks = 0L; $alive = 0
        foreach ($target in $targets) {
            if ($target.Exited) { continue }
            $target.Process.Refresh()
            if ($target.Process.HasExited) { $target.Exited = $true; continue }
            if ($target.Process.StartTime.ToUniversalTime().ToString('o') -ne $target.StartedUtc) {
                throw 'Process identity changed; refusing to include a reused process ID.'
            }
            $ticks = $target.Process.TotalProcessorTime.Ticks
            $deltaTicks += [Math]::Max(0L, $ticks - $target.LastCpuTicks)
            $target.LastCpuTicks = $ticks
            $workingBytes += $target.Process.WorkingSet64
            $privateBytes += $target.Process.PrivateMemorySize64
            $alive++
        }
        $seconds = $clock.Elapsed.TotalSeconds
        $deltaCpu = $deltaTicks / [double][TimeSpan]::TicksPerSecond
        $cpuSeconds += $deltaCpu
        $samples.Add([pscustomobject]@{
            ElapsedSeconds = [Math]::Round($seconds, 3)
            CpuPercentMachine = [Math]::Round(100 * $deltaCpu / ($seconds - $lastSeconds) / $logicalProcessors, 4)
            WorkingSetMiB = [Math]::Round($workingBytes / 1MB, 2)
            PrivateMiB = [Math]::Round($privateBytes / 1MB, 2)
            AliveProcesses = $alive
        })
        $lastSeconds = $seconds
        if ($alive -eq 0) { break }
    }
    $report = [pscustomobject]@{
        Mode = 'read-only process sampling; no startup or system changes'
        LogicalProcessors = $logicalProcessors
        ElapsedSeconds = [Math]::Round($lastSeconds, 3)
        CpuSeconds = [Math]::Round($cpuSeconds, 4)
        AverageCpuPercentMachine = [Math]::Round(100 * $cpuSeconds / $lastSeconds / $logicalProcessors, 4)
        PeakWorkingSetMiB = ($samples | Measure-Object WorkingSetMiB -Maximum).Maximum
        PeakPrivateMiB = ($samples | Measure-Object PrivateMiB -Maximum).Maximum
        CompleteWindow = (@($targets | Where-Object Exited).Count -eq 0)
        Targets = @($targets | Select-Object Id, Name, StartedUtc, Exited)
        Notes = 'Compare identical workloads. Summed working sets can double-count shared pages. Explorer measurements include Windows and all loaded extensions. A process exit makes the observation window incomplete.'
        Samples = @($samples.ToArray())
    }
    $json = $report | ConvertTo-Json -Depth 5
    if ($OutputPath) {
        $destination = [IO.Path]::GetFullPath($OutputPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::WriteAllText($destination, $json, [Text.UTF8Encoding]::new($false))
    }
    $json
} finally {
    $clock.Stop()
    foreach ($target in $targets) { $target.Process.Dispose() }
}
