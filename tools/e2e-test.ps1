param(
    [int]$Seconds = 120,
    [int]$SettleSeconds = 25,
    [string]$Root = 'D:\personal tasks\modding\听雨的声音\_recon\gametest',
    [string]$AutoPlay = 'builtin:jasmine',
    [switch]$NoDrive
)

# 无人值守的端到端验证：启动注入过的 Terraria 副本，用键盘把菜单点进世界，
# 然后读 TingYu\status.txt 与 tingyu.log 判断接管是否真的在发声。
#
# 注意：本文件必须以「UTF-8 带 BOM」保存（Windows PowerShell 5.1 读无 BOM 的 .ps1 会按 ANSI 解码）。

Add-Type -AssemblyName System.Windows.Forms

$log = Join-Path $Root 'TingYu\tingyu.log'
$status = Join-Path $Root 'TingYu\status.txt'
$config = Join-Path $Root 'TingYu\config.txt'
Remove-Item $log, $status -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'TingYu') | Out-Null

$env:TINGYU_AUTOPLAY = $AutoPlay

# 先把配置写好，让插件第一次读到的就是我们想要的曲目。
@(
    '# 听雨的声音 —— 端到端验证用配置'
    'enabled=1'
    "track=$AutoPlay"
    'transpose=0'
    'jitter=1'
    'releaseonevent=1'
    'releaseonmousemove=0'
    'hotkey=119'
    'verbose=1'
    'request=idle'
) | Set-Content -Path $config -Encoding UTF8

$exe = Join-Path $Root 'Terraria.exe'
$saves = Join-Path $Root 'saves'
$logs = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

Write-Host "=== launching: $exe ==="
$process = Start-Process -FilePath $exe -WorkingDirectory $Root -PassThru `
    -ArgumentList '-logerrors', '-logfile', "`"$logs`"", '-savedirectory', "`"$saves`""
Write-Host "pid=$($process.Id)"

function Send-GameKeys {
    param([string]$Keys, [int]$DelayMs = 700)
    $signature = @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
'@
    if (-not ('Win' -as [type])) { Add-Type -TypeDefinition $signature }
    $process.Refresh()
    if ($process.HasExited) { return $false }
    [Win]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds $DelayMs
    return $true
}

$start = Get-Date
$deadline = $start.AddSeconds($Seconds)
$exitedEarly = $false

if (-not $NoDrive) {
    # 等窗口出现与开场动画过掉
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { break }
        if ($process.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "=== window up, settling ${SettleSeconds}s ==="
    Start-Sleep -Seconds $SettleSeconds

    # 主菜单 → 单人游戏 → 选角色 → 选世界。
    # 选择界面上按「Enter」即「进入」，三个 Enter 足以到世界里。
    foreach ($step in @('{ENTER}', '{ENTER}', '{ENTER}')) {
        $process.Refresh()
        if ($process.HasExited) { $exitedEarly = $true; break }
        $elapsed = [int]((Get-Date) - $start).TotalSeconds
        $sent = Send-GameKeys -Keys $step -DelayMs 1500
        Write-Host "  t+${elapsed}s  sent $step (ok=$sent)"
    }
}

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) {
        Write-Host "=== process exited early: exit code $($process.ExitCode) ==="
        $exitedEarly = $true
        break
    }
    $elapsed = [int]((Get-Date) - $start).TotalSeconds
    $lines = if (Test-Path $log) { (Get-Content $log -ErrorAction SilentlyContinue | Measure-Object).Count } else { 0 }
    Write-Host "  t+${elapsed}s  log lines=$lines"
    if (Test-Path $status) {
        $active = (Select-String -Path $status -Pattern '^active=1' -Quiet)
        $done = (Select-String -Path $status -Pattern '^release=' -Quiet)
        if ($active) { Write-Host "  >>> 接管进行中" }
    }
}

Write-Host "=== killing ==="
$process.Refresh()
if (-not $process.HasExited) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}
Get-Process Terraria -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "=== log ==="
if (Test-Path $log) { Get-Content $log } else { Write-Host "(no log written)" }
Write-Host "=== status ==="
if (Test-Path $status) { Get-Content $status } else { Write-Host "(no status written)" }
if ($exitedEarly) { exit 3 }
exit 0
