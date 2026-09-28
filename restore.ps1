# 听雨的声音 —— 一键还原
#
# 把游戏恢复到安装前的状态：从 TingYu\Terraria.exe.orig 还原本体，
# 删掉复制进游戏目录的 TingYu.Plugin.dll 与 TingYu.Core.dll，并移除数据目录。
# 还原后 Terraria.exe 与原版逐字节一致（可用哈希核对：960A03BF…）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File restore.ps1
#   powershell -ExecutionPolicy Bypass -File restore.ps1 -TerrariaDir "D:\...\Terraria"

[CmdletBinding()]
param(
    [string]$TerrariaDir
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$patcher = Join-Path $here 'TingYu.Patcher.exe'

if (-not (Test-Path -LiteralPath $patcher)) { throw "找不到 TingYu.Patcher.exe，请确认本脚本与载荷放在同一目录。" }

$running = Get-Process -Name 'Terraria' -ErrorAction SilentlyContinue
if ($running) {
    $where = ($running | ForEach-Object { $_.Path }) -join "`n  "
    throw "检测到正在运行的 Terraria 进程，请先退出再还原：`n  $where"
}

$arguments = @('restore', '--payload', $here)
if ($TerrariaDir) { $arguments += @('--terraria', (Join-Path $TerrariaDir 'Terraria.exe')) }

& $patcher @arguments
exit $LASTEXITCODE
