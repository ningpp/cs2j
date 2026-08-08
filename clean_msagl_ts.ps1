# 删除 D:\msagl-ts-202608 下除 node_modules 以外的所有文件和文件夹
# 本脚本是 cs2j → MSAGL TS 转换流程的标准清理入口，禁止手工用 Remove-Item 替代。
$root = "D:\msagl-ts-202608"

if (-not (Test-Path $root)) {
    Write-Error "目录不存在: $root"
    exit 1
}

Get-ChildItem -Path $root -Force | Where-Object { $_.Name -ne "node_modules" } | ForEach-Object {
    Write-Host "删除: $($_.FullName)"
    Remove-Item -Path $_.FullName -Recurse -Force
}

Write-Host "完成。已删除除 node_modules 以外的所有内容。"
