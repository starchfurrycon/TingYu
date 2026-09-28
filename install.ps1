# 听雨的声音 —— 一键安装
#
# 做三件事：
#   1. 把注入载荷（TingYu.Plugin.dll、TingYu.Core.dll）复制到游戏目录；
#      CLR 在启动阶段只会在 exe 所在目录和 GAC 里找注入目标程序集，
#      放到子目录会直接让游戏以 0xE0434352 崩溃退出。
#   2. 调用 TingYu.Patcher 修改 Terraria.exe，把四个钩子挂进去。
#   3. 原文件会被备份成 Terraria.exe.tingyu-backup，restore.ps1 靠它完整还原。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File install.ps1
#   powershell -ExecutionPolicy Bypass -File install.ps1 -TerrariaDir "D:\...\Terraria"

[CmdletBinding()]
param(
    [string]$TerrariaDir
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$patcher = Join-Path $here 'TingYu.Patcher.exe'

if (-not (Test-Path -LiteralPath $patcher)) { throw "找不到 TingYu.Patcher.exe，请确认本脚本与载荷放在同一目录。" }

# 正在运行的游戏会锁住 Terraria.exe，必须先关掉，否则改动会写到一半失败。
$running = Get-Process -Name 'Terraria' -ErrorAction SilentlyContinue
if ($running) { throw "Terraria 正在运行，请先退出游戏再安装。" }

$arguments = @('install', '--payload', $here)
if ($TerrariaDir) { $arguments += @('--terraria', (Join-Path $TerrariaDir 'Terraria.exe')) }

& $patcher @arguments
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host ''
    Write-Host '安装未完成。若提示版本不符，说明这个程序只针对已验证过的游戏版本，' -ForegroundColor Yellow
    Write-Host '强行注入可能让游戏无法启动，所以脚本选择了停下。' -ForegroundColor Yellow
    exit $code
}

Write-Host ''
Write-Host '安装完成。启动游戏后，按 F10 开始/结束接管（游戏内需手持乐器）。'
