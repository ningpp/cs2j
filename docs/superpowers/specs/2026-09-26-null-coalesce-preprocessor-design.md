# Null 合并预处理器（Null Coalesce Preprocessor）设计

日期：2026-09-26

全文统一命名：C# 侧预处理器为 `NullCoalesceRewriter`（Core 目录
`NullCoalesceRewriter/`），CLI 包装为 `ProjectNullCoalescePreprocessor`，
中间目录名 `.cs2j-null-coalesce-src`，CLI verb `null-coalesce`。

## 1. 背景与问题

C# 的 null 合并运算符 `??` 和 null 合并赋值运算符 `??=` 在 Java 中没有
直接等价物。主转换器需要为这两种运算符编写专门的转换逻辑，但这些逻辑散
落在各 transformer 中，既复杂又容易出错。

本预处理器在 C# → C# 层面把 `??` 和 `??=` 改写为等价的显式三元运算符和
if 赋值形式，使主转换器不再需要处理这两种运算符。改写后的 C# 必须仍然
是合法、可编译、行为等价的 C# 程序。

### 总体约束

1. **仅做 C# → C# 转换**，不涉及任何 C# → Java 或其它语言的代码生成。
2. **语义绝对等价**：改写前后程序的运行结果、副作用顺序、求值次数必须完
   全相同。这是最高优先级。
3. **左操作数只求值一次**：`??` / `??=` 的左操作数可能包含方法调用、属性
   读取、索引器访问等带副作用的表达式，必须借助临时变量保证只计算一次
   （与原运算符语义一致）。
4. **临时变量命名唯一**：统一使用前缀 `___t` 加递增数字（如 `___t1`、
   `___t2`），在当前作用域内保证不重名、不与已有标识符冲突。
5. **不改动无关代码**：只改写出现目标语法的语句；其余代码原样保留。
6. **保持可编译**：输出必须是合法的 C#，保留 `using`、命名空间、类型推
   断（`var`）、访问修饰符等。

## 2. 目标

- 新增预处理阶段 **null-coalesce**：把 C# 中所有 `??` 和 `??=` 运算符改写
  为等价的显式形式（三元运算符 / if + 赋值），保证语义绝对等价。
- 改写后残留的 C# 中不再出现 `??` / `??=` 语法，主转换器无需处理这两种
  运算符。
- 复用现有预处理器基础设施（`ProjectPreprocessSource`、
  `SupportedProjectFiltering`、`ProjectUnsignedTypePreprocessor.GetBasicReferences`），
  保证中间目录选择/拷贝行为一致。
- clean 文件字节级保真；改写产物重新解析无 Error。
- 完整接入 CLI（独立 verb + `convert-project` 串联），与
  `GotoEliminator` / `UnsignedTypeRewriter` / `LocalFunctionLift` 同款模式。

## 3. 转换规则

### 3.1 NullCoalescing（`??`）

将 `左侧 ?? 右侧` 改写为"先求左值存入临时变量，再用三元运算符判断"。

**一律使用临时变量**（不论左操作数是否为简单标识符），保证最大安全性。

通用模板：

```
T ___tN = <左侧表达式>;
<目标> = (___tN != null) ? ___tN : <右侧表达式>;
```

其中 `T` 是左操作数的静态类型（由 SemanticModel 推导），`<目标>` 是原本
接收 `??` 结果的接收者（变量声明 / 赋值目标 / 嵌套表达式位置）。临时变量
声明提升到最近包含语句之前。

**示例 1：变量声明**

```csharp
// before
var a = possiblyNull ?? defaultValue;

// after
var ___t1 = possiblyNull;
var a = (___t1 != null) ? ___t1 : defaultValue;
```

**示例 2：普通赋值 / 嵌套使用**

```csharp
// before
x.Y = GetValue() ?? fallback;
var z = (a ?? b) + (c ?? d);

// after
var ___t1 = GetValue();
x.Y = (___t1 != null) ? ___t1 : fallback;
var ___t2 = a;
var ___t3 = c;
var z = (___t2 != null ? ___t2 : b) + (___t3 != null ? ___t3 : d);
```

**示例 3：嵌套 `??`**

```csharp
// before — 三层嵌套
var r = a ?? b ?? c ?? d;

// after (自底向上处理，内层先)
var ___t1 = c;
var ___t2 = b;
var ___t3 = a;
var r = (___t3 != null) ? ___t3 : ((___t2 != null) ? ___t2 : ((___t1 != null) ? ___t1 : d));
```

