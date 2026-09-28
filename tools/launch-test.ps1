param(
    [string]$Root = 'D:\personal tasks\modding\听雨的声音\_recon\gametest',
    [string]$Track = 'builtin:jasmine',
    [switch]$HeavyJasmine
)

# 给「人工实测」用：把副本准备好、开详细日志、启动游戏，然后由玩家自己在游戏里按键。
# 注意：本文件必须保存为「UTF-8 带 BOM」（Windows PowerShell 5.1 读无 BOM 的 .ps1 会按 ANSI 解码）。

$project = 'D:\personal tasks\modding\听雨的声音'
$patcher = Join-Path $project 'src\TingYu.Patcher\bin\Release\net48\TingYu.Patcher.exe'
$plugin = Join-Path $project 'src\TingYu.Plugin\bin\Release\net48\TingYu.Plugin.dll'
$core = Join-Path $project 'src\TingYu.Core\bin\Release\net48\TingYu.Core.dll'
$source = 'D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe'

Write-Host '=== 1/4 重新注入副本 ==='
Copy-Item $plugin $Root -Force
Copy-Item $core $Root -Force
& $patcher patch-copy --terraria $source --plugin (Join-Path $Root 'TingYu.Plugin.dll') --out (Join-Path $Root 'Terraria.exe')
if ($LASTEXITCODE -ne 0) { Write-Host '注入失败'; exit 1 }

Write-Host '=== 2/4 准备独立存档 ==='
$saves = Join-Path $Root 'saves'
New-Item -ItemType Directory -Force -Path (Join-Path $saves 'Players'), (Join-Path $saves 'Worlds') | Out-Null
# 一大清早.plr 是我方扫描出来唯一带乐器（星星吉他 4715）的角色。
Copy-Item 'C:\Users\lenovo\Documents\My Games\Terraria\Players\一大清早.plr' (Join-Path $saves 'Players') -Force
Copy-Item 'C:\Users\lenovo\Documents\My Games\Terraria\Worlds\草剑挥打.wld' (Join-Path $saves 'Worlds') -Force
Get-ChildItem (Join-Path $saves 'Players') | Select-Object Name, Length | Format-Table -AutoSize

Write-Host '=== 3/4 清日志、写配置 ==='
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

Write-Host '=== 4/4 启动游戏 ==='
$env:TINGYU_VERBOSE = '1'
$logs = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$process = Start-Process -FilePath (Join-Path $Root 'Terraria.exe') -WorkingDirectory $Root -PassThru `
    -ArgumentList '-logerrors', '-logfile', "`"$logs`"", '-savedirectory', "`"$saves`""
Write-Host "pid=$($process.Id)"
Write-Host ''
Write-Host '游戏已启动。请在游戏里：'
Write-Host '  1) 单人游戏 → 选「一大清早」→ 选「草剑挥打」'
Write-Host '  2) 手上拿星星吉他（背包里搜「星星」）'
Write-Host '  3) 按 F8 开始接管'
Write-Host '  4) 想提前停就再按一次 F8'
Write-Host ''
Write-Host "日志: $(Join-Path $data 'tingyu.log')"
Write-Host "启动记录: $(Join-Path $data 'boot.log')"
