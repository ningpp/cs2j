# ReadOnlyStructMaker — C# Struct 转 ReadOnly Struct 预处理器设计

**日期**: 2026-07-26
**范围**: 新模块 `src/CSharpToJava.Core/ReadOnlyStructMaker/` + CLI 动词 `make-readonly` + 项目级预处理集成 + 单元测试
**状态**: 待用户审核

## 1. 背景与目标

### 1.1 任务

开发一个 C# 源代码预处理器，将项目中所有符合转换条件的普通 struct 自动转换为 readonly struct。从语言层面约束 struct 的不可变性，启用 JIT 运行时优化，减少不必要的内存复制开销。

### 1.2 项目背景

- 工作目录 `d:\code\cs2j` 是基于 Roslyn 的 C#→Java 转换器（net10.0，Roslyn `Microsoft.CodeAnalysis.CSharp` 4.12.0，xUnit 2.9.3）
- 现有 GotoEliminator 模块已实现 goto/label 预处理功能，CLI 对应 `eliminate-goto` 子命令；`convert-project` 默认启用该预处理，可用 `--no-eliminate-goto` 关闭（见 `Program.cs` 中 `ConvertProjectOptions.NoEliminateGoto`）
- 本功能与 GotoEliminator **完全独立**，但遵循相同的架构模式

### 1.3 执行顺序

本功能必须在现有 goto 转状态机预处理步骤**之前**执行：

1. **第一阶段（本功能）**：普通 struct 自动转 readonly struct
2. **第二阶段（已有功能）**：goto 语句转状态机实现

### 1.4 强制约束

1. 输入输出均为标准 C#源码，与现有 C#→Java 流程完全独立
2. 所有转换在 C#语法树层面完成，输出合法的标准 C#代码
3. 幂等性——已不含可转换 struct 的文件输出与输入完全一致
4. 保留所有原有代码格式、注释、Whitespace 信息
5. 与现有 goto 预处理功能完全兼容，本功能的输出可直接作为 goto 预处理的输入
6. 语法重写阶段对每棵语法树单次遍历完成；调用图等分析结果缓存复用，避免重复分析

### 1.5 成功标准

- [ ] 所有符合条件的 struct 被成功转换为 readonly struct
- [ ] 不符合条件的 struct 不被修改，并通过诊断接口输出警告
- [ ] 转换后的 C# 代码编译通过
- [ ] 与现有 goto 预处理功能正确集成
- [ ] CLI 动词 `make-readonly` 正常工作
- [ ] `convert-project` 的 `--no-make-readonly` 选项正常工作
- [ ] 所有单元测试通过

## 2. 关键设计决策

| # | 决策 | 选项 | 选择 | 理由 |
|---|------|------|------|------|
| D1 | 字段修改分析深度 | (a) 语法级；(b) 语义级（单方法）；(c) 数据流分析 | **(c) 数据流分析** | 最全面，能检测间接字段修改 |
| D2 | 接口方法分析 | (a) 纳入调用图；(b) 独立检测；(c) 跳过检测 | **(a) 纳入调用图** | 与数据流分析方案一致 |
| D3 | partial struct 处理 | (a) 单文件独立分析；(b) 项目级合并分析；(c) 跳过 | **(c) 跳过** | 避免部分加 readonly 导致编译错误 |
| D4 | 诊断详细程度 | (a) 逐条列出；(b) 仅首个原因；(c) 仅是否可转换 | **(b) 仅首个原因** | 简化输出，用户可逐个修复 |
| D5 | 分析范围 | (a) 单文件；(b) 项目级编译后分析 | **(b) 项目级编译后分析** | 构建完整跨文件调用图 |

## 3. 模块布局

