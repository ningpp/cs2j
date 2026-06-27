# scripts/verify-goto-eliminator-baseline.ps1
# 确认未转换的 D:\csharpxml 今日可 build + test（红基线）。
# 绝不修改 D:\csharpxml；在临时副本上运行。
$ErrorActionPreference = 'Stop'
$src = 'D:\csharpxml'
$tmp = "D:\temp\cs2j_baseline_" + [guid]::NewGuid().ToString('N')
Write-Host "Copying $src -> $tmp"
Copy-Item -Recurse -Path $src -Destination $tmp
try {
    Set-Location $tmp
    Write-Host '--- dotnet build ---'
    dotnet build csharpxml.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
    Write-Host '--- dotnet test ---'
    dotnet test csharpxml.sln -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "test failed (exit $LASTEXITCODE)" }
    Write-Host 'BASELINE OK'
}
finally {
    Set-Location 'D:\code\cs2j'
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