**示例 4：return 语句**

```csharp
// before
return GetData() ?? fallback;

// after
var ___t1 = GetData();
return (___t1 != null) ? ___t1 : fallback;
```

**示例 5：方法参数**

```csharp
// before
Foo(GetValue() ?? fallback);

// after
var ___t1 = GetValue();
Foo((___t1 != null) ? ___t1 : fallback);
```

### 3.2 NullCoalescingAssign（`??=`）

将 `目标 ??= 表达式` 改写为"若目标为 null 则赋值"。

**关键修正：左操作数带副作用必须用临时变量。**

C# `??=` 运算符的语义：对左操作数求值一次（get），如果为 null 则对左操作
数求值作为赋值目标（set），共 get 一次 + set 一次。如果左操作数是索引器
或属性访问（如 `cache[key]`、`config.Timeout`），重复求值可能产生副作用
（key 重新计算、属性 getter 执行逻辑），必须用临时变量保证只求值一次。

**规则**：

1. **简单标识符**（局部变量 `a`、参数 `p` 等
   `IdentifierNameSyntax`）：不求值副作用，直接改写：

   ```csharp
   // before: a ??= GetDefault();
   // after:  if (a == null) { a = GetDefault(); }
   ```

2. **成员访问 / 索引器**（`config.Timeout`、`cache[key]`、
   `obj.Prop.Field` 等）：左操作数可能含副作用，**必须用临时变量**保证
   只 get 一次：

   ```csharp
   // before: config.Timeout ??= 30;
   // after:
   var ___t1 = config.Timeout;
   if (___t1 == null) { config.Timeout = 30; }
   ```

   ```csharp
   // before: cache[key] ??= Compute();
   // after:
   var ___t1 = cache[key];
   if (___t1 == null) { cache[key] = Compute(); }
   ```

   语义分析：临时变量 `___t1` 存储了 `cache[key]` 的 get 结果（get 一次），
   判断 `___t1 == null`（不重新 get），如果为 null 则执行 `cache[key] = ...`
   （set 一次）。总计 get 一次 + set 一次，与 `??=` 原始语义一致。

### 3.3 临时变量命名与作用域

- 前缀 `___t` + 递增数字（`___t1`、`___t2`、`___t3`…）
- 计数器为**文件级**（单文件内递增，跨文件重置）
- 提升位置：最近的包含语句之前（同一作用域内）
- 命名冲突检查：扫描当前作用域内已有标识符，如果 `___tN` 已存在则继续
  递增直到不冲突

### 3.4 类型推导

使用 SemanticModel 获取左操作数的静态类型：

```csharp
var typeSymbol = semanticModel.GetTypeInfo(leftOperand).Type;
if (typeSymbol == null || typeSymbol is IErrorTypeSymbol)
{
    // 无法推导类型 → 保守跳过 + Warning 诊断
    return originalNode;
}
var typeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
```

类型字符串格式说明：
- 使用 `FullyQualifiedFormat` 确保类型名无歧义（如 `System.String` 而非
  依赖 `using` 的 `string`）
- `Nullable<T>` → `System.Nullable<T>`（全限定形式，合法 C#）
- 泛型类型 → 全限定泛型形式（如 `System.Collections.Generic.List<System.String>`）

### 3.5 不改写的情况

- `??` 左操作数为 null 字面量（`null ?? x` → 无意义但合法 C#）→ 保留不动
- `??=` 左操作数为字面量/常量 → 原代码本身不合法，保留并 Error 诊断
- 字段初始化器中无法插入局部变量 → Warning + 保留
- LINQ 查询表达式内部 → Warning + 保留

## 4. 架构概览与组件布局

### 4.1 目录结构

```
src/CSharpToJava.Core/NullCoalesceRewriter/
    NullCoalesceRewriter.cs              # 公共入口 Preprocess(sourceCode, semanticModel, options) -> result
    NullCoalesceSyntaxRewriter.cs        # CSharpSyntaxRewriter 主体（两阶段改写核心）
    NullCoalesceExpressionCollector.cs   # Pass 1: 自底向上遍历，收集临时变量声明 + 改写表达式
    NullCoalesceStatementLifter.cs       # Pass 2: 将收集的声明提升到包含语句之前
    NullCoalesceOptions.cs               # record（Verbose / Strict）
    NullCoalesceStatistics.cs            # 统计模型
    NullCoalesceDiagnostic.cs            # 诊断模型
    NullCoalesceResult.cs                # 结果 record

src/CSharpToJava.CLI/ProjectNullCoalescePreprocessor.cs   # 项目模式包装
```