```
src/CSharpToJava.Core/ReadOnlyStructMaker/
├── ReadOnlyStructMaker.cs            # 公共入口
├── ReadOnlyStructMakerOptions.cs     # 配置选项
├── ReadOnlyStructMakerDiagnostics.cs  # 诊断类型 + 结果类型
├── StructAnalyzer.cs                 # 核心分析器
├── CallGraphBuilder.cs               # 调用图构建器
└── ReadOnlyStructRewriter.cs         # 语法重写器
```

## 4. 转换规则

### 4.1 自动转换条件（全部满足才执行转换）

1. 所有实例字段均为 `readonly`（可访问性不限）；静态字段不受限制（`const` 或可变静态字段均可）。注意：判定必须覆盖编译器生成的支持字段（backing field）——带任何 `set`/`init` 访问器的自动属性具有非 readonly 支持字段，会被本条件直接拦截
2. 手动实现的属性没有 `public`/`protected`/`internal` 等外部可访问的 `set`/`init` 访问器（`init` 按 `set` 同等对待，保守策略）
3. 没有实例方法会修改任何成员字段（静态方法、纯计算类方法除外）；直接字段赋值场景已被条件 1 覆盖，本条件主要捕获 `ref`/`out this` 逃逸等间接修改（见 9.2/9.3）
4. 没有实现接口方法会修改成员状态
5. 未标记 `[DoNotMakeReadOnly]` 等禁止转换的自定义特性（命名理由见 5.3 步骤 4）
6. 不是 ref struct、不是已经声明为 readonly struct 的类型
7. 不是 partial struct

### 4.2 转换操作

1. 在 struct 声明语法节点上添加 `readonly` 修饰符
2. 保留所有现有成员、方法、实现接口、泛型参数、Attribute、XML 注释不变
3. 对不符合转换条件的 struct 不做任何修改，并通过诊断接口输出警告说明不符合的具体原因

## 5. 架构设计

### 5.1 执行流程

```
┌─────────────────────────────────────────────────────────────────┐
│                        Entry Point                               │
│  ReadOnlyStructMaker.MakeReadOnly(compilation, options)          │
└───────────────────────────┬─────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Phase 1: 编译收集                             │
│  • 使用已有 Compilation 获取 SemanticModel                       │
│  • 收集所有 StructDeclarationSyntax                              │
│  • 识别已 readonly/ref/partial struct，标记跳过并输出 Info 诊断   │
└───────────────────────────┬─────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Phase 2: 调用图构建                           │
│  • CallGraphBuilder 遍历所有 struct 的实例方法                    │
│  • 构建方法→被调用方法的映射关系                                  │
│  • 标记直接修改字段的方法                                        │
└───────────────────────────┬─────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Phase 3: 可转换性判定                         │
│  • StructAnalyzer 递归检测每个 struct：                          │
│    - 所有实例字段是否为 readonly（含支持字段）                    │
│    - 手动属性是否无外部可访问 set/init                            │
│    - 调用图中是否存在字段修改路径                                 │
│    - 是否标记 [DoNotMakeReadOnly]                               │
└───────────────────────────┬─────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Phase 4: 语法重写                             │
│  • ReadOnlyStructRewriter 添加 readonly 修饰符                   │
│  • 保留所有 trivia（注释、空白）                                  │
│  • 输出转换后的源码                                              │
└─────────────────────────────────────────────────────────────────┘
```

### 5.2 核心组件

#### ReadOnlyStructMaker.cs — 公共入口

```csharp
public sealed class ReadOnlyStructMaker
{
    /// <summary>项目级入口：分析整个编译中的所有 struct 并转换为 readonly。</summary>
    public ReadOnlyStructMakerResult MakeReadOnly(
        CSharpCompilation compilation,
        ReadOnlyStructMakerOptions? options = null);
    
    /// <summary>单文件入口（用于测试）：分析单个文件中的 struct。</summary>
    public ReadOnlyStructMakerResult MakeReadOnly(
        SyntaxTree tree,
        SemanticModel semanticModel,
        ReadOnlyStructMakerOptions? options = null);
}
```

