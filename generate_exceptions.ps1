# generate_exceptions.ps1
# 通过反射获取 System.Exception 的所有 public 子类，生成 Java 代码并修改映射

$ErrorActionPreference = "Continue"
$workspace = "d:/code/cs2j"
$javaDir = "$workspace/java/csharptojava-compat/src/main/java/io/github/ningpp/compat"
$mappingFile = "$workspace/config/TypeMappings.json"

# 1. 反射获取所有 Exception 子类
Write-Host "正在通过反射获取 .NET Exception 子类..." -ForegroundColor Cyan

$allExceptionTypes = [System.Collections.Generic.List[System.Type]]::new()

# 获取当前已加载 + 可加载的程序集
$assemblies = [System.Collections.Generic.List[System.Reflection.Assembly]]::new()
$loadedAsms = [System.AppDomain]::CurrentDomain.GetAssemblies()

Write-Host "已加载 $($loadedAsms.Count) 个程序集，正在扫描..." -ForegroundColor Gray

foreach ($asm in $loadedAsms) {
    try {
        $types = $asm.GetExportedTypes()
        foreach ($t in $types) {
            if ($t.IsPublic -and $t.IsClass -and -not $t.IsAbstract -and [System.Exception].IsAssignableFrom($t)) {
                $allExceptionTypes.Add($t)
            }
        }
    } catch { }
}

Write-Host "反射获取到 $($allExceptionTypes.Count) 个 Exception 子类" -ForegroundColor Green

# 按命名空间分组显示
$grouped = $allExceptionTypes | Group-Object Namespace | Sort-Object Name
Write-Host "按命名空间分布：" -ForegroundColor Yellow
foreach ($g in $grouped) {
    Write-Host "  $($g.Name): $($g.Count) 个" -ForegroundColor Gray
}

# 2. 读取现有映射
Write-Host "`n读取现有映射..." -ForegroundColor Cyan
$mappingJson = Get-Content $mappingFile -Encoding UTF8 -Raw
# 提取已有的 csharp 映射（简单正则，不依赖 JSON 深度解析）
$existingCsharp = [System.Collections.Generic.HashSet[string]]::new()
[regex]::Matches($mappingJson, '"csharp":\s*"([^"]+)"') | ForEach-Object {
    [void]$existingCsharp.Add($_.Groups[1].Value)
}

Write-Host "现有映射中有 $($existingCsharp.Count) 个 C# 类型" -ForegroundColor Green

# 3. 需要生成 Java 类的异常（没有标准 Java 对应物的）
# 这些可以直接映射到 Java 标准异常的就不需要生成类
$standardJavaMap = @{
    "System.Exception"                   = "Exception"
    "System.ArgumentException"           = "IllegalArgumentException"
    "System.ArgumentNullException"       = "NullPointerException"
    "System.ArgumentOutOfRangeException" = "IndexOutOfBoundsException"
    "System.InvalidOperationException"   = "IllegalStateException"
    "System.NotImplementedException"     = "UnsupportedOperationException"
    "System.NullReferenceException"      = "NullPointerException"
    "System.IndexOutOfRangeException"    = "IndexOutOfBoundsException"
    "System.ArrayTypeMismatchException"  = "ArrayStoreException"
    "System.DivideByZeroException"       = "ArithmeticException"
    "System.OverflowException"           = "ArithmeticException"
    "System.NotSupportedException"       = "UnsupportedOperationException"
    "System.InvalidCastException"        = "ClassCastException"
    "System.FormatException"             = "IllegalArgumentException"
    "System.IO.IOException"              = "IOException"
    "System.IO.FileNotFoundException"    = "FileNotFoundException"
    "System.OperationCanceledException"  = "CancellationException"
    "System.TimeoutException"            = "TimeoutException"
    "System.TypeLoadException"           = "ClassNotFoundException"
    "System.MissingFieldException"       = "NoSuchFieldException"
    "System.MemberAccessException"       = "IllegalAccessException"
}

$toGenerate = [System.Collections.Generic.List[System.Type]]::new()
$toMapOnly = [System.Collections.Generic.List[object]]::new()

foreach ($t in $allExceptionTypes) {
    $fullName = $t.FullName
    if ($existingCsharp.Contains($fullName)) {
        continue  # 已有映射，跳过
    }
    if ($standardJavaMap.ContainsKey($fullName)) {
        $toMapOnly.Add(@{ csharp = $fullName; java = $standardJavaMap[$fullName]; import = "java.lang.$($standardJavaMap[$fullName])" })
    } else {
        $toGenerate.Add($t)
    }
}

Write-Host "`n需要生成 Java 类的异常：$($toGenerate.Count) 个" -ForegroundColor Yellow
Write-Host "只需要添加映射的异常：$($toMapOnly.Count) 个" -ForegroundColor Yellow

# 4. 生成 Java 异常类
# 先构建继承关系：需要知道每个异常的直接基类
Write-Host "`n生成 Java 异常类..." -ForegroundColor Cyan

# 获取所有需要生成的类型的基类，判断是否需要自定义父类
$javaClassNames = [System.Collections.Generic.HashSet[string]]::new()
foreach ($t in $toGenerate) {
    $javaName = $t.Name  # 简单取 Name，处理嵌套类的情况
    # 处理泛型后缀
    if ($javaName -match '`\d+') {
        $javaName = [regex]::Replace($javaName, '`\d+', '')
    }
    [void]$javaClassNames.Add($javaName)
}