### 4.2 核心流程

```
PreprocessAsync
  ├── ResolveLayout / CopyProjectFiles / CopyAncestorMsBuildFiles  (复用 ProjectPreprocessSource)
  ├── 逐文件遍历
  │    ├── dirty 检测（语法树中有 CoalesceExpression 或 CoalesceAssignmentExpression）
  │    ├── clean → 字节级保真拷贝
  │    └── dirty → NullCoalesceRewriter.Preprocess(code, semanticModel, options)
  │         ├── Parse → Compilation → SemanticModel
  │         ├── Pass 1: ExpressionCollector（自底向上改写表达式，收集临时变量）
  │         ├── Pass 2: StatementLifter（把临时变量提升到包含语句前）
  │         ├── Formatter.Format → 输出 C#
  │         └── 后置自检：产物中无残留 ?? / ??= 语法
  ├── dotnet restore
  └── 可选 dotnet build / test 验证
```

## 5. 两阶段改写实现

### 5.1 Pass 1 — ExpressionCollector（表达式改写 + 声明收集）

**核心类**：`NullCoalesceExpressionCollector : CSharpSyntaxRewriter`

#### 状态

- `List<PendingDeclaration> _pendingDeclarations` — 收集的临时变量声明
- `int _tempCounter` — 临时变量计数器
- `SemanticModel _model` — 语义模型
- `Stack<SyntaxNode> _ancestorStack` — 祖先节点栈（用于找到最近包含语句）

#### PendingDeclaration 结构

```csharp
private sealed record PendingDeclaration(
    StatementSyntax Declaration,      // 如 var ___t1 = GetValue();
    SyntaxNode OriginalNode,           // 被改写的 ?? / ??= 节点（用于匹配锚点）
    StatementSyntax AnchorStatement,   // 最近包含语句（临时变量插入到它之前）
    int Order);                         // 收集顺序（同一锚点多个声明时保持顺序）
```

#### 遍历策略

自底向上：先递归处理子节点（`Visit(node.Left)` / `Visit(node.Right)`），
再处理当前 `??` / `??=` 节点。确保内层 `??` 先被改写，外层后处理。

#### `??` 处理流程

```
VisitBinaryExpression(node)
  if node.IsKind(CoalesceExpression):
    1. left = Visit(node.Left)   // 递归处理内层 ??
    2. right = Visit(node.Right)  // 递归处理内层 ??
    3. 获取左操作数类型 T = _model.GetTypeInfo(node.Left).Type
    4. 如果 T == null 或 IErrorTypeSymbol → Warning + 返回原节点不动
    5. 生成 ___tN（递增 + 冲突检查）
    6. 构造声明: LocalDeclarationStatement(T, ___tN, node.Left.ToString())
    7. 找到最近包含语句 = _ancestorStack 中最近的 StatementSyntax
    8. 加入 _pendingDeclarations
    9. 返回: ParenthesizedExpression(
         ConditionalExpression(
           BinaryExpression(NotEqual, IdentifierName(___tN), NullLiteral),
           IdentifierName(___tN),
           right))
  else:
    return base.VisitBinaryExpression(node)
```

#### `??=` 处理流程

```
VisitAssignmentExpression(node)
  if node.IsKind(CoalesceAssignmentExpression):
    1. right = Visit(node.Right)  // 递归处理右侧内层 ??
    2. left = node.Left
    3. 判断 left 是否为简单标识符（IdentifierNameSyntax 且无成员访问链）
       - 是 → 不需要临时变量:
           构造: IfStatement(
                   BinaryExpression(Equals, left, NullLiteral),
                   Block(ExpressionStatement(
                     SimpleAssignment(left, right))))
       - 否（成员/索引器）→ 需要临时变量:
           T = _model.GetTypeInfo(left).Type
           如果 T == null → Warning + 返回原节点
           ___tN = 递增
           声明: T ___tN = left.ToString();
           构造: IfStatement(
                   BinaryExpression(Equals, IdentifierName(___tN), NullLiteral),
                   Block(ExpressionStatement(
                     SimpleAssignment(left, right))))
           加入 _pendingDeclarations
    4. 返回改写后的 IfStatement（替换原 ExpressionStatement）
  else:
    return base.VisitAssignmentExpression(node)
```