#### CallGraphBuilder.cs — 调用图构建器

```csharp
internal sealed class CallGraphBuilder
{
    // 注意：所有以 IMethodSymbol 为键的集合必须使用 SymbolEqualityComparer.Default，
    // 不能使用默认的引用相等比较器。

    /// <summary>方法符号 → 该方法内直接调用的实例方法符号集合</summary>
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> CallGraph { get; }
    
    /// <summary>方法符号 → 该方法是否直接修改字段（含 ref/out this 逃逸）</summary>
    public HashSet<IMethodSymbol> DirectFieldModifiers { get; }
    
    /// <summary>从 struct 符号开始构建调用图；跨文件调用经 compilation.GetSemanticModel(tree) 按语法树获取对应语义模型</summary>
    public void BuildForStruct(INamedTypeSymbol structSymbol, CSharpCompilation compilation);
    
    /// <summary>递归检测方法（或其调用链中）是否修改字段</summary>
    public bool ModifiesField(IMethodSymbol method, HashSet<IMethodSymbol>? visited = null);
}
```

#### ReadOnlyStructMakerOptions.cs — 配置选项

```csharp
public sealed class ReadOnlyStructMakerOptions
{
    /// <summary>禁止转换特性的名称（不含 Attribute 后缀），命名空间不限</summary>
    public string OptOutAttributeName { get; init; } = "DoNotMakeReadOnly";
    
    /// <summary>是否输出 Info 级跳过诊断（默认 true）</summary>
    public bool ReportSkipped { get; init; } = true;
    
    /// <summary>不可转换的 struct 是否视为致命错误（对应 CLI --strict）</summary>
    public bool Strict { get; init; }
}
```

#### StructAnalyzer.cs — 结构分析器

```csharp
internal sealed class StructAnalyzer
{
    private readonly CallGraphBuilder _callGraph;
    private readonly ReadOnlyStructMakerOptions _options;
    
    /// <summary>分析 struct 是否可转换为 readonly</summary>
    public AnalyzeResult Analyze(INamedTypeSymbol structSymbol, StructDeclarationSyntax syntax);
}

internal sealed record AnalyzeResult(
    bool ShouldRewrite,   // true = 需要添加 readonly；false = 无需操作（已符合/跳过/不可转换）
    string Reason,
    StructDeclarationSyntax? DeclarationSyntax,
    string? QualifiedName)
{
    /// <summary>已经是 readonly struct，无需重写</summary>
    public static AnalyzeResult AlreadyReadOnly(string name) => 
        new(false, "already readonly struct (skipped)", null, name);
    
    /// <summary>因结构性原因跳过（ref struct / partial struct）</summary>
    public static AnalyzeResult Skip(string name, string reason) => 
        new(false, reason, null, name);
    
    /// <summary>分析判定不可转换（字段/属性/方法不满足条件）</summary>
    public static AnalyzeResult Fail(string name, string reason, StructDeclarationSyntax syntax) => 
        new(false, reason, syntax, name);
    
    /// <summary>分析通过，需要执行 readonly 改写</summary>
    public static AnalyzeResult Success(string name, StructDeclarationSyntax syntax) => 
        new(true, "converted to readonly struct", syntax, name);
}
```

#### ReadOnlyStructRewriter.cs — 语法重写器