foreach ($t in $toGenerate) {
    $csharpName = $t.FullName
    $javaName = $t.Name
    if ($javaName -match '`\d+') {
        $javaName = [regex]::Replace($javaName, '`\d+', '')
    }

    # 确定 Java 父类
    $baseType = $t.BaseType
    $javaSuper = "RuntimeException"  # 默认
    $javaImport = ""

    if ($baseType -and $baseType -ne [System.Object]) {
        $baseName = $baseType.Name
        if ($baseName -match '`\d+') {
            $baseName = [regex]::Replace($baseName, '`\d+', '')
        }
        if ($baseType.FullName -eq "System.SystemException") {
            $javaSuper = "SystemException"
            $javaImport = "io.github.ningpp.compat.SystemException"
        } elseif ($baseType.FullName -eq "System.Exception") {
            $javaSuper = "RuntimeException"
            $javaImport = "java.lang.RuntimeException"
        } elseif ($javaClassNames.Contains($baseName) -or $baseName -eq "SystemException") {
            # 基类也是需要生成的自定义异常
            $javaSuper = $baseName
            $javaImport = "io.github.ningpp.compat.$baseName"
        } elseif ($standardJavaMap.ContainsKey($baseType.FullName)) {
            $javaSuper = $standardJavaMap[$baseType.FullName]
            if ($javaSuper -like "*Exception" -and -not $javaSuper.Contains(".")) {
                $javaImport = "java.lang.$javaSuper"
            }
        } else {
            $javaSuper = "RuntimeException"
            $javaImport = "java.lang.RuntimeException"
        }
    }

    $filePath = "$javaDir/$javaName.java"
    if (Test-Path $filePath) {
        Write-Host "  跳过（已存在）: $javaName" -ForegroundColor Gray
        continue
    }

    $importLine = if ($javaImport) { "import $javaImport;`n" } else { "" }

    $content = @"
package io.github.ningpp.compat;

/** Replacement for $csharpName (generated by CSharpToJava converter). */
public class $javaName extends $javaSuper {
    public $javaName() { super(); }
    public $javaName(String message) { super(message); }
    public $javaName(String message, Throwable cause) { super(message, cause); }
}
"@

    # 用无 BOM 的 UTF-8 编码写入
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($filePath, $content, $utf8NoBom)
    Write-Host "  生成: $javaName extends $javaSuper  ($csharpName)" -ForegroundColor Green
}

# 5. 修改映射文件
Write-Host "`n修改映射文件..." -ForegroundColor Cyan

# 找到异常映射的插入位置（在 System.Exception 映射附近）
# 读取映射文件为行数组
$lines = Get-Content $mappingFile -Encoding UTF8

# 找到最后一个异常映射的位置，在其后插入新映射
$lastExceptionLine = -1
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
    if ($lines[$i] -match '"csharp":\s*"System\.\w*Exception"') {
        # 找到这个映射块的结束（下一个 } 或文件结束）
        $lastExceptionLine = $i
        break
    }
}

# 更简单的方法：在 OutOfMemoryException 映射后插入
$insertIndex = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'System\.OutOfMemoryException') {
        # 找到这个映射块的结束
        for ($j = $i; $j -lt $lines.Count; $j++) {
            if ($lines[$j] -match '^\s*\},?\s*$') {
                $insertIndex = $j + 1
                break
            }
        }
        break
    }
}

if ($insertIndex -eq -1) {
    Write-Host "  找不到插入位置，请手动添加映射" -ForegroundColor Red
} else {
    # 生成新的映射条目
    $newMappings = [System.Collections.Generic.List[string]]::new()

    foreach ($item in $toMapOnly) {
        $csharp = $item.csharp
        $java = $item.java
        $import = $item.import
        $entry = @"
                        {
                            "csharp":  "$csharp",
                            "java":  "$java",
                            "imports":  [
                                            "$import"
                                        ]
                        },
"@
        $newMappings.Add($entry)
        Write-Host "  添加映射: $csharp -> $java" -ForegroundColor Green
    }

    foreach ($t in $toGenerate) {
        $csharp = $t.FullName
        $javaName = $t.Name
        if ($javaName -match '`\d+') {
            $javaName = [regex]::Replace($javaName, '`\d+', '')
        }
        $entry = @"
                        {
                            "csharp":  "$csharp",
                            "java":  "$javaName",
                            "imports":  [
                                            "io.github.ningpp.compat.$javaName"
                                        ]
                        },
"@
        $newMappings.Add($entry)
        Write-Host "  添加映射: $csharp -> $javaName" -ForegroundColor Green
    }

    # 插入新映射
    $before = $lines[0..($insertIndex - 1)]
    $after = $lines[$insertIndex..($lines.Count - 1)]
    $newLines = $before + $newMappings + $after
    # 无 BOM 写入 JSON
    [System.IO.File]::WriteAllLines($mappingFile, [string[]]$newLines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  映射文件已更新，插入了 $($newMappings.Count) 个新映射" -ForegroundColor Green
}

Write-Host "`n完成！" -ForegroundColor Cyan
