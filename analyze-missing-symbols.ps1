<#
.SYNOPSIS
    细化统计日志中"找不到符号"错误，按符号种类 + 方法/变量名归类，降序输出
.PARAMETER LogFile
    日志文件路径
#>
param(
    [string]$LogFile = "C:\agl\v20260315\build-errors-8-part.log"
)

if (-not (Test-Path $LogFile)) { Write-Error "日志文件不存在: $LogFile"; exit 1 }

# ── 1. 提取所有"找不到符号"块的 符号/位置 信息 ───────────────────────────
$lines    = Get-Content $LogFile
$records  = [System.Collections.Generic.List[hashtable]]::new()
$inBlock  = $false
$curKind  = $null; $curName = $null

foreach ($line in $lines) {
    if ($line -match '^\[ERROR\] /.+找不到符号') {
        $inBlock = $true; $curKind = $null; $curName = $null
    } elseif ($inBlock) {
        if ($line -match '^\[ERROR\]|^\[INFO\]|^\[WARNING\]') {
            $inBlock = $false
        } elseif ($line -match '符号:\s+(变量|方法|类)\s+(.+)') {
            $curKind = $Matches[1]; $curName = $Matches[2].Trim()
        } elseif ($line -match '位置:\s+(.+)' -and $curKind) {
            $records.Add(@{ Kind=$curKind; Name=$curName; Loc=$Matches[1].Trim() })
            $curKind = $null; $curName = $null
        } elseif ($curKind -and $line.Trim() -ne "") {
            $curName = $curName + $line.Trim()   # 追加被折行截断的部分
        }
    }
}

# ── 2. 归一化，打上语义标签 ────────────────────────────────────────────────
function Get-Category([hashtable]$r) {
    $kind = $r.Kind
    $name = $r.Name
    $loc  = $r.Loc

    # 提取方法名（去掉参数列表）
    $fullMethodName = if ($name -match '^(\w+)\(') { $Matches[1] } else { $name }

    if ($kind -eq "方法") {
        switch -Regex ($fullMethodName) {
            '^apply$'              { return "[方法] apply() —— 索引器 [] 未正确转换（应用 Func 委托/索引属性）" }
            '^stream$'             { return "[方法] stream() —— 对非标准集合调用 .stream()（IEnumerable→Stream 映射缺失）" }
            '^put$'                { return "[方法] put() —— Map.put() 调用上下文错误（接收者不是 Map）" }
            '^getCurrent$'         { return "[方法] getCurrent() —— IEnumerator.Current 属性未正确转换" }
            '^sort$'               { return "[方法] sort() —— 排序方法调用不正确（应为 Collections.sort / Comparator）" }
            '^Format$'             { return "[方法] Format() —— String.Format() 未转换为 String.format()" }
            '^notEquals$'          { return "[方法] notEquals() —— != 运算符错误地转为 notEquals()（应用 !.equals()）" }
            '^valueEquals$'        { return "[方法] valueEquals() —— 值类型 Equals() 未正确映射" }
            '^copyTo$'             { return "[方法] copyTo() —— Array.CopyTo() 未转换（应用 System.arraycopy）" }
            '^copy$'               { return "[方法] copy() —— Array 拷贝方法未转换（应用 System.arraycopy / Arrays.copyOf）" }
            '^reverse$'            { return "[方法] reverse() —— 反转方法调用不正确（应为 Collections.reverse）" }
            '^collect$'            { return "[方法] collect() —— Stream.collect() 调用在非流对象上" }
            '^writeLine$'          { return "[方法] writeLine() —— Console.WriteLine() 未转换为 System.out.println()" }
            '^referenceEquals$'    { return "[方法] referenceEquals() —— Object.ReferenceEquals() 未转换（应用 ==）" }
            '^IsNullOrEmpty$'      { return "[方法] IsNullOrEmpty() —— String.IsNullOrEmpty() 未转换（应用 == null || .isEmpty()）" }
            '^trimStart$'          { return "[方法] trimStart() —— String.TrimStart() 未转换（Java 应用 stripLeading()）" }
            '^Join$'               { return "[方法] Join() —— String.Join() 未转换（应用 String.join()）" }
            '^createInstance$'     { return "[方法] createInstance() —— Activator.CreateInstance 泛型实例化未转换" }
            '^setValue$'           { return "[方法] setValue() —— 属性 setter 未生成（转换遗漏 set 访问器）" }
            '^setId$'              { return "[方法] setId() / 其他 set*() —— 属性 setter 方法缺失" }
            '^set[A-Z]'            { return "[方法] set*() —— 属性 setter 方法缺失（C# 属性未生成对应 setter）" }
            '^getList$'            { return "[方法] getList() / 其他 get*() —— 属性 getter 方法缺失" }
            '^get[A-Z]'            { return "[方法] get*() —— 属性 getter 方法缺失（C# 属性未生成对应 getter）" }
            '^setZoomLevel$'       { return "[方法] set*() —— 属性 setter 方法缺失（C# 属性未生成对应 setter）" }
            '^exists$'             { return "[方法] exists() —— File.Exists() 未转换（应用 new File().exists()）" }
            '^write$'              { return "[方法] write() —— IO 方法未转换" }
            '^create$'             { return "[方法] create() —— 静态工厂方法缺失或未正确映射" }
            '^iterator$'           { return "[方法] iterator() —— 对非 Iterable 对象调用 .iterator()" }
            '^getValues$'          { return "[方法] getValues() —— 枚举/集合 values() 方法未正确映射" }
            '^getCapacity$'        { return "[方法] getCapacity() —— Capacity 属性 getter 缺失" }
            '^getLength$'          { return "[方法] getLength() —— Length 属性 getter 缺失（Java 用 .length 或 .size()）" }
            '^reset$'              { return "[方法] reset() —— IEnumerator.Reset() 未转换" }
            '^waitForExit$'        { return "[方法] waitForExit() —— Process.WaitForExit() 未转换" }
            '^notEquals$'          { return "[方法] notEquals() —— != 运算符错误地转为 notEquals()" }
            '^boxed$'              { return "[方法] boxed() —— Stream.boxed() 调用对象类型不匹配" }
            default {
                # 对剩余 setter/getter 兜底
                if ($fullMethodName -match '^set[A-Z]') { return "[方法] set*() —— 属性 setter 方法缺失（C# 属性未生成对应 setter）" }
                if ($fullMethodName -match '^get[A-Z]') { return "[方法] get*() —— 属性 getter 方法缺失（C# 属性未生成对应 getter）" }
                return "[方法] 其他缺失方法: $fullMethodName()"
            }
        }
    }

    if ($kind -eq "变量") {
        # Holder 变量 —— 转换器为 out/ref 参数生成的临时变量
        if ($name -match '^_\w+Holder$') {
            return "[变量] _*Holder —— 转换器为 out/ref 参数生成的 Holder 变量丢失（声明未正确插入）"
        }
        # 事件字段
        if ($name -match 'Event$|Changed$|Handler$' -or
            $name -match '^(layoutDoneEvent|beforeLayoutChangeEvent|ProgressChanged|AbortEvent)$') {
            return "[变量] 事件字段 —— C# 事件/委托字段未转换为 Java 字段或监听器接口"
        }
        # 静态工具类/命名空间级引用
        if ($name -match '^(Parallel|Environment|Regex|StringHelper|Debug|Trace|Math|Console|File|Path|Directory)$') {
            return "[变量] 静态类引用（Parallel/Environment/Regex 等）—— 未映射到 Java 对等类"
        }
        # C# 属性（驼峰或 Pascal）常见属性名
        if ($name -match '^(Name|Length|Count|Size|Value|IsEmpty|IsReadOnly|IsFixedSize|Capacity|NodeType|IsEmptyElement)$') {
            return "[变量] C# 属性作为字段访问 —— 属性未转换为 Java getter 方法"
        }
        # Anchor / 布局属性
        if ($name -match 'Anchor$|^(LeftAnchor|RightAnchor|TopAnchor|BottomAnchor|Lagrangian|Weight)$') {
            return "[变量] C# 布局/约束属性字段 —— 属性 getter/setter 未生成"
        }
        # 普通局部变量（全小写，非 Holder）
        if ($name -match '^[a-z]') {
            return "[变量] 局部变量找不到 —— 可能是 out/try-scope/var 变量作用域问题"
        }
        # 其他 Pascal 命名（C# 属性）
        return "[变量] C# 属性/字段 '$name' 未转换到 Java —— 可能是属性未生成 getter 或字段名映射错误"
    }

    if ($kind -eq "类") {
        return "[类] 类型引用找不到: $name"
    }

    return "其他"
}

