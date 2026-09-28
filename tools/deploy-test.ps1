param(
    [string]$Root = 'D:\personal tasks\modding\听雨的声音\_recon\gametest',
    [string]$Track = 'builtin:jasmine',
    [switch]$NoLaunch
)

# 把最新构建部署到测试副本并（可选）启动。
# 注意：本文件必须保存为「UTF-8 带 BOM」。

$project = 'D:\personal tasks\modding\听雨的声音'
$patcher = Join-Path $project 'src\TingYu.Patcher\bin\Release\net48\TingYu.Patcher.exe'
$plugin = Join-Path $project 'src\TingYu.Plugin\bin\Release\net48\TingYu.Plugin.dll'
$core = Join-Path $project 'src\TingYu.Core\bin\Release\net48\TingYu.Core.dll'
$source = 'D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe'

Get-Process Terraria -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$Root*" } |
    ForEach-Object { Write-Host "结束旧进程 $($_.Id)"; Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 2

Copy-Item $plugin $Root -Force
Copy-Item $core $Root -Force
& $patcher patch-copy --terraria $source --plugin (Join-Path $Root 'TingYu.Plugin.dll') --out (Join-Path $Root 'Terraria.exe')
if ($LASTEXITCODE -ne 0) { Write-Host '注入失败'; exit 1 }

$data = Join-Path $Root 'TingYu'
New-Item -ItemType Directory -Force -Path $data | Out-Null
Remove-Item (Join-Path $data 'tingyu.log'), (Join-Path $data 'status.txt'), (Join-Path $data 'boot.log') -Force -ErrorAction SilentlyContinue
@(
    '# 听雨的声音 —— 人工实测配置'
    'enabled=1'
    "track=$Track"
    'transpose=0'
    'jitter=1'
    'releaseonevent=0'
    'releaseonmousemove=0'
    'hotkey=121'
    'verbose=1'
    'request=idle'
) | Set-Content -Path (Join-Path $data 'config.txt') -Encoding UTF8

Write-Host '部署完成：'
Get-ChildItem $Root -Filter 'TingYu*' | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

if ($NoLaunch) { exit 0 }

$env:TINGYU_VERBOSE = '1'
$logs = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$process = Start-Process -FilePath (Join-Path $Root 'Terraria.exe') -WorkingDirectory $Root -PassThru `
    -ArgumentList '-logerrors', '-logfile', "`"$logs`"", '-savedirectory', "`"$(Join-Path $Root 'saves')`""
Write-Host "已启动 pid=$($process.Id)"
