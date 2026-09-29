param(
    [string]$Root = 'D:\personal tasks\modding\听雨的声音\_recon\gametest'
)

# 重新注入测试副本：把插件放到与 Terraria.exe 同级（CLR 的程序集探测只看这一层）。
$project = 'D:\personal tasks\modding\听雨的声音'
$patcher = Join-Path $project 'src\TingYu.Patcher\bin\Release\net48\TingYu.Patcher.exe'
$plugin = Join-Path $project 'src\TingYu.Plugin\bin\Release\net48\TingYu.Plugin.dll'
$core = Join-Path $project 'src\TingYu.Core\bin\Release\net48\TingYu.Core.dll'
$source = 'D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe'

# 副本被清理掉时自动重建。副本（_recon\gametest）整个都是本机可再生的东西，
# 所以随时可以删；这里把整个游戏目录拷过来，因为 patch-copy 只写 Terraria.exe，
# 没有 Content 与随附依赖的目录根本起不来。
$gameRoot = Split-Path $source -Parent
if (-not (Test-Path (Join-Path $Root 'Terraria.exe'))) {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Copy-Item (Join-Path $gameRoot '*') $Root -Recurse -Force
    Write-Host "已重建测试副本：$Root（从 $gameRoot 拷贝）"
}

Copy-Item $plugin $Root -Force
Copy-Item $core $Root -Force
Remove-Item (Join-Path $Root 'TingYu\TingYu.Plugin.dll'), (Join-Path $Root 'TingYu\TingYu.Core.dll') -Force -ErrorAction SilentlyContinue
$target = Join-Path $Root 'Terraria.exe'
& $patcher patch-copy --terraria $source --plugin (Join-Path $Root 'TingYu.Plugin.dll') --out $target
Write-Host "patch exit=$LASTEXITCODE"
Get-ChildItem $Root -Filter 'TingYu*.dll' | Select-Object Name, Length | Format-Table -AutoSize
Get-ChildItem (Join-Path $Root 'TingYu') | Select-Object Name | Format-Table -AutoSize