```csharp
internal sealed class ReadOnlyStructRewriter : CSharpSyntaxRewriter
{
    /// <summary>
    /// 可转换 struct 的语法节点集合（引用相等）。
    /// 要求分析与重写使用同一语法树实例（项目级流程在内存中保留编译的语法树，满足该前提），
    /// 避免基于名称匹配导致跨命名空间同名 struct、同名不同元数泛型 struct 的误判。
    /// </summary>
    private readonly HashSet<StructDeclarationSyntax> _convertibleStructNodes;
    
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        if (!_convertibleStructNodes.Contains(node))
        {
            return base.VisitStructDeclaration(node);
        }

        if (node.Modifiers.Count > 0)
        {
            // 已有修饰符（如 public）：readonly 追加到末尾，不带前导 trivia，
            // 依赖前一个修饰符的尾随空格分隔；自身尾随一个空格与 struct 关键字分隔。
            var trailingToken = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space);
            return node.AddModifiers(trailingToken);
        }

        // 无修饰符：readonly 接管 struct 关键字的前导 trivia（缩进、注释等），
        // struct 关键字自身的前导 trivia 清空，避免换行/缩进残留在 readonly 与 struct 之间。
        var readonlyToken = SyntaxFactory.Token(
                node.Keyword.LeadingTrivia,
                SyntaxKind.ReadOnlyKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space));
        return node
            .WithKeyword(node.Keyword.WithLeadingTrivia())
            .AddModifiers(readonlyToken);
    }
}
```

### 5.3 判定逻辑（按优先级顺序）

```
1. 已经是 readonly struct → 跳过 (Info)
2. 是 ref struct → 跳过 (Info)
3. 是 partial struct → 跳过 (Info)
4. 检查 [DoNotMakeReadOnly] 特性 → 不可转换 (Warning)
   - 特性名称：`DoNotMakeReadOnlyAttribute`，命名空间不限
   - 匹配逻辑：存在即视为不可转换（无需构造参数）
   - 命名说明：不得使用 `IsReadOnly` 作为自定义特性名——它与 `System.Runtime.CompilerServices.IsReadOnlyAttribute`
     冲突（编译器会为 readonly struct 自动附加该特性，且其没有接受 bool 参数的构造函数）
5. 检查所有实例字段是否为 readonly（含编译器生成的支持字段；静态字段不检查） → 不可转换 (Warning)
6. 检查手动实现属性是否有外部可访问的 set/init 访问器 → 不可转换 (Warning)
7. 通过调用图递归检查方法是否修改字段 → 不可转换 (Warning)
8. 全部通过 → 可转换
```

## 6. 诊断设计

### 6.1 诊断类型

```csharp
public enum ReadOnlyStructSeverity
{
    Info,       // 成功转换或跳过
    Warning,    // 无法转换
    Error       // 处理异常
}

public sealed record ReadOnlyStructMakerDiagnostic(
    ReadOnlyStructSeverity Severity,
    string StructName,
    string Reason,
    string? FilePath = null,
    int? Line = null);

public sealed class ReadOnlyStructMakerStatistics
{
    public int StructsScanned;
    public int StructsConverted;
    public int StructsSkipped;
    public int StructsFailed;
}

/// <summary>
/// 转换结果。对齐 GotoEliminatorResult 的模式：
/// 单文件模式下 OutputCode 始终有值（未变更时与输入一致，保证幂等），ChangedFiles 为空；
/// 项目级模式下 ChangedFiles 携带每个被修改文件的路径与转换后代码，OutputCode 为 null。
/// </summary>
public sealed record ReadOnlyStructMakerResult(
    string? OutputCode,
    bool Changed,
    IReadOnlyDictionary<string, string> ChangedFiles,
    IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics,
    ReadOnlyStructMakerStatistics Statistics);
```

### 6.2 诊断输出格式

**成功转换**：
```
[Info] Struct 'Point' in Math/Geometry.cs:10: converted to readonly struct
```

**无法转换**：
```
[Warning] Struct 'MutableBuffer' in IO/Buffer.cs:20: field '_data' is not readonly
[Warning] Struct 'Counter' in Stats/Counter.cs:15: method 'Increment' modifies fields
[Warning] Struct 'Config' in App/Config.cs:8: property 'Value' has accessible setter
[Warning] Struct 'Worker' in Tasks/Worker.cs:12: marked with [DoNotMakeReadOnly]
```

