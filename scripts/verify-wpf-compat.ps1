# scripts/verify-wpf-compat.ps1
# 验证 dotnet.wpf.swing 兼容类与真实 .NET 6 WPF 在相同输入下的输出一致（双向对比）。
#
# 流程：
#   1. dotnet build tools/WpfCompatBaseline (net6.0-windows) 并用 .NET 6 运行时执行 → baseline.txt
#   2. mvn compile java/csharptojava-compat → target/classes
#   3. javac tools/WpfCompatJavaCheck/WpfCheck.java → 执行 → jcheck.txt
#   4. diff 两边输出；仅允许异常类名差异（ArgumentException vs IllegalArgumentException）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\verify-wpf-compat.ps1

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tmp = "C:\Users\15097\AppData\Local\Temp\opencode"
$baselineProj = Join-Path $repo 'tools\WpfCompatBaseline\WpfCompatBaseline.csproj'
$baselineDll = Join-Path $repo 'tools\WpfCompatBaseline\bin\Release\net6.0-windows\WpfCompatBaseline.dll'
$compatClasses = Join-Path $repo 'java\csharptojava-compat\target\classes'
$checkSrc = Join-Path $repo 'tools\WpfCompatJavaCheck\WpfCheck.java'
$checkOut = Join-Path $tmp 'wpcheck'

$baselineTxt = Join-Path $tmp 'baseline.txt'
$jcheckTxt = Join-Path $tmp 'jcheck.txt'

Write-Host '== 1/4 build + run .NET 6 WPF baseline =='
dotnet build $baselineProj -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'baseline build failed' }
dotnet $baselineDll 2>&1 | Out-File -Encoding utf8 $baselineTxt

Write-Host '== 2/4 compile compat classes =='
Push-Location (Join-Path $repo 'java\csharptojava-compat')
try { mvn -q compile | Out-Null } finally { Pop-Location }
if (-not (Test-Path (Join-Path $compatClasses 'dotnet\wpf\swing\Point.class'))) {
    throw 'compat classes not compiled'
}

Write-Host '== 3/4 compile + run Java check =='
New-Item -ItemType Directory -Force -Path $checkOut | Out-Null
javac -encoding UTF-8 -cp $compatClasses -d $checkOut $checkSrc
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }
& "$env:JAVA_HOME\bin\java" -cp "$compatClasses;$checkOut" WpfCheck 2>&1 | Out-File -Encoding utf8 $jcheckTxt

Write-Host '== 4/4 diff (only ArgumentException name difference allowed) =='
$baselineLines = Get-Content $baselineTxt | ForEach-Object { $_.Trim() }
$jcheckLines = Get-Content $jcheckTxt | ForEach-Object { $_.Trim() }

function Normalize-Line([string]$line) {
    # The only acceptable cross-runtime difference is the exception class name
    # (System.ArgumentException vs java.lang.IllegalArgumentException) produced
    # by Rect(negative) ctors. Normalize both to a common token and strip the BOM.
    # Use word boundaries so IllegalArgumentException is NOT double-mutated.
    $line = $line -replace 'IllegalArgumentException', 'XArgumentException'
    $line = $line -replace '(?<!X)ArgumentException', 'XArgumentException'
    $line = $line.TrimStart([char]0xFEFF)
    return $line
}

$max = [Math]::Max($baselineLines.Count, $jcheckLines.Count)
$mismatches = @()
for ($i = 0; $i -lt $max; $i++) {
    $a = if ($i -lt $baselineLines.Count) { Normalize-Line $baselineLines[$i] } else { '<missing>' }
    $b = if ($i -lt $jcheckLines.Count) { Normalize-Line $jcheckLines[$i] } else { '<missing>' }
    if ($a -ne $b) {
        $mismatches += ('line {0}: .NET = "{1}" | Java = "{2}"' -f ($i + 1), $a, $b)
    }
}

if ($mismatches.Count -gt 0) {
    Write-Error 'Compat output differs from the .NET 6 WPF baseline:'
    $mismatches | Select-Object -First 40 | ForEach-Object { Write-Host $_ }
    exit 1
}
Write-Host ''
Write-Host '[OK] WPF compat classes match the .NET 6 WPF outputs (only ArgumentException naming differs).'
exit 0
