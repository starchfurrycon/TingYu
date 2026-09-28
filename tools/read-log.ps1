param(
    [string]$Root = 'D:\personal tasks\modding\听雨的声音\_recon\gametest',
    [int]$Tail = 120
)

# 读日志用。必须显式按 UTF-8 解码：Windows PowerShell 5.1 的 Get-Content
# 默认按 ANSI 读，会把 UTF-8 的中文变成乱码。
$data = Join-Path $Root 'TingYu'
foreach ($name in @('boot.log', 'tingyu.log', 'status.txt')) {
    $path = Join-Path $data $name
    Write-Host "=== $name ==="
    if (-not (Test-Path $path)) { Write-Host '(不存在)'; continue }
    $lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
    if ($name -ne 'status.txt' -and $lines.Count -gt $Tail) {
        Write-Host "(共 $($lines.Count) 行，显示最后 $Tail 行)"
        $lines = $lines[($lines.Count - $Tail)..($lines.Count - 1)]
    }
    $lines | ForEach-Object { Write-Host $_ }
}