**跳过**：
```
[Info] Struct 'AlreadyReadOnly' in Models/Point.cs:5: already readonly struct (skipped)
[Info] Struct 'RefStruct' in Memory/Span.cs:3: ref struct (skipped)
[Info] Struct 'PartialStruct' in Models/Data.cs:10: partial struct (skipped)
```

## 7. CLI 设计

### 7.1 新增 CLI 动词 `make-readonly`

```csharp
[Verb("make-readonly", HelpText = "Convert eligible structs to readonly structs")]
class MakeReadOnlyOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    
    [Option("strict", Default = false, HelpText = "Treat unconvertible structs as fatal")]
    public bool Strict { get; set; }
}
```

> 注意：与 `eliminate-goto` 的纯语法分析不同，本功能依赖 SemanticModel。单文件模式下 CLI 需内部创建
> ad-hoc 编译（引用当前运行时核心程序集）以获得语义模型；项目模式通过 SolutionLoader/MSBuild 加载
> 完整项目（含 `dotnet restore`，参考 `ProjectGotoPreprocessor` 的做法）。

### 7.2 convert-project 集成

在 `ConvertProjectOptions` 中添加：

```csharp
[Option("no-make-readonly", Default = false, HelpText = "Disable the default readonly-struct preprocessing step")]
public bool NoMakeReadOnly { get; set; }

public bool MakeReadOnly => !NoMakeReadOnly;
```

该选项与现有 `--no-eliminate-goto` 的"默认启用、显式关闭"模式一致。同时需要两处配套修改：

1. `GetProjectConversionOptionTokens` 需加入 `make-readonly={opts.MakeReadOnly}` 指纹 token，
   使增量缓存能感知该开关变化（现有 `eliminate-goto` token 同款做法）。
2. `GetPreservedDestinationDirectories` 目前仅保留 `.cs2j-no-goto-src`；引入本预处理后需同时保留
   `.cs2j-readonly-src`，否则 `--force` 清理目标目录时会误删中间目录。

### 7.3 预处理流程集成

```
原始 C# 源码
     │
     ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 1: ReadOnlyStructMaker（如果启用）                    │
│  • 编译项目获取 CSharpCompilation                             │
│  • 构建调用图分析所有 struct                                  │
│  • 输出到中间目录 .cs2j-readonly-src/                         │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 2: GotoEliminator（如果启用）                         │
│  • 从 Phase 1 输出目录读取                                   │
│  • 转换后输出到 .cs2j-no-goto-src/                           │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
             后续 C# → Java 转换
```

### 7.4 中间目录结构

```
<destination>/
├── .cs2j-readonly-src/     ← ReadOnlyStructMaker 输出（如有）
│   ├── Project.csproj
│   └── **/*.cs
├── .cs2j-no-goto-src/      ← GotoEliminator 输出（如有）
│   ├── Project.csproj
│   └── **/*.cs
└── <module>/src/main/java/ ← 最终 Java 输出（单模块模式为 src/main/java）
```

### 7.5 ProjectReadOnlyStructPreprocessor.cs

新增项目级预处理器（类似 `ProjectGotoPreprocessor`）：

```csharp
internal static class ProjectReadOnlyStructPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-readonly-src";
    
    public static async Task<ProjectReadOnlyStructPreprocessResult> PreprocessAsync(
        ProjectReadOnlyStructPreprocessRequest request);
}
```

实现要点：

- 语义分析依赖完整引用：复制项目文件到中间目录后需执行 `dotnet restore`，再通过 `SolutionLoader`
  （MSBuildWorkspace）加载项目获得 `CSharpCompilation`（restore 流程参考 `ProjectGotoPreprocessor`）。
- 其输出的中间目录作为 GotoEliminator 预处理（`ProjectGotoPreprocessor`）的输入源，即
  `ProjectGotoPreprocessRequest.SourcePath` 指向 `.cs2j-readonly-src` 中的对应路径。

## 8. 错误处理