#### `??=` 在表达式上下文

`??=` 是 `AssignmentExpression`，通常出现在 `ExpressionStatement` 中。但
C# 8+ 也允许赋值出现在表达式中（如 `var x = (a ??= b) + 1`）。

处理策略：

- 如果 `??=` 的最近祖先是 `ExpressionStatement` → 直接替换为 `IfStatement`
  （正常路径）
- 如果 `??=` 出现在更深的表达式上下文（非 ExpressionStatement）→ 需要把整
  个包含语句拆分为多条语句。具体实现：
  1. 提取 `??=` 左操作数为临时变量（如果有副作用）
  2. 把整个包含语句拆分为：
     - `if (left == null) { left = right; }`
     - 后续使用 `left` 替代原 `(left ??= right)` 的位置
  3. 如果无法安全拆分 → Warning 诊断 + 保留原节点

### 5.2 Pass 2 — StatementLifter（语句提升）

**核心类**：`NullCoalesceStatementLifter : CSharpSyntaxRewriter`

**输入**：Pass 1 产出的语法树 + `_pendingDeclarations` 列表。

**策略**：

遍历语法树中的 `BlockSyntax` 节点。对于每个 block 中的语句，检查
`_pendingDeclarations` 中是否有 `AnchorStatement == 该语句` 的声明。如果
有，在该语句之前插入这些声明（按 `Order` 排序）。

```csharp
public override SyntaxNode VisitBlock(BlockSyntax node)
{
    var newStatements = new List<StatementSyntax>();
    foreach (var stmt in node.Statements)
    {
        // 查找属于该语句的挂起声明
        var pending = _pendingDeclarations
            .Where(d => d.AnchorStatement == stmt)
            .OrderBy(d => d.Order);
        foreach (var p in pending)
            newStatements.Add(p.Declaration);

        // 递归处理子块
        var visited = (StatementSyntax)Visit(stmt);
        newStatements.Add(visited);
    }
    return node.WithStatements(newStatements);
}
```

#### 特殊上下文处理

| 上下文 | 处理方式 |
|--------|----------|
| 方法体 `Block` | 正常插入到语句前 |
| 构造函数参数 `: base(...)` 中的 `??` | 提取为局部变量声明在构造函数体开头 |
| Lambda 表达式体（expression lambda） | 转为 block lambda，插入声明 |
| Lambda 块内 | 正常插入到包含语句前 |
| switch 表达式 arms | 正常插入到最近语句前（如果 switch 表达式在语句中） |
| LINQ 查询表达式 | 保守跳过 + Warning |
| 字段初始化器 | 保守跳过 + Warning |

### 5.3 格式化与输出

两阶段完成后，使用 `Formatter.Format` 规整间距（与 `GotoEliminator` /
`LocalFunctionLift` 同款 `AdhocWorkspace` 方式）。输出 UTF-8 无 BOM。

### 5.4 Dirty 检测

纯语法快速路径（不需要 SemanticModel）：

```csharp
private static bool IsDirty(CompilationUnitSyntax root)
    => root.DescendantNodes().Any(n =>
        n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
        || n is AssignmentExpressionSyntax a
            && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));
```

clean 文件字节级保真（直接返回原字符串）。

### 5.5 后置自检

产物重新解析，确认不含残留 `??` / `??=` 语法：

```csharp
private static bool ContainsResidualNullCoalesce(string code)
{
    var root = CSharpSyntaxTree.ParseText(code).GetRoot();
    return root.DescendantNodes().Any(n =>
        n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
        || n is AssignmentExpressionSyntax a
            && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));
}
```

如果有残留 → Error 诊断。

## 6. 语义保证与边界情况

### 6.1 语义等价性保证

| 规则 | 保证方式 |
|------|----------|
| 左操作数只求值一次 | `??` 一律用临时变量；`??=` 对非简单标识符用临时变量 |
| 副作用顺序不变 | 临时变量声明在原语句之前，求值顺序与原始 `??` 从左到右一致 |
| 求值次数不变 | 临时变量只赋值一次（存左操作数结果），后续引用不重复求值 |
| 嵌套 `??` 从内向外展开 | Pass 1 自底向上递归，内层先处理，外层后处理；每个左操作数独立临时变量 |
| `??=` 的 get/set 次数 | 简单标识符：0 次 get（直接读变量）+ 1 次 set（赋值时）；非简单标识符：1 次 get（存入临时变量）+ 1 次 set（赋值时）→ 与原语义一致 |

