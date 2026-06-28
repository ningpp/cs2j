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
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = 'D:\csharpxml'
$tmp = "D:\temp\cs2j_verify_" + [guid]::NewGuid().ToString('N')

function ConvertTo-ProcessArgument($argument) {
    $text = [string]$argument
    if ($text -ne '' -and $text -notmatch '[\s"]') {
        return $text
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($ch in $text.ToCharArray()) {
        if ($ch -eq '\') {
            $backslashes++
            continue
        }

        if ($ch -eq '"') {
            [void]$builder.Append('\' * (($backslashes * 2) + 1))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append('\' * $backslashes)
            $backslashes = 0
        }
        [void]$builder.Append($ch)
    }

    if ($backslashes -gt 0) {
        [void]$builder.Append('\' * ($backslashes * 2))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Join-ProcessArguments([string[]]$arguments) {
    return ($arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
}

function Invoke-WithTimeout($name, $filePath, [string[]]$arguments, [int]$timeoutSeconds) {
    Write-Host "> $name (timeout: ${timeoutSeconds}s)"
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $filePath
    $startInfo.Arguments = Join-ProcessArguments $arguments
    $startInfo.WorkingDirectory = (Get-Location).ProviderPath
    $startInfo.UseShellExecute = $false

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()
    if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        finally {
            throw "$name timed out after ${timeoutSeconds}s"
        }
    }

    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        throw "$name failed (exit $($process.ExitCode))"
    }
}

function Invoke-DotnetWithTimeout($name, [string[]]$arguments, [int]$timeoutSeconds) {
    Invoke-WithTimeout $name 'dotnet' $arguments $timeoutSeconds
}

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

function Assert-NoGotoSyntax($root) {
    $scan = "D:\temp\cs2j_verify_goto_scan_" + [guid]::NewGuid().ToString('N')
    try {
        Invoke-DotnetWithTimeout 'create goto scanner project' @('new', 'console', '-o', $scan, '--framework', 'net10.0', '--no-restore') 120
        @'
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var rootDir = args[0];
var remaining = new List<string>();
foreach (var file in Directory.EnumerateFiles(rootDir, "*.cs", SearchOption.AllDirectories))
{
    var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
    var gotos = root.DescendantNodes().OfType<GotoStatementSyntax>().Count();
    var labels = root.DescendantNodes().OfType<LabeledStatementSyntax>().Count();
    if (gotos != 0 || labels != 0)
        remaining.Add($"{file}|goto={gotos}|label={labels}");
}

Console.WriteLine($"REMAINING={remaining.Count}");
foreach (var item in remaining.Take(100))
    Console.WriteLine(item);
return remaining.Count == 0 ? 0 : 2;
'@ | Set-Content -Path (Join-Path $scan 'Program.cs') -Encoding UTF8
        $project = (Get-ChildItem -Path $scan -Filter *.csproj | Select-Object -First 1).FullName
        Invoke-DotnetWithTimeout 'restore goto scanner dependencies' @('add', $project, 'package', 'Microsoft.CodeAnalysis.CSharp', '--version', '4.12.0') 300
        Invoke-DotnetWithTimeout 'run Roslyn goto/label scan' @('run', '--project', $project, '--', $root) 300
    }
    finally {
        Remove-Item -Recurse -Force $scan -ErrorAction SilentlyContinue
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
    Invoke-DotnetWithTimeout 'run eliminate-goto' @(
        'run',
        '--project',
        "$repo\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj",
        '--',
        'eliminate-goto',
        '-s',
        $tmp,
        '-d',
        $tmp,
        '--verbose') 900

    Write-Host "--- dotnet build ---"
    Set-Location $tmp
    Invoke-DotnetWithTimeout 'build transformed csharpxml' @('build', 'csharpxml.sln', '-c', 'Release') 900

    Write-Host "--- dotnet test ---"
    Invoke-DotnetWithTimeout 'test transformed csharpxml' @('test', 'csharpxml.sln', '-c', 'Release', '--no-build') 900

    Write-Host "--- self-check: no goto/label in transformed .cs ---"
    Assert-NoGotoSyntax $tmp

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