| 场景 | 处理方式 | 退出码 |
|------|----------|--------|
| 项目编译失败（无法获得语义模型） | 输出 Error 诊断并中止（CLI 与 convert-project 集成行为一致，同 goto 预处理失败处理） | 1 |
| 单个 struct 分析异常 | 记录 Error 诊断，继续处理其他 struct | 0 (或 1 if strict) |
| 所有 struct 可转换 | 正常完成 | 0 |
| 存在不可转换 struct | 输出 Warning 诊断 | 0 (或 1 if strict) |
| 文件 I/O 错误 | 输出错误，终止 | 1 |

## 9. 关键场景处理

### 9.1 递归方法调用

调用图中存在环的情况：

```csharp
struct S {
    readonly int x;
    void A() { B(); }  // A → B
    void B() { A(); }  // B → A (环)
}
```

**处理方式**：`CallGraphBuilder.ModifiesField` 使用 `visited` 集合防止无限递归。

### 9.2 显式接口方法实现

C# 的 struct 不支持 `virtual`/`override`，但可以实现接口方法（显式或隐式）。接口方法若被外部通过接口引用调用，可能间接修改字段。

```csharp
struct S : IResettable {
    readonly int _value;
    public void Reset() { Helpers.Clear(ref this); }  // 接口方法经 ref this 间接修改字段
}
```

注意：接口方法内的**直接字段赋值**已被条件 1（实例字段必须 readonly）先行拦截；纳入调用图的主要
价值在于捕获 `ref`/`out this` 逃逸（上例）及多层调用链中的间接修改。

**处理方式**：结构体实现的接口方法需纳入调用图分析。若任一接口实现方法直接或间接修改了实例字段，该 struct 标记为不可转换（保守策略——外部调用方可能通过接口引用触发字段修改）。此场景已在 D2 决策中确定纳入调用图分析。

### 9.3 this 作为 ref/out 传递

```csharp
struct S {
    int x;
    void M() { Helper(ref this); }
}
```

**处理方式**：检测方法体内是否存在 `ref this` 或 `out this` 传递，如有则标记为可能修改。

### 9.4 嵌套 struct

```csharp
struct Outer {
    readonly int value;
    struct Inner {
        int mutableField;  // Inner 不可转换
    }
}
```

**处理方式**：每个 struct 独立分析，嵌套 struct 不影响外层 struct 的判定。

### 9.5 泛型 struct

```csharp
struct Point<T> {
    readonly T X;
    readonly T Y;
}
```

**处理方式**：泛型参数不影响 readonly 转换判定。

## 10. 测试策略

### 10.1 单元测试

| 测试场景 | 测试名称 | 预期结果 |
|----------|----------|----------|
| 全 readonly 字段 | AllReadonlyFields_ConvertsSuccessfully | Changed=true |
| 含非 readonly 字段 | NonReadonlyField_SkipsWithWarning | Changed=false, Warning |
| 含外部 set 属性 | PropertyWithSetter_SkipsWithWarning | Changed=false, Warning |
| 方法修改字段 | MethodModifiesField_SkipsWithWarning | Changed=false, Warning |
| 已经是 readonly | AlreadyReadOnly_Skips | Changed=false, Info |
| ref struct | RefStruct_Skips | Changed=false, Info |
| partial struct | PartialStruct_Skips | Changed=false, Info |
| [DoNotMakeReadOnly] | DoNotMakeReadOnlyAttribute_Skips | Changed=false, Warning |
| 嵌套 struct | NestedStruct_ConvertsIndependently | 分别判断 |
| 泛型 struct | GenericStruct_ConvertsSuccessfully | 正确处理 |
| 含 Attribute | StructWithAttribute_PreservesAttribute | Attribute 保留 |
| 含 XML 注释 | StructWithXmlDoc_PreservesComments | XML 注释保留 |
| 空 struct | EmptyStruct_ConvertsSuccessfully | 可转换 |
| 静态方法修改静态字段 | StaticMethodOnly_ConvertsSuccessfully | 不影响 |
| 自动属性私有 set（支持字段非 readonly） | AutoPropertyPrivateSetter_SkipsWithWarning | Changed=false, Warning |
| this 以 ref/out 传递 | RefThisEscape_SkipsWithWarning | Changed=false, Warning |
| 调用链间接修改 | CallChainModifiesField_Skips | 递归检测成功 |