### 6.2 `??` 在特殊上下文

| 上下文 | 改写前 | 改写后 | 备注 |
|--------|--------|--------|------|
| 变量声明 | `var a = x ?? y;` | `var ___t1 = x;` + `var a = (___t1 != null) ? ___t1 : y;` | 正常 |
| 普通赋值 | `x.Y = GetValue() ?? fallback;` | `var ___t1 = GetValue();` + `x.Y = (___t1 != null) ? ___t1 : fallback;` | 正常 |
| return | `return GetData() ?? fallback;` | `var ___t1 = GetData();` + `return (___t1 != null) ? ___t1 : fallback;` | 正常 |
| 方法参数 | `Foo(GetValue() ?? fallback);` | `var ___t1 = GetValue();` + `Foo((___t1 != null) ? ___t1 : fallback);` | 正常 |
| 三元分支 | `x ? (a ?? b) : c` | `var ___t1 = a;` + `x ? ((___t1 != null) ? ___t1 : b) : c` | 正常 |
| Lambda 体 | `() => GetValue() ?? fallback` | `() => { var ___t1 = GetValue(); return (___t1 != null) ? ___t1 : fallback; }` | expression lambda → block lambda |
| 字段初始化 | `class C { int? f = GetDefault() ?? 0; }` | 不改写 + Warning | 字段初始化器上下文无法插入局部变量声明 |

### 6.3 `??=` 在特殊上下文

| 上下文 | 改写前 | 改写后 | 备注 |
|--------|--------|--------|------|
| 语句（简单标识符） | `a ??= GetDefault();` | `if (a == null) { a = GetDefault(); }` | 正常，不需临时变量 |
| 语句（成员访问） | `config.Timeout ??= 30;` | `var ___t1 = config.Timeout;` + `if (___t1 == null) { config.Timeout = 30; }` | 临时变量，保证只 get 一次 |
| 语句（索引器） | `cache[key] ??= Compute();` | `var ___t1 = cache[key];` + `if (___t1 == null) { cache[key] = Compute(); }` | 临时变量，保证只 get 一次 |
| 表达式上下文 | `var x = (a ??= b) + 1;` | 拆分为多语句 | 提取 `a` 为临时变量 → `if (a == null) { a = b; }` → `var x = a + 1;` |

### 6.4 嵌套 `??` 详细示例

```csharp
// before — 三层嵌套
var r = a ?? b ?? c ?? d;

// Pass 1 自底向上处理:
//   1. 处理 b ?? c ?? d（内层先）
//      a. 处理 c ?? d（最内层）
//         生成 ___t1 = c, 改写为 (___t1 != null) ? ___t1 : d
//      b. 处理 b ?? [改写后的 c??d]
//         生成 ___t2 = b, 改写为 (___t2 != null) ? ___t2 : ((___t1 != null) ? ___t1 : d)
//   2. 处理 a ?? [改写后的 b??c??d]
//      生成 ___t3 = a, 改写为 (___t3 != null) ? ___t3 : (...)

// Pass 2 提升后:
var ___t1 = c;
var ___t2 = b;
var ___t3 = a;
var r = (___t3 != null) ? ___t3 : ((___t2 != null) ? ___t2 : ((___t1 != null) ? ___t1 : d));
```

## 7. 数据结构

### 7.1 Options

```csharp
namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <param name="Verbose">If true, emit Info-level diagnostics.</param>
/// <param name="Strict">If true, residual ?? / ??= produces Error; otherwise Warning.</param>
public sealed record NullCoalesceOptions(
    bool Verbose = false,
    bool Strict = false);
```

### 7.2 Statistics

```csharp
public sealed class NullCoalesceStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int NullCoalescingRewritten;       // ?? 改写次数
    public int NullCoalescingAssignRewritten; // ??= 改写次数
    public int TempVariablesGenerated;        // 生成临时变量数
    public int NestedRewrites;                // 嵌套改写次数
    public int SkippedUnsupported;            // 跳过的不支持场景数
    public int ResidualCount;                 // 后置自检残留数
}
```

### 7.3 Diagnostic