# ── 3. 统计 ───────────────────────────────────────────────────────────────
$counts = @{}
foreach ($r in $records) {
    $cat = Get-Category $r
    if ($counts.ContainsKey($cat)) { $counts[$cat]++ } else { $counts[$cat] = 1 }
}

# ── 4. 输出 ───────────────────────────────────────────────────────────────
$total  = $records.Count
$sorted = $counts.GetEnumerator() | Sort-Object Value -Descending
$sep    = "=" * 90

Write-Host "`n$sep" -ForegroundColor Cyan
Write-Host ("  「找不到符号」细化统计  |  共 {0} 条 / {1} 种类别  |  {2}" -f $total, $counts.Count, (Split-Path $LogFile -Leaf)) -ForegroundColor Cyan
Write-Host $sep -ForegroundColor Cyan
Write-Host ("{0,4}  {1,-66}  {2,5}  {3}" -f "排名", "错误类别", "数量", "占比")
Write-Host ("-" * 90)

$rank = 1
foreach ($e in $sorted) {
    $pct = "{0,5:P1}" -f ($e.Value / $total)
    Write-Host ("{0,4}. {1,-66}  {2,5}  {3}" -f $rank, $e.Key, $e.Value, $pct)
    $rank++
}

Write-Host $sep -ForegroundColor Cyan

# ── 5. 按大类汇总 ─────────────────────────────────────────────────────────
$byKind = @{ "方法"=0; "变量"=0; "类"=0 }
foreach ($r in $records) {
    $byKind[$r.Kind] = $byKind[$r.Kind] + 1
}
Write-Host "`n  大类汇总:" -ForegroundColor Yellow
foreach ($k in "方法","变量","类") {
    $pct = "{0:P1}" -f ($byKind[$k] / $total)
    Write-Host ("    符号:{0}  {1,4} 条  ({2})" -f $k, $byKind[$k], $pct)
}
Write-Host $sep -ForegroundColor Cyan