### 10.2 集成测试

| 测试场景 | 测试名称 | 预期结果 |
|----------|----------|----------|
| CLI 单文件 | Cli_SingleFile_Transforms | 输出含 readonly |
| CLI 目录 | Cli_Directory_CopiesAndTransforms | 仅转换目标 struct |
| convert-project 集成 | ConvertProject_MakeReadOnly_Integration | 正确执行预处理链 |

### 10.3 验证方法

转换后的 C# 代码必须编译通过：

```csharp
[Fact]
public void ConvertedCode_CompilesWithoutErrors()
{
    var input = "struct Point { public readonly int X; public readonly int Y; }";
    var tree = CSharpSyntaxTree.ParseText(input);
    var compilation = CSharpCompilation.Create("test")
        .AddSyntaxTrees(tree)
        .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    var result = new ReadOnlyStructMaker().MakeReadOnly(
        tree, compilation.GetSemanticModel(tree));
    
    Assert.True(result.Changed);
    var outputTree = CSharpSyntaxTree.ParseText(result.OutputCode!);
    Assert.False(outputTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error));
    Assert.Contains("readonly struct", result.OutputCode!);
}
```

## 11. 性能考虑

| 方面 | 措施 |
|------|------|
| 编译获取 | 本预处理在 convert-project 流程中早于主转换的编译发生，需自行经 SolutionLoader 加载项目获得 CSharpCompilation（存在项目二次加载开销；如需消除，可后续将主转换改为复用预处理的编译结果） |
| 调用图缓存 | 每个 struct 的调用图只构建一次，避免重复分析 |
| 短路判定 | 发现第一个不满足条件立即停止该 struct 的分析 |
| 并行处理 | 各 struct 分析相互独立，可并行处理（可选优化） |
| 增量处理 | 通过 fingerprint 机制跳过未变更的文件（复用现有缓存） |

## 12. 文件清单

### 新增文件

| 路径 | 说明 |
|------|------|
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs` | 公共入口 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs` | 配置选项 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs` | 诊断类型 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs` | 核心分析器 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs` | 调用图构建器 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs` | 语法重写器 |
| `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs` | 项目级预处理器 |
| `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs` | 单元测试 |

### 修改文件

| 路径 | 说明 |
|------|------|
| `src/CSharpToJava.CLI/Program.cs` | 新增 `make-readonly` 动词和 `MakeReadOnlyOptions`；`ConvertProjectOptions`（定义于该文件内）新增 `NoMakeReadOnly` 选项；`GetProjectConversionOptionTokens` 增加指纹 token；`GetPreservedDestinationDirectories` 保留新中间目录 |
| `README.md` | 更新文档 |

## 13. 与 GotoEliminator 的对比

| 方面 | GotoEliminator | ReadOnlyStructMaker |
|------|----------------|---------------------|
| 输入 | 单文件 / 目录 / 项目（经 convert-project 集成） | 项目编译或单文件 |
| 输出 | 无 goto 的 C# 源码 | 含 readonly struct 的 C# 源码 |
| 分析深度 | 语法树遍历 | 语义分析 + 调用图 |
| 依赖 | 无需编译 | 需要 SemanticModel |
| CLI 动词 | `eliminate-goto` | `make-readonly` |
| 中间目录 | `.cs2j-no-goto-src` | `.cs2j-readonly-src` |
| 执行顺序 | 第二阶段 | 第一阶段 |