```csharp
public enum NullCoalesceSeverity { Warning, Error }

public sealed record NullCoalesceDiagnostic(
    NullCoalesceSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0);
```

### 7.4 Result

```csharp
public sealed record NullCoalesceResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<NullCoalesceDiagnostic> Diagnostics,
    NullCoalesceStatistics Statistics);
```

## 8. CLI 包装（ProjectNullCoalescePreprocessor）

### 8.1 请求/结果模型

```csharp
internal sealed class ProjectNullCoalesceRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectNullCoalesceResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required NullCoalesceStatistics Statistics { get; init; }
    public required IReadOnlyList<NullCoalesceDiagnostic> Diagnostics { get; init; }
}
```

### 8.2 PreprocessAsync 流程

完全 mirror `ProjectLocalFunctionLiftPreprocessor`：

1. `ResolveLayout` + `ComputeExclusions`
2. `CopyProjectFilesAsync` + `CopyAncestorMsBuildDirectoryFiles`
3. 预先解析所有 `.cs` 文件为 SyntaxTree
4. 创建 `CSharpCompilation`（`allowUnsafe: true`），使用
   `ProjectUnsignedTypePreprocessor.GetBasicReferences()` 获取基础引用集
5. 逐文件遍历：
   - 非 `.cs` → 原样拷贝
   - `.cs` → dirty 检测 → clean 直接拷贝 / dirty 改写
6. 后置自检（残留 `??` / `??=` 检测）
7. `dotnet restore`
8. 可选 `dotnet build -c Release` + `dotnet test -c Release`
9. 清理 build 输出目录

### 8.3 CLI Verb 接入

在 `Program.cs` 中：

1. 新增 Options 类 `NullCoalesceOptions`（verb 名 `null-coalesce`），对齐
   `EliminateGotoOptions` / `MakeReadOnlyOptions` 的 verb 模式
2. 新增 dispatch `(NullCoalesceOptions opts) => NullCoalesceAsync(opts)`
3. `convert-project` 流水线中新增 `NullCoalesce` 开关，串联在
   `LocalFunctionLift` 之后
4. 中间目录 `.cs2j-null-coalesce-src` 加入 `FingerprintInputRoot` preserved
   列表

### 8.4 SemanticModel 构建

与 `ProjectLocalFunctionLiftPreprocessor` 同款：

- 预先解析所有 `.cs` 文件为 SyntaxTree（使用
  `CSharpParseOptions(preprocessorSymbols: new[] { "NETCOREAPP" })`）
- 创建 `CSharpCompilation`（`allowUnsafe: true`）
- 从 compilation 获取每个文件的 SemanticModel
- 使用 `ProjectUnsignedTypePreprocessor.GetBasicReferences()` 获取基础引用集

## 9. convert-project 流水线集成

### 9.1 阶段顺序

```
 1. StripConditional       (条件指令预处理)
 2. UnsignedType           (无符号类型改写)
 3. ReadOnlyStruct         (只读结构体改写)
 4. TupleToStruct          (元组转结构体)
 5. GenericPreprocess      (泛型预处理)
 6. StripDefaultParameter  (默认参数剥离)
 7. GotoEliminator         (goto 消除)
 8. LocalFunctionLift      (局部函数提升)
 9. NullCoalesce           ← 新增（null 合并改写）
10. RecordReadOnly         (record 只读预处理)
11. ArrayMethod           (数组方法改写)
→ 转译
```

放在 `LocalFunctionLift` 之后、`RecordReadOnly` 之前。理由：
- null 合并是纯语法降级，不依赖其他预处理器的产物
- 在 goto 消除和局部函数提升之后，代码结构更简单，`??` / `??=` 的作用域
  更清晰
- 在 record/array 预处理之前，避免与它们的改写产生交互

### 9.2 幂等性

重复运行幂等：改写后产物不含 `??` / `??=`，再次运行 dirty 检测为 clean，
统计为 0。

## 10. 测试策略

### 10.1 单元测试（NullCoalesceRewriterUnitTests）

放在 `tests/CSharpToJava.Tests/`，参考 `GotoEliminatorTests.cs` 风格。

