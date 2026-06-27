# scripts/verify-goto-eliminator.ps1
# 复制 D:\csharpxml -> 临时目录 -> 转换副本 -> dotnet build/test -> 断言源只读 -> 清理
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\verify-goto-eliminator.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\verify-goto-eliminator.ps1 -SkipSha256
#
# -SkipSha256：跳过源目录 SHA256 基线/重检（迭代调试期间加速）。完全转换成功后去掉此参数做最终只读证明。

param(
    [switch]$SkipSha256
)

$ErrorActionPreference = 'Stop'
$repo = 'D:\code\cs2j'
$src = 'D:\csharpxml'
$tmp = "D:\temp\cs2j_verify_" + [guid]::NewGuid().ToString('N')

function Get-TreeHashes($root) {
    Get-ChildItem -Recurse -File $root |
        Where-Object { $_.FullName -notmatch '\\(\.git|bin|obj)\\' -and $_.FullName -notmatch '\\(\.git|bin|obj)$' } |
        ForEach-Object {
            [PSCustomObject]@{ Path = $_.FullName; Hash = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash }
        }
}

# 复制时排除 .git/bin/obj —— 这些目录可能含大量构建产物，复制浪费时间且对转换无意义。
function Copy-TreeExcludingBuildDirs($source, $destination) {
    # 不用 robocopy（输出非零退出码即使成功），改用 .NET API 递归复制 + 跳过模式。
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $stack = New-Object System.Collections.Stack
    $stack.Push(@{ Src = $source; Dst = $destination })
    while ($stack.Count -gt 0) {
        $top = $stack.Pop()
        $srcDir = $top.Src
        $dstDir = $top.Dst
        New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
        # 复制文件
        foreach ($file in [System.IO.Directory]::EnumerateFiles($srcDir)) {
            $dstFile = [System.IO.Path]::Combine($dstDir, [System.IO.Path]::GetFileName($file))
            [System.IO.File]::Copy($file, $dstFile, $true)
        }
        # 递归子目录，跳过 .git/bin/obj
        foreach ($dir in [System.IO.Directory]::EnumerateDirectories($srcDir)) {
            $name = [System.IO.Path]::GetFileName($dir)
            if ($name -eq '.git' -or $name -eq 'bin' -or $name -eq 'obj') { continue }
            $stack.Push(@{ Src = $dir; Dst = ([System.IO.Path]::Combine($dstDir, $name)) })
        }
    }
}

$before = $null
if (-not $SkipSha256) {
    Write-Host "Recording source SHA256 baseline (excluding .git/bin/obj)..."
    $before = Get-TreeHashes $src
} else {
    Write-Host "Skipping source SHA256 baseline (-SkipSha256)."
}

Write-Host "Copying $src -> $tmp (excluding .git/bin/obj)"
Copy-TreeExcludingBuildDirs $src $tmp

try {
    Write-Host "Running eliminate-goto on copy..."
    & dotnet run --project "$repo\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj" -- `
        eliminate-goto -s $tmp -d $tmp --verbose
    if ($LASTEXITCODE -ne 0) { throw "eliminate-goto failed (exit $LASTEXITCODE)" }

    Write-Host "--- dotnet build ---"
    Set-Location $tmp
    dotnet build csharpxml.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

    Write-Host "--- dotnet test ---"
    dotnet test csharpxml.sln -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "test failed (exit $LASTEXITCODE)" }

    Write-Host "--- self-check: no goto/label in transformed .cs ---"
    $remaining = Get-ChildItem -Recurse -File $tmp -Filter *.cs | Select-String -Pattern '\bgoto\b' -SimpleMatch:$false
    # 注：注释/字符串里的 goto 可能误报；用 Roslyn 自检更准。此处仅粗检。
    if ($remaining) { Write-Warning "Possible goto remnants (verify manually): $($remaining.Count)" }

    Write-Host "VERIFY OK"
}
finally {
    Set-Location $repo
    if (-not $SkipSha256) {
        Write-Host "Re-checking source SHA256 (read-only proof)..."
        $after = Get-TreeHashes $src
        $diff = Compare-Object $before $after -Property Path, Hash
        if ($diff) { throw "SOURCE MODIFIED! Differences:`n$($diff | Out-String)" }
        Write-Host "Source unchanged."
    } else {
        Write-Host "Skipping source SHA256 re-check (-SkipSha256)."
    }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
