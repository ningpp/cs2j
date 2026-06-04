# verify.ps1
# 验证生成的异常类和映射

$workspace = "d:/code/cs2j"
$javaDir = "$workspace/java/csharptojava-compat/src/main/java/io/github/ningpp/compat"
$mappingFile = "$workspace/config/TypeMappings.json"

Write-Host "=== 验证结果 ===" -ForegroundColor Cyan

# 1. 验证 JSON 格式
Write-Host "`n1. 验证映射文件 JSON 格式..." -ForegroundColor Yellow
try {
    $json = Get-Content $mappingFile -Encoding UTF8 -Raw | ConvertFrom-Json
    Write-Host "   JSON 格式正确 ✓" -ForegroundColor Green
} catch {
    Write-Host "   JSON 格式错误: $_" -ForegroundColor Red
    exit 1
}

# 2. 检查重复映射
Write-Host "`n2. 检查重复映射..." -ForegroundColor Yellow
$content = Get-Content $mappingFile -Encoding UTF8 -Raw
$matches = [regex]::Matches($content, '"csharp":\s*"([^"]+)"')
$grouped = $matches | Group-Object { $_.Groups[1].Value }
$dupes = $grouped | Where-Object { $_.Count -gt 1 }
if ($dupes) {
    Write-Host "   发现重复映射：" -ForegroundColor Red
    $dupes | ForEach-Object { Write-Host "      $($_.Name): $($_.Count) 次" -ForegroundColor Yellow }
} else {
    Write-Host "   无重复映射 ✓" -ForegroundColor Green
}

# 3. 验证 SystemException 继承 RuntimeException
Write-Host "`n3. 验证 SystemException 继承关系..." -ForegroundColor Yellow
$sysEx = Get-Content "$javaDir/SystemException.java" -Encoding UTF8
if ($sysEx -match "extends RuntimeException") {
    Write-Host "   SystemException extends RuntimeException ✓" -ForegroundColor Green
} else {
    Write-Host "   SystemException 未继承 RuntimeException ✗" -ForegroundColor Red
}

# 4. 验证 OutOfMemoryException 继承 SystemException
Write-Host "`n4. 验证 OutOfMemoryException 继承关系..." -ForegroundColor Yellow
$oom = Get-Content "$javaDir/OutOfMemoryException.java" -Encoding UTF8
if ($oom -match "extends SystemException") {
    Write-Host "   OutOfMemoryException extends SystemException ✓" -ForegroundColor Green
} else {
    Write-Host "   OutOfMemoryException 未继承 SystemException ✗" -ForegroundColor Red
}

# 5. 验证映射：System.SystemException -> SystemException
Write-Host "`n5. 验证映射：System.SystemException..." -ForegroundColor Yellow
if ($content -match '"csharp":\s*"System\.SystemException".*?"java":\s*"SystemException"') {
    Write-Host "   System.SystemException -> SystemException ✓" -ForegroundColor Green
} else {
    Write-Host "   映射不正确 ✗" -ForegroundColor Red
}

# 6. 验证映射：System.OutOfMemoryException -> OutOfMemoryException
Write-Host "`n6. 验证映射：System.OutOfMemoryException..." -ForegroundColor Yellow
if ($content -match '"csharp":\s*"System\.OutOfMemoryException".*?"java":\s*"OutOfMemoryException"') {
    Write-Host "   System.OutOfMemoryException -> OutOfMemoryException ✓" -ForegroundColor Green
} else {
    Write-Host "   映射不正确 ✗" -ForegroundColor Red
}

# 7. 统计生成的异常类数量
Write-Host "`n7. 统计生成的异常类..." -ForegroundColor Yellow
$exFiles = Get-ChildItem "$javaDir/*.java" | Where-Object { $_.Name -notlike "*Holder" -and $_.Name -notlike "CSharp*" -and $_.Name -notlike "Decimal*" }
$exClasses = $exFiles | Where-Object {
    $c = Get-Content $_.FullName -Encoding UTF8
    $c -match "extends.*Exception"
}
Write-Host "   生成的总异常类数量: $($exClasses.Count)" -ForegroundColor Green

# 8. 检查继承 SystemException 的类
$extendsSE = $exClasses | Where-Object {
    $c = Get-Content $_.FullName -Encoding UTF8
    $c -match "extends SystemException"
}
Write-Host "   继承 SystemException 的类: $($extendsSE.Count) 个" -ForegroundColor Cyan

# 9. 检查继承 RuntimeException 的类
$extendsRE = $exClasses | Where-Object {
    $c = Get-Content $_.FullName -Encoding UTF8
    $c -match "extends RuntimeException"
}
Write-Host "   继承 RuntimeException 的类: $($extendsRE.Count) 个" -ForegroundColor Cyan

Write-Host "`n=== 验证完成 ===" -ForegroundColor Cyan
