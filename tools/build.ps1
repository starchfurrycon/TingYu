# 听雨的声音 —— 构建脚本
#
# 产出：
#   build\<Configuration>\payload\   Manager 可执行文件 + 待注入载荷 + 装箱脚本
#   build\<Configuration>\release\   （加 -Package）可直接发行的内容
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\build.ps1
#   powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Configuration Release -Package
#
# 依赖（本机已具备）：Visual Studio 2022 MSBuild、.NET Framework 4.8、XNA 4.0（装在 GAC）。
#
# 关于 XNA：管理器界面要解码游戏自带的 XNB 贴图，所以引用 XNA 4.0。
# 这些程序集在 GAC 里（C:\Windows\Microsoft.NET\assembly\GAC_32），**不在游戏目录里**，
# 所以 csproj 用简单名引用即可，脚本只需要校验它们在位，不需要往 lib\ 里拷任何东西。
# 早先的版本假设 XNA 在游戏目录，那会导致构建直接失败。

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Package,
    [string]$TerrariaDir = 'D:\Program Files (x86)\Steam\steamapps\common\Terraria'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Find-MSBuild {
    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) { return $path }
    }
    $command = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw '找不到 MSBuild.exe。请安装 Visual Studio 2022（含 .NET 桌面开发工作负载）。'
}

function Assert-XnaPresent {
    # XNA 是 x86 程序集，只装在 GAC_32 里。
    $gac = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_32'
    $needed = @(
        'Microsoft.Xna.Framework',
        'Microsoft.Xna.Framework.Game',
        'Microsoft.Xna.Framework.Graphics',
        'Microsoft.Xna.Framework.Xact'
    )
    $missing = @()
    foreach ($name in $needed) {
        if (-not (Test-Path -LiteralPath (Join-Path $gac $name))) { $missing += $name }
    }
    if ($missing.Count -gt 0) {
        throw ("GAC 里缺少 XNA 4.0 程序集：" + ($missing -join '、') + "。`n" +
               "请安装 XNA Framework Redistributable 4.0（或确认游戏本体已装好，它会一并安装 XNA）。")
    }
    Write-Host "XNA 4.0 已在 GAC 中。"
}

# 每个项目各自的目标平台不同，必须显式传给 MSBuild：
# 插件与管理器要跟 Terraria 同进程/同位数，所以是 x86；
# 核心库与装箱工具是纯托管代码，AnyCPU 即可。
$projects = @(
    @{ Path = 'src\TingYu.Core\TingYu.Core.csproj';         Platform = 'AnyCPU' },
    @{ Path = 'src\TingYu.Plugin\TingYu.Plugin.csproj';     Platform = 'x86'    },
    @{ Path = 'src\TingYu.Patcher\TingYu.Patcher.csproj';   Platform = 'AnyCPU' },
    @{ Path = 'src\TingYu.Manager\TingYu.Manager.csproj';   Platform = 'x86'    }
)

$msbuild = Find-MSBuild
Assert-XnaPresent

foreach ($project in $projects) {
    Write-Host "==> $($project.Path)  ($Configuration / $($project.Platform))"
    & $msbuild $project.Path /nologo /v:m `
        /p:Configuration=$Configuration /p:Platform=$($project.Platform)
    if ($LASTEXITCODE -ne 0) { throw "$($project.Path) 构建失败（exit $LASTEXITCODE）" }
}

$out = Join-Path $root "build\$Configuration"
$payload = Join-Path $out 'payload'
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload | Out-Null

# 载荷必须和 Terraria.exe 放在同一目录：CLR 在启动阶段探测注入目标程序集时，
# 只查 exe 所在目录和 GAC，放子目录会直接 FileNotFoundException 崩掉游戏。
$artifacts = @(
    "src\TingYu.Core\bin\$Configuration\net48\TingYu.Core.dll",
    "src\TingYu.Plugin\bin\$Configuration\net48\TingYu.Plugin.dll",
    "src\TingYu.Patcher\bin\$Configuration\net48\TingYu.Patcher.exe"
)
foreach ($file in $artifacts) {
    if (-not (Test-Path -LiteralPath $file)) { throw "缺少构建产物：$file" }
    Copy-Item -LiteralPath $file -Destination $payload -Force
}

# 管理器连同它的依赖（Mono.Cecil 由装箱项目传递过来）一起进载荷。
$managerDir = "src\TingYu.Manager\bin\$Configuration\net48"
Copy-Item -LiteralPath "$managerDir\TingYu.Manager.exe" -Destination $payload -Force
foreach ($name in @('TingYu.Manager.exe.config', 'Mono.Cecil.dll')) {
    $source = Join-Path $managerDir $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $payload -Force }
}

# 首次安装用的脚本：玩家只要双击这一个，不必手敲命令行。
foreach ($script in @('install.ps1', 'restore.ps1')) {
    $source = Join-Path $root $script
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $payload -Force }
}

Write-Host "载荷目录: $payload"

if ($Package) {
    $release = Join-Path $out 'release'
    if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    # 这里必须用 -Path：-LiteralPath 不展开通配符，`$payload\*` 会被当成字面文件名
    # 而静默什么都不复制——发行目录于是只剩文档，压缩包里没有程序。
    Copy-Item -Path "$payload\*" -Destination $release -Recurse -Force
    foreach ($doc in @('README.md', 'LICENSE', 'CHANGELOG.md')) {
        $source = Join-Path $root $doc
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $release -Force }
    }

    $zip = Join-Path $out "TingYu-$Configuration.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path "$release\*" -DestinationPath $zip
    Write-Host "发行目录: $release"
    Write-Host "发行压缩包: $zip"
}
