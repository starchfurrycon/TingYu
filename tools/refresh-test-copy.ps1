param(
    [string]$Root = 'D:\personal tasks\modding\听雨的声音\_recon\gametest'
)

# 重新注入测试副本：把插件放到与 Terraria.exe 同级（CLR 的程序集探测只看这一层）。
$project = 'D:\personal tasks\modding\听雨的声音'
$patcher = Join-Path $project 'src\TingYu.Patcher\bin\Release\net48\TingYu.Patcher.exe'
$plugin = Join-Path $project 'src\TingYu.Plugin\bin\Release\net48\TingYu.Plugin.dll'
$core = Join-Path $project 'src\TingYu.Core\bin\Release\net48\TingYu.Core.dll'
$source = 'D:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe'

Copy-Item $plugin $Root -Force
Copy-Item $core $Root -Force
Remove-Item (Join-Path $Root 'TingYu\TingYu.Plugin.dll'), (Join-Path $Root 'TingYu\TingYu.Core.dll') -Force -ErrorAction SilentlyContinue
$target = Join-Path $Root 'Terraria.exe'
& $patcher patch-copy --terraria $source --plugin (Join-Path $Root 'TingYu.Plugin.dll') --out $target
Write-Host "patch exit=$LASTEXITCODE"
Get-ChildItem $Root -Filter 'TingYu*.dll' | Select-Object Name, Length | Format-Table -AutoSize
Get-ChildItem (Join-Path $Root 'TingYu') | Select-Object Name | Format-Table -AutoSize