**`??` 改写测试组**：
- 简单标识符左操作数：`var a = x ?? y;` → 验证临时变量生成
- 方法调用左操作数：`var a = Get() ?? y;` → 验证只求值一次
- 同一语句多个 `??`：`var z = (a ?? b) + (c ?? d);` → 验证独立临时变量
- 嵌套 `??`：`var r = a ?? b ?? c;` → 验证从内向外展开
- return 语句：`return Get() ?? fallback;`
- 方法参数：`Foo(Get() ?? fallback);`
- 三元分支内：`x ? (a ?? b) : c;`
- 字段初始化器：不改写 + Warning

**`??=` 改写测试组**：
- 简单标识符：`a ??= GetDefault();` → `if (a == null) { a = GetDefault(); }`
- 成员访问：`config.Timeout ??= 30;` → 验证临时变量
- 索引器：`cache[key] ??= Compute();` → 验证临时变量
- 表达式上下文：`var x = (a ??= b) + 1;` → 验证语句拆分

**负向测试**：
- clean 文件（无 `??` / `??=`）→ 字节级保真
- `string.Length` 等非 `??` 的 `BinaryExpression` → 不改写
- 普通赋值 `=` → 不改写

**后置自检**：
- 改写产物重新解析无 Error
- 改写产物无残留 `??` / `??=`

### 10.2 语义等价测试（NullCoalesceSemanticsTests）

把同一组夹具源码分别以"原始"与"改写后"编译运行，逐字节比对 stdout：

- 左操作数有副作用（方法调用 + 计数器断言求值次数）
- 嵌套 `??`（多层求值顺序）
- `??=` 索引器（key 有副作用时验证只求值一次）
- `??=` 简单标识符

### 10.3 项目级集成测试（ProjectNullCoalescePreprocessorTests）

参考 `ProjectGotoPreprocessorTests.cs` /
`ReadOnlyStructPreprocessorVerifyTests.cs`：

- `IsDotNetAvailable` 守卫
- `CreateTempDirectory` 临时目录
- 验证中间目录结构正确
- `dotnet build -c Release` 通过
- `dotnet test -c Release` 通过（含测试工程时）
- 后置自检：中间目录产物无残留 `??` / `??=`

## 11. 非目标

- 不处理 `??` 在模式匹配中的使用（C# 没有此语法，`??` 不是 pattern）
- 不处理 `Nullable<T>` 的 `HasValue` / `Value` 改写（与 `??` 语义不同）
- 不对 `throw` 表达式做特殊改写（`x ?? throw new Exception()` 改写后
  变为 `___tN = x; (___tN != null) ? ___tN : throw new Exception()`，
  三元运算符的 false 分支允许 throw 表达式，天然支持；但如果 throw
  出现在左操作数位置则类型推导会失败，按 §12 跳过 + Warning）
- 不处理条件运算符 `?:` 本身（它不是 `??`）
- 不修改主转换器的任何行为（纯 C# → C# 预处理）
- 不规划 Java 侧支撑类或 compat 方法（null 合并改写后不需要任何外部支撑）
- 不规划 Maven 模块或 TypeMappings 条目（产物是纯 C# 语法等价物）
- 不处理 TypeScript 路径（TS 转换不经过本预处理）

## 12. 已知限制与风险

| 限制 | 说明 | 缓解 |
|------|------|------|
| 字段初始化器中的 `??` | 无法在字段初始化器前插入局部变量声明 → Warning + 保留 | 极少见；可作为后续迭代处理（如提取到构造函数） |
| LINQ 查询表达式内的 `??` | 查询表达式语法结构不支持中间插入语句 → Warning + 保留 | 罕见；可手动改写 |
| `??=` 在复杂表达式上下文 | 拆分为多语句可能改变作用域或变量捕获语义 | 保守处理，Warning 诊断明确标注 |
| 类型推导失败 | 左操作数类型无法解析时跳过 | SemanticModel 依赖完整 compilation，restore 成功后应可解析 |
| 临时变量名冲突 | `___tN` 可能与用户代码中已有标识符冲突 | 冲突检查 + 递增直到不冲突 |
| `??` 左操作数为 `throw` 表达式 | `throw` 表达式没有类型 → 类型推导失败 | 跳过 + Warning；`x ?? throw ...` 在实践中可手工处理 |

## 13. 迁移

- 无 Java 侧变更：不涉及 compat 类、TypeMappings、Maven 模块。
- `convert-project` 集成为新增阶段，默认开启（与其他预处理器阶段相同的
  内置策略）。
- 旧产物需重跑 cs2j 全流水线再生效。
- 对已预处理输入的重复运行幂等（改写统计为 0）。
