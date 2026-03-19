<#
.SYNOPSIS
    统计 Java Maven 构建日志中的编译错误，按类型数量降序输出
.PARAMETER LogFile
    日志文件路径（默认：C:\agl\v20260315\build-errors-8-part.log）
#>
param(
    [string]$LogFile = "C:\agl\v20260315\build-errors-8-part.log"
)

if (-not (Test-Path $LogFile)) {
    Write-Error "日志文件不存在: $LogFile"
    exit 1
}

#region 合并被終端折行截断的錯誤行
$lines = Get-Content $LogFile
$merged = [System.Collections.Generic.List[string]]::new()
$buf = ""

foreach ($line in $lines) {
    if ($line -match '^\[ERROR\] /') {
        if ($buf -ne "") { $merged.Add($buf) }
        $buf = $line
    } elseif ($buf -ne "" -and $line -match '^\[\d+,\d+\]') {
        # 上一行是被截断的路径行，这行是 [行,列] 部分，拼接起来
        $buf = $buf + $line
        $merged.Add($buf)
        $buf = ""
    } else {
        if ($buf -ne "") { $merged.Add($buf); $buf = "" }
    }
}
if ($buf -ne "") { $merged.Add($buf) }
#endregion

#region 提取并归一化错误类型
function Get-ErrorType([string]$line) {
    # 提取 [行,列] 后的错误描述
    if ($line -match '\[\d+,\d+\] (.+)$') {
        $msg = $Matches[1].Trim()
    } else {
        return "其他 [ERROR]"
    }

    # ── 按规则从最具体到最宽泛归一化 ──────────────────────────────────

    # 1. 找不到符号（最常见，保持原样）
    if ($msg -eq "找不到符号") { return "找不到符号" }

    # 2. 找不到合适的方法 / 构造器
    if ($msg -match '找不到合适的方法') { return "找不到合适的方法" }
    if ($msg -match '找不到合适的构造器') { return "找不到合适的构造器" }

    # 3. 不兼容的类型 - 细分
    if ($msg -match '^不兼容的类型:') {
        if ($msg -match '方法引用无效')          { return "不兼容的类型: 方法引用无效" }
        if ($msg -match 'lambda 表达式中的返回类型错误') { return "不兼容的类型: lambda 返回类型错误" }
        if ($msg -match '条件表达式中的类型错误') { return "不兼容的类型: 条件表达式类型错误" }
        if ($msg -match '无法推断类型变量')       { return "不兼容的类型: 无法推断类型变量" }
        if ($msg -match '推论变量.+具有不兼容的上限') { return "不兼容的类型: 推论变量具有不兼容的上限" }
        if ($msg -match '推断类型不符合上限')     { return "不兼容的类型: 推断类型不符合上限" }
        if ($msg -match '不存在类型变量.+的实例') { return "不兼容的类型: 不存在类型变量的实例（Stream→Iterable）" }
        if ($msg -match '无法转换为')             { return "不兼容的类型: 无法转换" }
        if ($msg -match 'try-with-resources')     { return "不兼容的类型: try-with-resources 不适用" }
        if ($msg -match '从\w+转换到\w+可能会有损失') { return "不兼容的类型: 精度损失（窄化转换）" }
        return "不兼容的类型（其他）"
    }

    # 4. 无法将 方法/构造器 应用到给定类型
    if ($msg -match '^无法将.+中的方法.+应用到给定类型')     { return "无法将方法应用到给定类型" }
    if ($msg -match '^无法将.+中的构造器.+应用到给定类型')   { return "无法将构造器应用到给定类型" }
    if ($msg -match '^无法将接口.+中的方法.+应用到给定类型') { return "无法将方法应用到给定类型" }

    # 5. 不是公共的，无法从外部程序包访问
    if ($msg -match '不是公共的; 无法从外部程序包中对其进行访问') { return "构造器/方法不是公共的，包外不可访问" }

    # 6. 是抽象的；无法实例化
    if ($msg -match '是抽象的; 无法实例化') { return "抽象类/接口无法实例化" }

    # 7. 不是抽象的，未覆盖抽象方法
    if ($msg -match '不是抽象的, 并且未覆盖') { return "类不是抽象的，但未覆盖抽象方法" }

    # 8. 方法无法实现接口方法（返回类型不兼容）
    if ($msg -match '无法实现.+返回类型.+与.+不兼容') { return "方法无法实现接口方法（返回类型不兼容）" }
    if ($msg -match '中的\S+无法实现\S+中的')          { return "方法无法实现接口方法" }

    # 9. 名称冲突
    if ($msg -match '^名称冲突') { return "名称冲突（签名相同但互不覆盖）" }

    # 10. 无法安全地转换（未检查转换）
    if ($msg -match '无法安全地转换为') { return "未经检查的类型转换" }

    # 11. 创建泛型数组
    if ($msg -eq "创建泛型数组") { return "创建泛型数组" }

    # 12. 意外的类型
    if ($msg -eq "意外的类型") { return "意外的类型" }

    # 13. 无法推断本地变量类型
    if ($msg -match '^无法推断本地变量.+的类型') { return "无法推断本地变量的类型" }

    # 14. 无法为 final 变量赋值
    if ($msg -match '^无法为 final 变量.+分配值') { return "无法为 final 变量分配值" }

    # 15. 变量已在作用域内定义
    if ($msg -match '已在方法.+中定义了变量') { return "变量已在方法中重复定义" }

    # 16. 程序包不存在
    if ($msg -match '^程序包.+不存在') { return "程序包不存在" }

    # 17. for-each 不适用
    if ($msg -match 'for-each 不适用于表达式类型') { return "for-each 不适用于表达式类型" }

    # 18. 无法取消引用原始类型
    if ($msg -match '^无法取消引用') { return "无法取消引用原始类型" }

    # 19. 无法为 final 变量赋值
    if ($msg -match '^无法为 final 变量') { return "无法为 final 变量分配值" }

    # 20. 访问控制
    if ($msg -match '是 private 访问控制') { return "访问控制冲突（private）" }

    # 21. 基元模式预览功能
    if ($msg -match '基元模式.*预览功能') { return "基元模式是预览功能（需启用预览）" }

    # 22. 其他
    return $msg
}
#endregion

#region 统计
$errorCounts = @{}
foreach ($line in $merged) {
    $type = Get-ErrorType $line
    if ($errorCounts.ContainsKey($type)) {
        $errorCounts[$type]++
    } else {
        $errorCounts[$type] = 1
    }
}
#endregion

#region 输出
$total = $merged.Count
$typeCount = $errorCounts.Count
$sorted = $errorCounts.GetEnumerator() | Sort-Object Value -Descending

$sep = "=" * 72
Write-Host "`n$sep" -ForegroundColor Cyan
Write-Host ("  编译错误类型统计  |  日志: {0}" -f (Split-Path $LogFile -Leaf)) -ForegroundColor Cyan
Write-Host $sep -ForegroundColor Cyan
Write-Host ("{0,4}  {1,-48}  {2,6}  {3}" -f "排名", "错误类型", "数量", "占比")
Write-Host ("-" * 72)

$rank = 1
foreach ($entry in $sorted) {
    $pct = "{0:P1}" -f ($entry.Value / $total)
    Write-Host ("{0,4}. {1,-48}  {2,6}  {3}" -f $rank, $entry.Key, $entry.Value, $pct)
    $rank++
}

Write-Host $sep -ForegroundColor Cyan
Write-Host ("  合计：{0} 个错误，共 {1} 种类型" -f $total, $typeCount) -ForegroundColor Yellow
Write-Host $sep -ForegroundColor Cyan
#endregion
