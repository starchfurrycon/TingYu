param(
    [int]$Seconds = 60,
    [string]$Root = 'D:\personal tasks\modding\听雨的声音\_recon\gametest',
    [string]$AutoPlay = 'builtin:jasmine',
    [switch]$KeepAlive
)

# 注意：本文件必须以「UTF-8 带 BOM」保存。Windows PowerShell 5.1 读无 BOM 的 .ps1
# 会按 ANSI 解码，中文路径会变成乱码，Start-Process 直接失败。

$log = Join-Path $Root 'TingYu\tingyu.log'
$status = Join-Path $Root 'TingYu\status.txt'
Remove-Item $log, $status -Force -ErrorAction SilentlyContinue

$env:TINGYU_AUTOPLAY = $AutoPlay

$exe = Join-Path $Root 'Terraria.exe'
$saves = Join-Path $Root 'saves'
$logs = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
Write-Host "=== launching: $exe ==="
$process = Start-Process -FilePath $exe -WorkingDirectory $Root -PassThru `
    -ArgumentList '-autoplay', '-logerrors', '-logfile', "`"$logs`"", `
                  '-savedirectory', "`"$saves`"", `
                  '-playersave', '"踏入泥泞"', '-world', '"草剑挥打"'
Write-Host "pid=$($process.Id)"

$start = Get-Date
$deadline = $start.AddSeconds($Seconds)
$exitedEarly = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) {
        Write-Host "=== process exited early: exit code $($process.ExitCode) ==="
        $exitedEarly = $true
        break
    }
    $elapsed = [int]((Get-Date) - $start).TotalSeconds
    if (Test-Path $log) {
        $lines = (Get-Content $log -ErrorAction SilentlyContinue | Measure-Object).Count
        Write-Host "  t+${elapsed}s  log lines=$lines"
    }
    else {
        Write-Host "  t+${elapsed}s  (no log yet)"
    }
}

if ($KeepAlive) {
    Write-Host "=== leaving process running (pid=$($process.Id)) ==="
}
else {
    Write-Host "=== killing ==="
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }
}

Write-Host "=== log tail ==="
if (Test-Path $log) { Get-Content $log -Tail 80 } else { Write-Host "(no log written)" }
Write-Host "=== status ==="
if (Test-Path $status) { Get-Content $status } else { Write-Host "(no status written)" }
Write-Host "=== tingyu folder ==="
$dir = Join-Path $Root 'TingYu'
if (Test-Path $dir) {
    Get-ChildItem $dir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
}
Write-Host "=== game logs ==="
Get-ChildItem $logs -Filter '*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { Write-Host "--- $($_.Name) ---"; Get-Content $_.FullName -Tail 40 }
if ($exitedEarly) { exit 3 }
exit 0
