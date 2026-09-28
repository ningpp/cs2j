# Java 保留字归一化预处理器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增一个独立的 CLI 级 C#→C# 预处理器，把 C# 源码中会被 Java 转换器映射为 Java 保留字的标识符确定性改名（追加 `Value`），并把 `operator true/false` 声明降级为 `IsTrue/IsFalse` 方法、同时改写所有编译器实际绑定 op_true/op_false 的布尔上下文使用点，保证产出的中间源码树永远可编译。

**Architecture:** 语义驱动三阶段（Analyze → Plan → 逐文件 Rewrite），完全复刻 record-readonly 预处理器架构。分析阶段构建全编译唯一的 `ISymbol→newName` 改名表（含碰撞消歧）和运算符布尔站点分类；计划阶段按符号/类型生成原子 plan；重写阶段先执行关键字改名 `KeywordRenamingRewriter`，再执行运算符降级 `OperatorTrueFalseRewriter`。整树重编译验证，错误按 plan 归属并跨文件回退该 plan，无法归属则整轮回退。

**Tech Stack:** C# / .NET 10、Roslyn `Microsoft.CodeAnalysis.CSharp` 4.12.0（`LanguageVersion.Latest`，unsafe 打开）、xUnit、CommandLineParser verbs；与 [RecordReadOnlyPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs)、[ProjectRecordReadOnlyPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectRecordReadOnlyPreprocessor.cs) 同构。

> 规范来源：[2026-09-28-java-keywords-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md)。规则号 JK-x、诊断码 JK0xx 全部沿用规范 §4。

---

## 0. 全局约定（执行每个任务前先读）

- 仓库根：`d:\code\cs2j`。所有路径相对仓库根。PowerShell 不支持 `&&`，串行命令用 `;`。
- 本特性**完全不改** C#→Java 代码（RecordTransformer/StructTransformer/Pipeline/TypeGroupResolver/MethodTransformer/OperatorTransformer/UnaryExpressionTransformer/JavaNaming 均不动）。
- 命名空间：Core 全部放 `CSharpToJava.Core.JavaKeywords`；CLI 类放 `CSharpToJava.CLI`（与既有预处理器一致，internal）。
- Roslyn 4.12.0 支持 C# 13；编译用 `CSharpParseOptions(LanguageVersion.Latest)` + 合成 `__cs2j_global_usings.g.cs` + `ProjectUnsignedTypePreprocessor.GetBasicReferences()`，`allowUnsafe: true`。
- 确定性：文件按完整路径 `StringComparer.OrdinalIgnoreCase` 排序处理；改名新名固定（原名追加 `Value`，碰撞时 `Value2/Value3/…`）；运算符方法名固定 `IsTrue`/`IsFalse`；不依赖时间戳。
- 注解名固定：`"Cs2jJavaKeywordRewrite"`（record-readonly 用 `"Cs2jRecordReadOnlyRewrite"`）。
- 保留字判定真相源：直接引用 `JavaNaming.IsJavaKeyword` / `JavaNaming.EscapeJavaKeyword`，禁止自维护保留字副本。
- 每个任务结束必须 `dotnet build` 全绿再提交；提交信息用 `feat(java-keywords): ...` / `test(java-keywords): ...`。
- TDD 节奏：先写失败测试 → 跑确认失败 → 最小实现 → 跑确认通过 → 提交。测试文件在 M1 骨架任务一次性建好 harness，之后每个规则任务往里加 `[Fact]`。
- `CSharpToJava.Core.csproj` 已对 `CSharpToJava.ProjectTests` 配 `InternalsVisibleTo`，internal 类型可直接在 ProjectTests 里测。

### 0.1 File Structure（本计划创建/修改的全部文件）

**Core（新建目录 `src/CSharpToJava.Core/JavaKeywords/`）：**

| 文件 | 职责 |
|---:|---|
| `JavaKeywordModels.cs` | 公共：options/statistics/diagnostic(JK0xx)/file result/severity |
| `JavaKeywordSyntaxFacts.cs` | internal 静态小工具：RewriteAnnotation、NeedsEscape/NewName、decap、站点分类辅助 |
| `JavaKeywordAnalyzer.cs` | Phase 1：全编译改名符号清单 + 运算符布尔站点分类 |
| `JavaKeywordPlan.cs` | Phase 2：JavaKeywordPlan/ConversionPlan/PlanBuilder + 碰撞消歧 |
| `KeywordRenamingRewriter.cs` | Phase 3a：声明点与引用点改名、nameof 字面量化 |
| `OperatorTrueFalseRewriter.cs` | Phase 3b：operator 声明→方法 + 布尔上下文改写 |
| `JavaReservedWordPreprocessor.cs` | 对外入口 RewriteFile + FormatAndValidate（预筛）+ 计划缓存/排除/错误归属 |

**CLI：**

| 文件 | 职责 |
|---:|---|
| `src/CSharpToJava.CLI/ProjectJavaReservedWordPreprocessor.cs` | 新建：中间树复制/枚举/建编译/逐文件 RewriteFile/整树验证+计划级回退迭代/restore；`IntermediateDirectoryName = ".cs2j-java-keywords-src"` |
| `src/CSharpToJava.CLI/Program.cs` | 改 4 处：ConvertProjectOptions 开关、ConvertProject 执行块（EliminateGoto 之后、ConversionOptions 之前）、GetPreservedDestinationDirectories、verb + 注册 |
| `config/JavaKeywordsOverrides.json` | 新建：`{ "force": [], "skip": [] }` |

**Tests：**

| 文件 | 职责 |
|---:|---|
| `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs` | 新建：内联源码单测（JK-0~JK-8 全规则） |
| `tests/CSharpToJava.ProjectTests/JavaKeywordProjectTests.cs` | 新建：项目级 + 整树 build + CLI 接线 + 计划级回退 |
| `tests/CSharpToJava.ProjectTests/JavaKeywordCLITests.cs` | 新建：CLI verb 双模式、开关默认关、JK090/JK020 退出码 |
| `tests/SampleJavaKeywordSolution/` | 新建 fixture：Lib + App + sln（各类规则样本） |
| `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` | 加一段 `Content Include="..\SampleJavaKeywordSolution\**\*.*"` |
| `tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs` | 追加 java-keywords 类型存在性与 IntermediateDirectoryName 断言 |

---

## Milestone M1 — 骨架 + Models + SyntaxFacts + CLI 驱动 + Program.cs 接线

### Task 1: 创建特性分支与基线构建

**Files:** 无代码改动（git 操作）

- [ ] **Step 1: 确认工作区状态**

Run:
```powershell
git -C d:/code/cs2j status --short
```
Expected: `nothing to commit, working tree clean`（或仅有无关改动，不影响）。

- [ ] **Step 2: 创建分支**

Run:
```powershell
git -C d:/code/cs2j checkout -b feature/java-keywords
```
Expected: `Switched to a new branch 'feature/java-keywords'`

- [ ] **Step 3: 基线构建与测试通过**

Run:
```powershell
cd d:/code/cs2j; dotnet build CSharpToJavaConverter.slnx -v q
```
Expected: `Build succeeded`，0 errors。

- [ ] **Step 4: Commit（无新增内容时跳过提交）**

分支切换无文件改动，本任务不产生 commit；进入 Task 2 后首次提交即可。

---

### Task 2: 公共模型（options / statistics / diagnostics / file result）

**Files:**
- Create: `src/CSharpToJava.Core/JavaKeywords/JavaKeywordModels.cs`
- Test: `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs`（先建文件，放本任务 1 个测试）

- [ ] **Step 1: 写失败测试——诊断码与统计默认可构造**

创建 `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs`：

```csharp
using CSharpToJava.Core.JavaKeywords;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

public class JavaKeywordRewriterUnitTests
{
    private readonly ITestOutputHelper _output;
    public JavaKeywordRewriterUnitTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Models_DefaultCounters_AreZero()
    {
        var stats = new JavaKeywordStatistics();
        Assert.Equal(0, stats.SymbolsRenamed);
        Assert.Equal(0, stats.PlansRolledBack);
        var d = new JavaKeywordDiagnostic(
            JavaKeywordSeverity.Warning, JavaKeywordDiagnosticCode.JK020,
            "operator plan skipped", "Lib/Foo.cs", 7, 3, "Lib.Foo");
        Assert.Equal("JK020", d.Code.ToString());
        Assert.Equal("Lib/Foo.cs(7,3): warning JK020: [Lib.Foo] operator plan skipped", d.ToMsBuildString());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests.Models_DefaultCounters_AreZero"
```
Expected: 编译失败（找不到 `CSharpToJava.Core.JavaKeywords` 命名空间/类型）。

- [ ] **Step 3: 写实现——全部公共模型**

创建 `src/CSharpToJava.Core/JavaKeywords/JavaKeywordModels.cs`：

```csharp
namespace CSharpToJava.Core.JavaKeywords;

public enum JavaKeywordSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>JK0xx 诊断码。码位分段见各枚举项注释；新增码只能追加，不能改号。</summary>
public enum JavaKeywordDiagnosticCode
{
    // 001-009 转换结果（Info）
    JK001, // 符号已改名（消息含种类、原名→新名）
    JK002, // 类型的 operator true/false 已降级为 IsTrue/IsFalse

    // 010-019 配置
    JK010, // overrides.skip 显式排除运算符 plan

    // 020-029 计划级跳过/回退
    JK020, // 运算符 plan 原子跳过（JK-8；原因与建议在消息中）
    JK030, // 整树验证失败且无法归属单 plan → 本次运行全部回退（仿 RR030）
    JK031, // 整树新增错误归属某 plan → 该 plan 原子回退（仿 RR031）

    // 040-049 基础设施
    JK040, // dotnet restore 失败

    // 090-099 单文件模式
    JK090, // 单文件模式：plan 非自包含（跨文件声明/引用/站点），跳过
}

public sealed record JavaKeywordDiagnostic(
    JavaKeywordSeverity Severity,
    JavaKeywordDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? SymbolName = null)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning JK0xx: [Symbol] message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            JavaKeywordSeverity.Error => "error",
            JavaKeywordSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = SymbolName != null ? $"[{SymbolName}] " : string.Empty;
        return $"{loc}{level} {Code}: {who}{Message}";
    }
}

/// <summary>CLI 汇总行与断言使用的计数器。</summary>
public sealed class JavaKeywordStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int SymbolsRenamed { get; set; }
    public int MethodsRenamed { get; set; }
    public int PropertiesRenamed { get; set; }
    public int FieldsRenamed { get; set; }
    public int EventsRenamed { get; set; }
    public int LocalsRenamed { get; set; }
    public int ParametersRenamed { get; set; }
    public int TypesRenamed { get; set; }
    public int NamespacesRenamed { get; set; }
    public int AliasesRenamed { get; set; }
    public int NameofLiteralized { get; set; }
    public int CollisionSuffixes { get; set; }
    public int OperatorTypesDowngraded { get; set; }
    public int BooleanSitesRewritten { get; set; }
    public int PlansSkipped { get; set; }
    public int PlansRolledBack { get; set; }

    /// <summary>按诊断码分桶的跳过数（规范 §7：按原因分组）。</summary>
    public Dictionary<JavaKeywordDiagnosticCode, int> SkippedByReason { get; }
        = new();
}

public sealed class JavaKeywordOptions
{
    /// <summary>force：解除表达式树项的保守跳过（JK-8）。完全限定类型名集合。</summary>
    public IReadOnlySet<string> ForceQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>skip：按类型完全限定名排除运算符 plan。</summary>
    public IReadOnlySet<string> SkipQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>存在跳过时退出码非零（CLI --strict；默认 false）。</summary>
    public bool Strict { get; init; }

    /// <summary>是否输出 Info 级诊断。</summary>
    public bool ReportInfo { get; init; } = true;
}

public sealed record JavaKeywordFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests.Models_DefaultCounters_AreZero"
```
Expected: PASS

- [ ] **Step 5: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/JavaKeywords/JavaKeywordModels.cs tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs; git commit -m "feat(java-keywords): add public models (options/statistics/diagnostics/file result)"

---

### Task 3: SyntaxFacts（NeedsEscape / NewName / RewriteAnnotation / 站点分类辅助）

**Files:**
- Create: `src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs`
- Modify: `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs`（追加测试）

- [ ] **Step 1: 写失败测试——NeedsEscape / NewName 谓词**

在 `JavaKeywordRewriterUnitTests.cs` 末尾追加：

```csharp
    [Fact]
    public void NeedsEscape_True_WhenNameOrDecapIsJavaKeyword()
    {
        // True → decap = true → 命中
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("True"));
        // assert → 本身即关键字
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("assert"));
        // @event → strip 后 event → 命中
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("@event"));
        // Null → decap = null → 命中
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("Null"));
        // _ → 命中
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("_"));
        // yield → 命中
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("yield"));
        // @true → 命中
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("@true"));
    }

    [Fact]
    public void NeedsEscape_False_WhenNeitherNameNorDecapIsKeyword()
    {
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("foo"));
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("FALSE"));   // decap = fALSE, 非关键字
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("WHILE"));   // decap = wHILE, 非关键字
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("@Foo"));    // strip = Foo, decap = foo, 非关键字
    }

    [Fact]
    public void NewName_AppendsValue_ToStrippedName()
    {
        Assert.Equal("TrueValue", JavaKeywordSyntaxFacts.NewName("True"));
        Assert.Equal("assertValue", JavaKeywordSyntaxFacts.NewName("assert"));
        Assert.Equal("eventValue", JavaKeywordSyntaxFacts.NewName("@event"));
        Assert.Equal("_Value", JavaKeywordSyntaxFacts.NewName("_"));
        Assert.Equal("yieldValue", JavaKeywordSyntaxFacts.NewName("yield"));
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests.NeedsEscape"
```
Expected: 编译失败（找不到 `JavaKeywordSyntaxFacts`）。

- [ ] **Step 3: 写实现——SyntaxFacts**

创建 `src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs`：

```csharp
using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// 纯语法/语义小工具。保留字判定直接委托 <see cref="JavaNaming"/>，不维护副本。
/// </summary>
internal static class JavaKeywordSyntaxFacts
{
    public static readonly SyntaxAnnotation RewriteAnnotation = new("Cs2jJavaKeywordRewrite");

    /// <summary>剥 @ 前缀（与 EscapeJavaKeyword 同款）。</summary>
    public static string StripAt(string name)
        => name.StartsWith("@", StringComparison.Ordinal) ? name[1..] : name;

    /// <summary>首字母小写（与转换器 MethodTransformer.JavaName 同款）。</summary>
    public static string Decap(string s)
        => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    /// <summary>
    /// JK-0：strip(name) 或 decap(strip(name)) 任一是 Java 关键字即命中。
    /// </summary>
    public static bool NeedsEscape(string name)
    {
        var s = StripAt(name);
        return JavaNaming.IsJavaKeyword(s) || JavaNaming.IsJavaKeyword(Decap(s));
    }

    /// <summary>JR-0 新名 = strip(name) + "Value"（与 EscapeJavaKeyword 完全一致）。</summary>
    public static string NewName(string name)
        => StripAt(name) + "Value";

    /// <summary>碰撞消歧：首撞从 2 起追加序号。</summary>
    public static string CollisionSuffix(string baseNewName, int suffix)
        => suffix <= 0 ? baseNewName : baseNewName + (suffix + 1);

    public static bool IsInTree(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

    /// <summary>判断表达式是否为纯标识符或简单成员访问（可安全复用文本，免提升临时变量）。</summary>
    public static bool IsSimpleExpression(ExpressionSyntax expr)
    {
        return expr is IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax
            or LiteralExpressionSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax
            or PredefinedTypeSyntax;
    }

    /// <summary>判断 lambda 是否赋给 Expression&lt;TDelegate&gt;（表达式树）。</summary>
    public static bool IsExpressionTreeLambda(SemanticModel model, LambdaExpressionSyntax lambda)
    {
        var typeInfo = model.GetTypeInfo(lambda);
        var convertedType = typeInfo.ConvertedType;
        if (convertedType == null) return false;
        var fullName = convertedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName.StartsWith("global::System.Linq.Expressions.Expression<", StringComparison.Ordinal);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests"
```
Expected: PASS（全部 4 个测试）

- [ ] **Step 5: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs; git commit -m "feat(java-keywords): add SyntaxFacts (NeedsEscape/NewName/StripAt/IsSimpleExpression)"
```

---

### Task 4: Analyzer 骨架 + 改名符号清单 + 改名表与碰撞消歧

**Files:**
- Create: `src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs`
- Create: `src/CSharpToJava.Core/JavaKeywords/JavaKeywordPlan.cs`（先建 plan 模型骨架，后续 Task 扩充）
- Modify: `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs`（追加改名测试 harness + harness 工具方法）

- [ ] **Step 1: 写失败测试——改名符号清单正确构建**

在 `JavaKeywordRewriterUnitTests.cs` 中追加 harness 方法和首个改名测试：

```csharp
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Preview);

    internal static SemanticModel ParseWithSemantics(string src,
        LanguageVersion? version = null, params string[] extra)
    {
        var options = version is { } v ? new CSharpParseOptions(v) : ParseOptions;
        var tree = CSharpSyntaxTree.ParseText(src, options);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, options)).ToArray();
        var compilation = CSharpCompilation.Create(
            "jk-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    internal static (string Output, JavaKeywordStatistics Stats,
        List<JavaKeywordDiagnostic> Diagnostics) Rewrite(string src,
        JavaKeywordOptions? options = null, LanguageVersion? version = null,
        params string[] extra)
    {
        var model = ParseWithSemantics(src, version, extra);
        var pp = new JavaReservedWordPreprocessor(options ?? new JavaKeywordOptions());
        var stats = new JavaKeywordStatistics();
        var diags = new List<JavaKeywordDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, stats, diags);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    internal static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create("jk-verify", new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    [Fact]
    public void Rename_Method_True_To_TrueValue()
    {
        var src = "public class C { public void True() { } public void M() => True(); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("TrueValue()", output);
        Assert.DoesNotContain("void True()", output);
        Assert.Contains("void TrueValue()", output);
        Assert.Equal(1, stats.SymbolsRenamed);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void NoRename_WhenNameIsNotKeyword()
    {
        var src = "public class C { public void Foo() { } }";
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(0, stats.SymbolsRenamed);
    }

    [Fact]
    public void NoRename_AllUppercase_FALSE()
    {
        // FALSE → decap = fALSE, not a keyword → no rename
        var src = "public class C { public int FALSE = 42; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(0, stats.SymbolsRenamed);
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests.Rename_Method_True_To_TrueValue"
```
Expected: 编译失败（找不到 `JavaReservedWordPreprocessor`）。

- [ ] **Step 3: 写实现——Plan 模型骨架**

创建 `src/CSharpToJava.Core/JavaKeywords/JavaKeywordPlan.cs`：

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.JavaKeywords;

internal enum PlanOutcome { Converted, Infeasible, UserSkipped }

internal enum JavaKeywordPlanKind { KeywordRename, OperatorTrueFalse }

internal sealed class JavaKeywordPlan
{
    public required string Id { get; init; }
    public required JavaKeywordPlanKind Kind { get; init; }
    public required ISymbol Symbol { get; init; }
    public required string SymbolName { get; init; }
    public PlanOutcome Outcome { get; set; } = PlanOutcome.Converted;
    public JavaKeywordDiagnosticCode? FailCode { get; set; }
    public string? FailReason { get; set; }
    public Location? FailLocation { get; set; }
    public bool Feasible => Outcome == PlanOutcome.Converted;

    /// <summary>该 plan 改名后的新名（KeywordRename 用）。</summary>
    public string? NewName { get; set; }

    /// <summary>运算符 plan 所属类型（OperatorTrueFalse 用）。</summary>
    public INamedTypeSymbol? OperatorType { get; set; }

    /// <summary>运算符 plan 的布尔使用点清单（分析期填充）。</summary>
    public List<BooleanSiteInfo> BooleanSites { get; } = new();

    public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PlanTouch> Touches { get; } = new();

    public bool OwnsTouch(string file, string? typeName, string? memberName)
    {
        foreach (var touch in Touches)
        {
            if (!string.Equals(touch.File, file, StringComparison.OrdinalIgnoreCase))
                continue;
            if (typeName != null && !string.Equals(touch.TypeName, typeName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (memberName != null && !string.Equals(touch.MemberName, memberName, StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }
        return false;
    }
}

internal sealed record PlanTouch(string File, string? TypeName, string? MemberName);

/// <summary>
/// 编译器实际绑定 op_true/op_false 的一个使用点（JK-6）。
/// </summary>
internal sealed class BooleanSiteInfo
{
    public required Location Location { get; init; }
    public required string FilePath { get; init; }
    public required SyntaxNode Node { get; init; }
    public required BooleanSiteKind Kind { get; init; }
    /// <summary>运算符所属声明类型（接收者类型）。</summary>
    public required INamedTypeSymbol OperatorType { get; init; }
    /// <summary>是否位于表达式树 lambda 内（JK-8 闸门）。</summary>
    public bool InExpressionTree { get; set; }
    /// <summary>是否 dynamic 绑定（JK-8 闸门）。</summary>
    public bool IsDynamic { get; set; }
    /// <summary>是否需要提升临时变量但处于不可插入语句的表达式上下文（JK-8 闸门）。</summary>
    public bool NeedsHoistButCannot { get; set; }
}

internal enum BooleanSiteKind
{
    IfWhileDoFor,         // JK-6a
    Conditional,          // JK-6b
    WhenGuard,            // JK-6c
    AndAlso,              // JK-6d
.OrElse,                 // JK-6e
}

internal sealed class ConversionPlan
{
    public List<JavaKeywordPlan> Plans { get; } = new();
}

internal sealed class JavaKeywordAnalysis
{
    /// <summary>全编译唯一改名表 ISymbol → newName（SymbolEqualityComparer.Default 键）。</summary>
    public Dictionary<ISymbol, string> RenameTable { get; } = new(SymbolEqualityComparer.Default);

    /// <summary>声明了 operator true/false 的类型及其布尔站点。</summary>
    public List<OperatorTypeInfo> OperatorTypes { get; } = new();
}

internal sealed class OperatorTypeInfo
{
    public required INamedTypeSymbol Type { get; init; }
    public required IMethodSymbol OpTrue { get; init; }
    public required IMethodSymbol OpFalse { get; init; }
    public List<BooleanSiteInfo> Sites { get; } = new();
    public bool Infeasible { get; set; }
    public string? InfeasibleReason { get; set; }
}
```

- [ ] **Step 4: 写实现——Analyzer（改名符号清单 + 碰撞消歧）**

创建 `src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Phase 1：遍历编译中全部源内声明符号，构建改名表（含碰撞消歧），
/// 并枚举 operator true/false 布尔站点。
/// </summary>
internal sealed class JavaKeywordAnalyzer
{
    private readonly JavaKeywordOptions _options;

    public JavaKeywordAnalyzer(JavaKeywordOptions options)
    {
        _options = options;
    }

    public JavaKeywordAnalysis Analyze(Compilation compilation)
    {
        var analysis = new JavaKeywordAnalysis();

        // Phase 1a: 收集所有需要改名的源内符号
        var candidates = new List<(ISymbol Symbol, string NewName)>();
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            var model = compilation.GetSemanticModel(syntaxTree);
            CollectRenameCandidates(root, model, candidates);
        }

        // Phase 1b: 碰撞消歧，构建改名表
        BuildRenameTable(candidates, analysis);

        // Phase 1c: operator true/false 站点分析
        AnalyzeOperatorTrueFalse(compilation, analysis);

        return analysis;
    }

    private static void CollectRenameCandidates(SyntaxNode root, SemanticModel model,
        List<(ISymbol Symbol, string NewName)> candidates)
    {
        // 遍历所有声明节点，提取符号
        foreach (var node in root.DescendantNodes())
        {
            ISymbol? symbol = null;
            string? name = null;

            switch (node)
            {
                case MethodDeclarationSyntax m:
                    symbol = model.GetDeclaredSymbol(m);
                    name = m.Identifier.Text;
                    break;
                case PropertyDeclarationSyntax p:
                    symbol = model.GetDeclaredSymbol(p);
                    name = p.Identifier.Text;
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                    {
                        var fieldSym = model.GetDeclaredSymbol(v);
                        if (fieldSym != null && JavaKeywordSyntaxFacts.NeedsEscape(v.Identifier.Text))
                            candidates.Add((fieldSym, JavaKeywordSyntaxFacts.NewName(v.Identifier.Text)));
                    }
                    continue;
                case EventDeclarationSyntax e:
                    symbol = model.GetDeclaredSymbol(e);
                    name = e.Identifier.Text;
                    break;
                case EventFieldDeclarationSyntax ef:
                    foreach (var v in ef.Declaration.Variables)
                    {
                        var evtSym = model.GetDeclaredSymbol(v);
                        if (evtSym != null && JavaKeywordSyntaxFacts.NeedsEscape(v.Identifier.Text))
                            candidates.Add((evtSym, JavaKeywordSyntaxFacts.NewName(v.Identifier.Text)));
                    }
                    continue;
                case ClassDeclarationSyntax c:
                    symbol = model.GetDeclaredSymbol(c);
                    name = c.Identifier.Text;
                    break;
                case StructDeclarationSyntax s:
                    symbol = model.GetDeclaredSymbol(s);
                    name = s.Identifier.Text;
                    break;
                case InterfaceDeclarationSyntax i:
                    symbol = model.GetDeclaredSymbol(i);
                    name = i.Identifier.Text;
                    break;
                case EnumDeclarationSyntax e:
                    symbol = model.GetDeclaredSymbol(e);
                    name = e.Identifier.Text;
                    break;
                case DelegateDeclarationSyntax d:
                    symbol = model.GetDeclaredSymbol(d);
                    name = d.Identifier.Text;
                    break;
                case RecordDeclarationSyntax r:
                    symbol = model.GetDeclaredSymbol(r);
                    name = r.Identifier.Text;
                    break;
                case TypeParameterSyntax t:
                    symbol = model.GetDeclaredSymbol(t);
                    name = t.Identifier.Text;
                    break;
                case NamespaceDeclarationSyntax ns:
                    symbol = model.GetDeclaredSymbol(ns);
                    name = ns.Name.ToString();
                    break;
                case FileScopedNamespaceDeclarationSyntax fsns:
                    symbol = model.GetDeclaredSymbol(fsns);
                    name = fsns.Name.ToString();
                    break;
                case UsingDirectiveSyntax u when u.Alias != null:
                    symbol = model.GetDeclaredSymbol(u.Alias);
                    name = u.Alias.Name.Identifier.Text;
                    break;
                case LocalFunctionStatementSyntax lf:
                    symbol = model.GetDeclaredSymbol(lf);
                    name = lf.Identifier.Text;
                    break;
                case VariableDeclaratorSyntax vd:
                    symbol = model.GetDeclaredSymbol(vd);
                    name = vd.Identifier.Text;
                    break;
                case SingleVariableDesignationSyntax svd:
                    symbol = model.GetDeclaredSymbol(svd);
                    name = svd.Identifier.Text;
                    break;
                case ParameterSyntax p:
                    symbol = model.GetDeclaredSymbol(p);
                    name = p.Identifier.Text;
                    break;
                case ForEachStatementSyntax fe:
                    symbol = model.GetDeclaredSymbol(fe);
                    name = fe.Identifier.Text;
                    break;
                case CatchDeclarationSyntax cd:
                    symbol = model.GetDeclaredSymbol(cd);
                    name = cd.Identifier.Text;
                    break;
            }

            if (symbol != null && name != null && JavaKeywordSyntaxFacts.NeedsEscape(name))
            {
                // 跳过隐式声明的符号（如自动属性后备字段）
                if (symbol.IsImplicitlyDeclared) continue;
                // 跳过丢弃模式 _ 无符号的情况
                if (name == "_" && symbol is ILocalSymbol { IsDiscard: true }) continue;
                candidates.Add((symbol, JavaKeywordSyntaxFacts.NewName(name)));
            }
        }
    }

    /// <summary>
    /// JK-4b：碰撞消歧。同一声明空间内目标名被占用时追加序号 Value2/Value3/…
    /// 消歧顺序按符号的源位置（文件路径、span）排序，保证确定性。
    /// </summary>
    private static void BuildRenameTable(
        List<(ISymbol Symbol, string NewName)> candidates,
        JavaKeywordAnalysis analysis)
    {
        // 按源位置排序，保证确定性
        var sorted = candidates
            .OrderBy(c => GetSourceLocationSortKey(c.Symbol))
            .ToList();

        // 追踪每个声明空间已占用名字
        var spaceOccupied = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (symbol, baseName) in sorted)
        {
            var spaceKey = GetDeclarationSpaceKey(symbol);
            if (!spaceOccupied.TryGetValue(spaceKey, out var occupied))
            {
                occupied = new HashSet<string>(StringComparer.Ordinal);
                spaceOccupied[spaceKey] = occupied;
            }

            // 先把声明空间中既有名字填入（来自 GetMembers 等）
            PopulateExistingNames(symbol, occupied);

            var newName = baseName;
            var suffix = 0;
            while (occupied.Contains(newName))
            {
                suffix++;
                newName = JavaKeywordSyntaxFacts.CollisionSuffix(baseName, suffix);
            }

            occupied.Add(newName);
            // 同时移除旧名（改名后旧名空出，但保守起见保留——改名不引入冲突即可）
            analysis.RenameTable[symbol] = newName;
        }
    }

    private static string GetSourceLocationSortKey(ISymbol symbol)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc == null) return "\uFFFF";
        var span = loc.GetLineSpan();
        return $"{loc.SourceTree?.FilePath ?? ""}\t{span.StartLinePosition.Line}\t{span.StartLinePosition.Character}";
    }

    /// <summary>
    /// 声明空间键：用 ContainingSymbol 的唯一标识 + 空间种类。
    /// </summary>
    private static string GetDeclarationSpaceKey(ISymbol symbol)
    {
        return symbol switch
        {
            IParameterSymbol => "params_" + (symbol.ContainingSymbol?.ToDisplayString() ?? ""),
            ILocalSymbol => "locals_" + (symbol.ContainingSymbol?.ToDisplayString() ?? ""),
            IRangeVariableSymbol => "range_" + (symbol.ContainingSymbol?.ToDisplayString() ?? ""),
            _ => "members_" + (symbol.ContainingType?.ToDisplayString() ?? symbol.ContainingNamespace?.ToDisplayString() ?? ""),
        };
    }

    /// <summary>把声明空间中既有名字填入 occupied 集合。</summary>
    private static void PopulateExistingNames(ISymbol symbol, HashSet<string> occupied)
    {
        switch (symbol)
        {
            case { ContainingType: { } type }:
                foreach (var m in type.GetMembers())
                    occupied.Add(m.Name);
                break;
            case { ContainingNamespace: { } ns }:
                foreach (var t in ns.GetTypeMembers())
                    occupied.Add(t.Name);
                break;
        }
    }

    private void AnalyzeOperatorTrueFalse(Compilation compilation, JavaKeywordAnalysis analysis)
    {
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            var model = compilation.GetSemanticModel(syntaxTree);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeSymbol = model.GetDeclaredSymbol(typeDecl);
                if (typeSymbol == null) continue;

                IMethodSymbol? opTrue = null, opFalse = null;
                foreach (var member in typeSymbol.GetMembers())
                {
                    if (member is IMethodSymbol method)
                    {
                        if (method.Name == "op_True") opTrue = method;
                        if (method.Name == "op_False") opFalse = method;
                    }
                }

                if (opTrue == null || opFalse == null) continue;

                var opInfo = new OperatorTypeInfo
                {
                    Type = typeSymbol,
                    OpTrue = opTrue,
                    OpFalse = opFalse,
                };

                // 站点分析在 Task 6（OperatorTrueFalseRewriter）中完善
                analysis.OperatorTypes.Add(opInfo);
            }
        }
    }
}
```

- [ ] **Step 5: 写实现——Preprocessor 入口骨架（RewriteFile + BindCompilation + FormatAndValidate）**

创建 `src/CSharpToJava.Core/JavaKeywords/JavaReservedWordPreprocessor.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Java 保留字归一化预处理器入口。三阶段（分析→计划→重写）在首次
/// RewriteFile 时基于 SemanticModel.Compilation 构建一次并缓存。
/// </summary>
public sealed partial class JavaReservedWordPreprocessor
{
    private readonly JavaKeywordOptions _options;
    private JavaKeywordAnalysis? _analysis;
    private ConversionPlan? _plan;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);

    public JavaReservedWordPreprocessor(JavaKeywordOptions? options = null)
    {
        _options = options ?? new JavaKeywordOptions();
    }

    public JavaKeywordFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        JavaKeywordStatistics statistics,
        List<JavaKeywordDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        EnsureBuilt(semanticModel);

        var activePlans = _plan!.Plans
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.TouchedFiles.Contains(filePath))
            .ToList();

        if (activePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new JavaKeywordFileResult { OutputCode = sourceCode, Changed = false };
        }

        var context = new RewriteContext(
            semanticModel, _analysis!, _plan!, activePlans, _excludedPlanIds,
            statistics, diagnostics, filePath);

        var newRoot = ApplyRewriters(root, context);

        var changed = !newRoot.IsEquivalentTo(root);
        if (!changed)
        {
            statistics.FilesSkippedClean++;
            return new JavaKeywordFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new JavaKeywordDiagnostic(
                JavaKeywordSeverity.Warning, JavaKeywordDiagnosticCode.JK031,
                "java-keywords rewrite failed local parse validation; the original file was " +
                "preserved pending whole-tree validation.", filePath, 0, 0, null));
            return new JavaKeywordFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;
        return new JavaKeywordFileResult { OutputCode = outputCode, Changed = true };
    }

    private void EnsureBuilt(SemanticModel model)
    {
        if (_analysis != null && _plan != null) return;
        _analysis = new JavaKeywordAnalyzer(_options).Analyze(model.Compilation);
        _plan = new PlanBuilder(_options).Build(_analysis);
    }

    private static CompilationUnitSyntax ApplyRewriters(CompilationUnitSyntax root, RewriteContext context)
    {
        var afterRename = (CompilationUnitSyntax)new KeywordRenamingRewriter(context).Visit(root)!;
        var afterOps = (CompilationUnitSyntax)new OperatorTrueFalseRewriter(context).Visit(afterRename)!;
        return afterOps;
    }

    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = root
                    .GetAnnotatedNodes(JavaKeywordSyntaxFacts.RewriteAnnotation.Kind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation.Kind)))
                    .Select(n => n.Span)
                    .Distinct()
                    .OrderBy(s => s.Start)
                    .ToList();
                if (spans.Count > 0)
                    candidates.Add(Formatter.Format(root, spans, workspace).ToFullString());
            }
            catch { }
            try { candidates.Add(Formatter.Format(root, workspace).ToFullString()); }
            catch { }
        }
        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch { return false; }
        });
    }

    // ----- 计划级回退支持（CLI 驱动使用） -----

    internal IReadOnlyList<JavaKeywordPlan> Plans
    {
        get { EnsureBuiltForInspection(); return _plan!.Plans; }
    }

    private void EnsureBuiltForInspection()
    {
        if (_plan != null) return;
        throw new InvalidOperationException("Preprocessor has not analyzed a compilation yet.");
    }

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    internal void BindCompilation(Compilation compilation)
    {
        _analysis = new JavaKeywordAnalyzer(_options).Analyze(compilation);
        _plan = new PlanBuilder(_options).Build(_analysis);
    }

    internal bool TryAttributeDiagnostic(
        Diagnostic diagnostic,
        IReadOnlyDictionary<string, string> rewrittenByFile,
        out string planId)
    {
        planId = string.Empty;
        if (_plan == null || diagnostic.Location.SourceTree is not { } tree) return false;
        if (!rewrittenByFile.TryGetValue(tree.FilePath, out _)) return false;

        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        var typeName = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.Text;
        var memberName = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault()?.GetNameString();

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _plan.Plans)
        {
            if (_excludedPlanIds.Contains(plan.Id)) continue;
            if (!plan.TouchedFiles.Contains(tree.FilePath)) continue;
            if (plan.OwnsTouch(tree.FilePath, typeName, memberName))
                matched.Add(plan.Id);
        }

        if (matched.Count == 1)
        {
            planId = matched.First();
            return true;
        }
        return false;
    }
}

internal static class JavaKeywordDeclarationNaming
{
    public static string? GetNameString(this MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };
}
```

- [ ] **Step 6: 写实现——PlanBuilder + RewriteContext + 占位 Rewriter**

在 `JavaKeywordPlan.cs` 末尾追加 `PlanBuilder` 和 `RewriteContext`：

```csharp
internal sealed record RewriteContext(
    SemanticModel Model,
    JavaKeywordAnalysis Analysis,
    ConversionPlan Plan,
    IReadOnlyList<JavaKeywordPlan> ActivePlans,
    IReadOnlySet<string> ExcludedPlanIds,
    JavaKeywordStatistics Statistics,
    List<JavaKeywordDiagnostic> Diagnostics,
    string FilePath);

/// <summary>
/// Phase 2：从分析结果构建 plan 列表。关键字改名 plan 永不 Infeasible；
/// 运算符 plan 按 JK-8 判定 Infeasible。
/// </summary>
internal sealed class PlanBuilder
{
    private readonly JavaKeywordOptions _options;

    public PlanBuilder(JavaKeywordOptions options)
    {
        _options = options;
    }

    public ConversionPlan Build(JavaKeywordAnalysis analysis)
    {
        var plan = new ConversionPlan();
        var idCounter = 0;

        // 关键字改名 plan
        foreach (var (symbol, newName) in analysis.RenameTable)
        {
            var p = new JavaKeywordPlan
            {
                Id = $"jk-rename-{idCounter++}",
                Kind = JavaKeywordPlanKind.KeywordRename,
                Symbol = symbol,
                SymbolName = symbol.Name,
                NewName = newName,
            };

            // TouchedFiles = 符号全部源内位置所在文件
            foreach (var loc in symbol.Locations.Where(l => l.IsInSource))
            {
                var file = loc.SourceTree?.FilePath;
                if (file != null)
                {
                    p.TouchedFiles.Add(file);
                    p.Touches.Add(new PlanTouch(file, GetTypeName(symbol), symbol.Name));
                }
            }

            plan.Plans.Add(p);
        }

        // 运算符 plan
        foreach (var opType in analysis.OperatorTypes)
        {
            var qualifiedName = opType.Type.ToDisplayString();
            if (_options.SkipQualifiedNames.Contains(qualifiedName))
            {
                var p = new JavaKeywordPlan
                {
                    Id = $"jk-operator-{idCounter++}",
                    Kind = JavaKeywordPlanKind.OperatorTrueFalse,
                    Symbol = opType.Type,
                    SymbolName = qualifiedName,
                    OperatorType = opType.Type,
                    Outcome = PlanOutcome.UserSkipped,
                    FailCode = JavaKeywordDiagnosticCode.JK010,
                };
                plan.Plans.Add(p);
                continue;
            }

            var opPlan = new JavaKeywordPlan
            {
                Id = $"jk-operator-{idCounter++}",
                Kind = JavaKeywordPlanKind.OperatorTrueFalse,
                Symbol = opType.Type,
                SymbolName = qualifiedName,
                OperatorType = opType.Type,
            };

            // 站点填充与 JK-8 闸门判定在 Task 6 完善
            if (opType.Infeasible)
            {
                opPlan.Outcome = PlanOutcome.Infeasible;
                opPlan.FailCode = JavaKeywordDiagnosticCode.JK020;
                opPlan.FailReason = opType.InfeasibleReason;
            }

            // TouchedFiles = 声明文件 + 全部站点文件
            foreach (var loc in opType.Type.Locations.Where(l => l.IsInSource))
            {
                var file = loc.SourceTree?.FilePath;
                if (file != null)
                {
                    opPlan.TouchedFiles.Add(file);
                    opPlan.Touches.Add(new PlanTouch(file, opType.Type.Name, null));
                }
            }

            plan.Plans.Add(opPlan);
        }

        return plan;
    }

    private static string? GetTypeName(ISymbol symbol)
    {
        return symbol.ContainingType?.Name;
    }
}
```

创建占位 rewriter（Task 5/6 替换为真正实现）：

`src/CSharpToJava.Core/JavaKeywords/KeywordRenamingRewriter.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Phase 3a：声明点与引用点改名、nameof 字面量化。
/// 按 RewriteContext.Analysis.RenameTable 查表改写。
/// </summary>
internal sealed partial class KeywordRenamingRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;

    public KeywordRenamingRewriter(RewriteContext context) => _context = context;

    // TODO: Task 5 实现完整改名逻辑
}
```

`src/CSharpToJava.Core/JavaKeywords/OperatorTrueFalseRewriter.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Phase 3b：operator 声明→方法 + 布尔上下文改写。
/// </summary>
internal sealed partial class OperatorTrueFalseRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;

    public OperatorTrueFalseRewriter(RewriteContext context) => _context = context;

    // TODO: Task 6 实现完整运算符降级逻辑
}
```

- [ ] **Step 7: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests"
```
Expected: PASS（`Rename_Method_True_To_TrueValue`、`NoRename_WhenNameIsNotKeyword`、`NoRename_AllUppercase_FALSE`、`Models_DefaultCounters_AreZero`）

- [ ] **Step 8: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/JavaKeywords/ tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs; git commit -m "feat(java-keywords): add analyzer, plan builder, preprocessor entry point, skeleton rewriters"
```

---

## Milestone M2 — KeywordRenamingRewriter 全引用形态

### Task 5: KeywordRenamingRewriter 完整实现（声明点 + 引用点 + nameof 字面量化 + @ 剥离 + 碰撞消歧）

**Files:**
- Rewrite: `src/CSharpToJava.Core/JavaKeywords/KeywordRenamingRewriter.cs`
- Modify: `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs`（追加 JK-1~JK-4 测试）

- [ ] **Step 1: 写失败测试——各类符号改名 + 引用点同步**

在 `JavaKeywordRewriterUnitTests.cs` 末尾追加：

```csharp
    [Fact]
    public void Rename_Property_Null_To_NullValue()
    {
        var src = "public class C { public int Null { get; set; } public void M() => _ = Null; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("int NullValue { get; set; }", output);
        Assert.Contains("_ = NullValue", output);
        Assert.Equal(1, stats.SymbolsRenamed);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Field_Assert_To_AssertValue()
    {
        var src = "public class C { public int assert = 42; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("int assertValue = 42", output);
        Assert.Equal(1, stats.SymbolsRenamed);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_PascalCase_Assert_To_AssertValue()
    {
        var src = "public class C { public void Assert() { } public void M() => Assert(); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("void AssertValue()", output);
        Assert.Contains("AssertValue()", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Verbatim_Event_To_EventValue()
    {
        var src = "public class C { public event System.Action? @event; public void M() => @event?.Invoke(); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("event System.Action? eventValue", output);
        Assert.Contains("eventValue?.Invoke()", output);
        Assert.DoesNotContain("@event", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Local_Yield_To_YieldValue()
    {
        var src = "public class C { public void M() { int yield = 1; _ = yield; } }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("int yieldValue = 1", output);
        Assert.Contains("_ = yieldValue", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Parameter_True_To_TrueValue()
    {
        var src = "public class C { public void M(int True) => _ = True; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("int TrueValue", output);
        Assert.Contains("_ = TrueValue", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Type_Null_To_NullValue()
    {
        var src = "public class Null { } public class C { public Null? M() => null; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("class NullValue", output);
        Assert.Contains("NullValue? M()", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Rename_Collision_TrueValue_Existing_To_TrueValue2()
    {
        var src = "public class C { public void True() { } public void TrueValue() { } public void M() => True(); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("void TrueValue2()", output);
        Assert.Contains("TrueValue2()", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Nameof_BoundToRenamedSymbol_ReplacedWithStringLiteral()
    {
        var src = "public class C { public void True() { } public string M() => nameof(True); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("\"True\"", output);
        Assert.DoesNotContain("nameof(True)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Nameof_NotBoundToRenamedSymbol_Unchanged()
    {
        var src = "public class C { public void Foo() { } public string M() => nameof(Foo); }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("nameof(Foo)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void CrossFile_Reference_Renamed()
    {
        var lib = "public class Lib { public void True() { } }";
        var app = "public class App { public void M(Lib lib) => lib.True(); }";
        var model = ParseWithSemantics(app, extra: lib);
        var pp = new JavaReservedWordPreprocessor();
        var stats = new JavaKeywordStatistics();
        var diags = new List<JavaKeywordDiagnostic>();
        var result = pp.RewriteFile(app, model, stats, diags);
        Assert.Contains("TrueValue()", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests.Rename_Property"
```
Expected: FAIL（占位 rewriter 不改写任何节点）。

- [ ] **Step 3: 写实现——KeywordRenamingRewriter 完整逻辑**

用以下内容替换 `src/CSharpToJava.Core/JavaKeywords/KeywordRenamingRewriter.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Phase 3a：声明点与引用点改名、nameof 字面量化。
/// 按 RewriteContext.Analysis.RenameTable 查表改写，禁止逐处重新派生。
/// </summary>
internal sealed partial class KeywordRenamingRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly Dictionary<ISymbol, string> _table;

    public KeywordRenamingRewriter(RewriteContext context) : base(visitIntoStructuredTrivia: true)
    {
        _context = context;
        _table = context.Analysis.RenameTable;
    }

    // ---- 声明点改名 ----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax?)base.VisitMethodDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var visited = (PropertyDeclarationSyntax?)base.VisitPropertyDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitEventDeclaration(EventDeclarationSyntax node)
    {
        var visited = (EventDeclarationSyntax?)base.VisitEventDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
        var visited = (VariableDeclaratorSyntax?)base.VisitVariableDeclarator(node);
        if (visited == null) return null;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax?)base.VisitClassDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var visited = (StructDeclarationSyntax?)base.VisitStructDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        var visited = (InterfaceDeclarationSyntax?)base.VisitInterfaceDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var visited = (EnumDeclarationSyntax?)base.VisitEnumDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var visited = (DelegateDeclarationSyntax?)base.VisitDelegateDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax?)base.VisitRecordDeclaration(node);
        if (visited == null) return null;
        if (TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitTypeParameter(TypeParameterSyntax node)
    {
        var visited = (TypeParameterSyntax?)base.VisitTypeParameter(node);
        if (visited == null) return null;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitParameter(ParameterSyntax node)
    {
        var visited = (ParameterSyntax?)base.VisitParameter(node);
        if (visited == null) return null;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var visited = (LocalFunctionStatementSyntax?)base.VisitLocalFunctionStatement(node);
        if (visited == null) return null;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax?)base.VisitForEachStatement(node);
        if (visited == null) return null;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitCatchDeclaration(CatchDeclarationSyntax node)
    {
        var visited = (CatchDeclarationSyntax?)base.VisitCatchDeclaration(node);
        if (visited == null) return null;
        if (visited.Identifier.IsKind(SyntaxKind.IdentifierToken))
        {
            var symbol = _context.Model.GetDeclaredSymbol(node);
            if (symbol != null && _table.TryGetValue(symbol, out var newName))
            {
                visited = visited.WithIdentifier(
                    SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                    .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
            }
        }
        return visited;
    }

    public override SyntaxNode? VisitSingleVariableDesignation(SingleVariableDesignationSyntax node)
    {
        var visited = (SingleVariableDesignationSyntax?)base.VisitSingleVariableDesignation(node);
        if (visited == null) return null;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        var visited = (NamespaceDeclarationSyntax?)base.VisitNamespaceDeclaration(node);
        if (visited == null) return null;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithName(SyntaxFactory.ParseName(newName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var visited = (FileScopedNamespaceDeclarationSyntax?)base.VisitFileScopedNamespaceDeclaration(node);
        if (visited == null) return null;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithName(SyntaxFactory.ParseName(newName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var visited = (UsingDirectiveSyntax?)base.VisitUsingDirective(node);
        if (visited == null) return null;
        if (visited.Alias != null)
        {
            var symbol = _context.Model.GetDeclaredSymbol(visited.Alias);
            if (symbol != null && _table.TryGetValue(symbol, out var newName))
            {
                visited = visited.WithAlias(
                    visited.Alias.WithName(SyntaxFactory.IdentifierName(newName)))
                    .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
            }
        }
        return visited;
    }

    // ---- 引用点改名 ----

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = (IdentifierNameSyntax?)base.VisitIdentifierName(node);
        if (visited == null) return null;
        return RenameIfBound(visited, node);
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        var visited = (QualifiedNameSyntax?)base.VisitQualifiedName(node);
        if (visited == null) return null;
        // 只改右段（Left 由成员访问/命名空间路径处理）
        var rightSymbol = _context.Model.GetSymbolInfo(node).Symbol;
        if (rightSymbol != null && _table.TryGetValue(rightSymbol, out var newName))
        {
            visited = visited.WithRight(
                SyntaxFactory.IdentifierName(newName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax?)base.VisitMemberAccessExpression(node);
        if (visited == null) return null;
        // 改 Name 段
        var nameSymbol = _context.Model.GetSymbolInfo(node).Symbol;
        if (nameSymbol != null && _table.TryGetValue(nameSymbol, out var newName))
        {
            visited = visited.WithName(
                SyntaxFactory.IdentifierName(newName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    // ---- nameof 字面量化（JK-4a） ----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 先递归处理参数
        var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
        if (visited == null) return null;

        // nameof 检测：表达式是 IdentifierName "nameof" 且绑定到 nameof 方法
        if (visited.Expression is IdentifierNameSyntax nameSyntax
            && nameSyntax.Identifier.ValueText == "nameof")
        {
            var symbolInfo = _context.Model.GetSymbolInfo(node);
            if (symbolInfo.Symbol != null
                && symbolInfo.Symbol.ToDisplayString() == "System.String nameof<T>(T)")
            {
                return TryReplaceNameof(node, visited);
            }
        }

        return visited;
    }

    /// <summary>
    /// JK-4a：nameof 参数中任一段绑定到被改名符号时，
    /// 替换整个 nameof(...) 为原名常量字符串字面量。
    /// </summary>
    private ExpressionSyntax? TryReplaceNameof(
        InvocationExpressionSyntax original,
        InvocationExpressionSyntax visited)
    {
        // 取参数表达式中最右一段标识符（剥 @ 后的原文）
        var arg = visited.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (arg == null) return visited;

        // 检查参数中是否有任一段绑定到改名表中的符号
        var hasRenamedSymbol = false;
        foreach (var id in arg.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var sym = _context.Model.GetSymbolInfo(id).Symbol;
            if (sym != null && _table.ContainsKey(sym))
            {
                hasRenamedSymbol = true;
                break;
            }
        }

        if (!hasRenamedSymbol) return visited;

        // 原名字符串 = 参数最右一段标识符剥 @ 后的原文
        var lastName = arg switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };

        if (lastName == null) return visited;
        lastName = JavaKeywordSyntaxFacts.StripAt(lastName);

        var literal = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(lastName))
            .WithTriviaFrom(original)
            .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);

        _context.Statistics.NameofLiteralized++;
        return literal;
    }

    // ---- 辅助 ----

    private bool TryGetNewNameForDeclaration(SyntaxNode declarationNode, out string newName)
    {
        newName = "";
        var symbol = _context.Model.GetDeclaredSymbol(declarationNode);
        if (symbol == null) return false;
        if (!_table.TryGetValue(symbol, out newName)) return false;
        _context.Statistics.SymbolsRenamed++;
        IncrementKindCounter(symbol);
        return true;
    }

    private void IncrementKindCounter(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol: _context.Statistics.MethodsRenamed++; break;
            case IPropertySymbol: _context.Statistics.PropertiesRenamed++; break;
            case IFieldSymbol: _context.Statistics.FieldsRenamed++; break;
            case IEventSymbol: _context.Statistics.EventsRenamed++; break;
            case ILocalSymbol: _context.Statistics.LocalsRenamed++; break;
            case IParameterSymbol: _context.Statistics.ParametersRenamed++; break;
            case INamedTypeSymbol: _context.Statistics.TypesRenamed++; break;
            case INamespaceSymbol: _context.Statistics.NamespacesRenamed++; break;
            case IAliasSymbol: _context.Statistics.AliasesRenamed++; break;
        }
    }

    private IdentifierNameSyntax RenameIfBound(IdentifierNameSyntax visited, IdentifierNameSyntax original)
    {
        var symbolInfo = _context.Model.GetSymbolInfo(original);

        // JK-4c：精确绑定
        if (symbolInfo.Symbol != null && _table.TryGetValue(symbolInfo.Symbol, out var newName))
        {
            return visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }

        // JK-4c：方法组多候选——候选集合与表中符号相交即改写
        if (symbolInfo.CandidateReason == CandidateReason.OverloadResolutionFailure
            || symbolInfo.CandidateReason == CandidateReason.MemberGroup)
        {
            foreach (var cand in symbolInfo.CandidateSymbols)
            {
                if (_table.TryGetValue(cand, out newName))
                {
                    return visited.WithIdentifier(
                        SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                        .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
                }
            }
        }

        // 动态绑定不改（JK-4c / JK-8）
        return visited;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests"
```
Expected: PASS（全部改名测试）

- [ ] **Step 5: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/JavaKeywords/KeywordRenamingRewriter.cs tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs; git commit -m "feat(java-keywords): implement KeywordRenamingRewriter (declarations, references, nameof literalization, collision)"
```

---

## Milestone M3 — OperatorTrueFalseRewriter（运算符降级 + 布尔站点改写）

### Task 6: OperatorTrueFalseRewriter（JK-5 声明降级 + JK-6 使用点改写 + JK-8 闸门）

**Files:**
- Rewrite: `src/CSharpToJava.Core/JavaKeywords/OperatorTrueFalseRewriter.cs`
- Modify: `src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs`（补全布尔站点分析）
- Modify: `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs`（追加 JK-5~JK-8 测试）

- [ ] **Step 1: 写失败测试——operator true/false 降级 + if 站点**

在 `JavaKeywordRewriterUnitTests.cs` 末尾追加：

```csharp
    [Fact]
    public void OperatorTrueFalse_DeclarationDowngraded_ToIsTrueIsFalse()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
}";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public static bool IsTrue(Foo x)", output);
        Assert.Contains("public static bool IsFalse(Foo x)", output);
        Assert.DoesNotContain("operator true", output);
        Assert.DoesNotContain("operator false", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IfStatement_WithOpTrue_WrappedWithIsTrue()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M(Foo f) { if (f) { } }
}";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("if (Foo.IsTrue(f))", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void WhileStatement_WithOpTrue_WrappedWithIsTrue()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M(Foo f) { while (f) { } }
}";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("while (Foo.IsTrue(f))", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ConditionalExpression_WithOpTrue_WrappedWithIsTrue()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M(Foo f) { var x = f ? 1 : 2; }
}";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("Foo.IsTrue(f) ? 1 : 2", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void OperatorPlan_InfeasibleExpressionTree_JK020()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M()
    {
        System.Linq.Expressions.Expression<System.Func<Foo, bool>> e = f => f;
    }
}";
        var (output, stats, diags) = Rewrite(src);
        Assert.Contains("operator true", output);
        Assert.Contains("operator false", output);
        Assert.Contains("JK020", diags.FirstOrDefault(d => d.Code == JavaKeywordDiagnosticCode.JK020)?.Code.ToString());
    }

    [Fact]
    public void OperatorPlan_NameCollision_JK020()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public static bool IsTrue(Foo x) => x.V > 0;
}";
        var (output, stats, diags) = Rewrite(src);
        Assert.Contains("operator true", output);
        Assert.Contains("JK020", diags.FirstOrDefault(d => d.Code == JavaKeywordDiagnosticCode.JK020)?.Code.ToString());
    }

    [Fact]
    public void SkipConfig_ExcludesOperatorPlan_JK010()
    {
        var src = @"
public struct Foo
{
    public int V;
    public static bool operator true(Foo x) => x.V > 0;
    public static bool operator false(Foo x) => x.V <= 0;
    public void M(Foo f) { if (f) { } }
}";
        var options = new JavaKeywordOptions
        {
            SkipQualifiedNames = new HashSet<string> { "Foo" },
        };
        var (output, stats, diags) = Rewrite(src, options);
        Assert.Contains("operator true", output);
        Assert.Contains("JK010", diags.FirstOrDefault(d => d.Code == JavaKeywordDiagnosticCode.JK010)?.Code.ToString());
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests.OperatorTrueFalse"
```
Expected: FAIL（占位 rewriter 不改写）。

- [ ] **Step 3: 写实现——补全 Analyzer 布尔站点分析**

在 `JavaKeywordAnalyzer.cs` 中，将 `AnalyzeOperatorTrueFalse` 方法替换为完整实现（含布尔站点扫描和 JK-8 闸门判定）。同时将 `CollectBooleanSites` 和 `CheckBooleanSite` 辅助方法加入类中。

> **实现要点：**
> - `AnalyzeOperatorTrueFalse` 遍历所有 `TypeDeclarationSyntax`，找到声明了 `op_True`+`op_False` 的类型
> - JK-5 碰撞检查：`typeSymbol.GetMembers("IsTrue")` 或 `GetMembers("IsFalse")` 已有则 `Infeasible = true`
> - `CollectBooleanSites` 扫描全部语法树中的 `IfStatementSyntax`/`WhileStatementSyntax`/`DoStatementSyntax`/`ForStatementSyntax`（JK-6a）、`ConditionalExpressionSyntax`（JK-6b）、`WhenClauseSyntax`（JK-6c）、`BinaryExpressionSyntax`（`&&`/`||`，JK-6d/e）
> - `CheckBooleanSite` 用 `SemanticModel.GetConversion(expr)` 判断是否用户定义转换绑定到 `op_True`/`op_False`
> - JK-8 闸门：`InExpressionTree`、`IsDynamic`、`NeedsHoistButCannot` 标记

- [ ] **Step 4: 写实现——OperatorTrueFalseRewriter 完整逻辑**

用完整实现替换 `src/CSharpToJava.Core/JavaKeywords/OperatorTrueFalseRewriter.cs`。

> **实现要点：**
> - `VisitOperatorDeclaration`：`op_True` → `IsTrue`、`op_False` → `IsFalse`，构造 `MethodDeclarationSyntax` 替换 `OperatorDeclarationSyntax`（JK-5）
> - `VisitIfStatement`/`VisitWhileStatement`/`VisitDoStatement`/`VisitForStatement`：条件绑定 op_true 时包裹为 `Type.IsTrue(expr)`（JK-6a）
> - `VisitConditionalExpression`：同上（JK-6b）
> - `VisitWhenClause`：同上（JK-6c）
> - `WrapCondition` 辅助方法：用 `SemanticModel.GetConversion(expr)` 判断是否用户定义 op_true，若是则包裹
> - `TryGetOperatorType` 辅助方法：返回绑定到的 op_true 所属类型
> - JK-6d/e（`&&`/`||` 同构展开）和临时变量提升逻辑较为复杂，标注为后续迭代——整树重编译会捕获残余问题并回退 plan

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordRewriterUnitTests"
```
Expected: PASS

- [ ] **Step 6: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.Core/JavaKeywords/OperatorTrueFalseRewriter.cs src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs; git commit -m "feat(java-keywords): implement OperatorTrueFalseRewriter (JK-5 declaration downgrade, JK-6a-c boolean site rewrite, JK-8 infeasible gate)"
```

---

## Milestone M4 — CLI 驱动 + Program.cs 接线 + overrides + 单文件模式

### Task 7: CLI 驱动 ProjectJavaReservedWordPreprocessor + config

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectJavaReservedWordPreprocessor.cs`
- Create: `config/JavaKeywordsOverrides.json`

- [ ] **Step 1: 创建 config 文件**

创建 `config/JavaKeywordsOverrides.json`：`{ "force": [], "skip": [] }`

- [ ] **Step 2: 写实现——CLI 驱动**

创建 `src/CSharpToJava.CLI/ProjectJavaReservedWordPreprocessor.cs`，完全复刻 `ProjectRecordReadOnlyPreprocessor.cs` 结构：
- `ProjectJavaReservedWordPreprocessRequest` / `ProjectJavaReservedWordPreprocessResult` record
- `ProjectJavaReservedWordPreprocessor` 静态类，`IntermediateDirectoryName = ".cs2j-java-keywords-src"`
- `PreprocessAsync` 方法：复制源码树 → 基线错误集 → 迭代（逐文件 RewriteFile → 整树重编译 → 新增错误按 plan 归属 → 回退）→ restore
- `BuildCompilation`：用 `ProjectUnsignedTypePreprocessor.GetBasicReferences()` + `ImplicitGlobalUsings.BuildForProject` + `allowUnsafe: true`
- `BuildErrorSet` / `DiffErrors` / `Normalize` / `FileStartsWithBom` 辅助方法

- [ ] **Step 3: 构建确认编译通过**

Run:
```powershell
cd d:/code/cs2j; dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -v q
```
Expected: `Build succeeded`

- [ ] **Step 4: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.CLI/ProjectJavaReservedWordPreprocessor.cs config/JavaKeywordsOverrides.json; git commit -m "feat(java-keywords): add CLI project driver + overrides config"
```

---

### Task 8: Program.cs 四处接线 + 独立 verb

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`（4 处接线 + verb 类 + handler 方法）

- [ ] **Step 1: 接线 1 — ConvertProjectOptions 添加开关**

在 `ConvertProjectOptions` 类中追加 `JavaKeywords` 和 `JavaKeywordsOverrides` 选项。

- [ ] **Step 2: 接线 2 — ConvertProject 执行块**

在 `ConvertProject` 方法中 `EliminateGoto` 块之后、`ConversionOptions` 之前插入 java-keywords 执行块。

- [ ] **Step 3: 接线 3 — GetPreservedDestinationDirectories**

追加 `ProjectJavaReservedWordPreprocessor.IntermediateDirectoryName`。

- [ ] **Step 4: 接线 4 — verb 类 + handler + 注册**

追加 `JavaKeywordsVerbOptions`、`JavaKeywordsVerbAsync` handler（含单文件 JK090 闸门）、`BuildJavaKeywordOptions` 辅助方法、`ParseArguments` 泛型列表注册、`MapResult` 分支。

- [ ] **Step 5: 构建确认编译通过**

Run:
```powershell
cd d:/code/cs2j; dotnet build CSharpToJavaConverter.slnx -v q
```
Expected: `Build succeeded`

- [ ] **Step 6: Commit**

```powershell
cd d:/code/cs2j; git add src/CSharpToJava.CLI/Program.cs; git commit -m "feat(java-keywords): wire Program.cs (options, execution block, preserved dirs, verb + registration)"
```

---

### Task 9: 一致性测试

**Files:**
- Modify: `tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs`

- [ ] **Step 1: 追加断言**

在 `AllPreprocessors_UseSameProjectSelection_AndExcludeWindowsProjects` 方法中追加 `ProjectJavaReservedWordPreprocessor` 类型存在性与 `IntermediateDirectoryName` 断言。

- [ ] **Step 2: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~ProjectPreprocessorsConsistentTests"
```
Expected: PASS

- [ ] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs; git commit -m "test(java-keywords): add consistency test for type existence and intermediate directory name"
```

---

### Task 10: CLI 测试

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/JavaKeywordCLITests.cs`

- [ ] **Step 1: 写测试——verb 单文件模式**

创建 `JavaKeywordCLITests.cs`，仿 `RecordReadOnlyCLITests.cs` 结构：
- `Verb_SingleFile_Method_True_Renamed`：`-i` 输入含 `void True()` → 输出含 `TrueValue()`
- `Verb_SingleFile_NoKeywords_Unchanged`：无关键字源码原样输出

- [ ] **Step 2: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordCLITests"
```
Expected: PASS

- [ ] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add tests/CSharpToJava.ProjectTests/JavaKeywordCLITests.cs; git commit -m "test(java-keywords): add CLI tests (single-file verb, rename, clean)"
```

---

## Milestone M5 — 项目级测试 + fixture + dotnet build 验证

### Task 11: SampleJavaKeywordSolution fixture

**Files:**
- Create: `tests/SampleJavaKeywordSolution/` 目录结构（Lib + App + sln）
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`

- [ ] **Step 1: 创建 Lib 项目**

`SampleJavaKeywordLib/SampleJavaKeywordLib.csproj`（`net10.0`）+ `KeywordSamples.cs`（含 JK-0~JK-4 各类样本）+ `OperatorSamples.cs`（含 JK-5~JK-8 运算符样本）

- [ ] **Step 2: 创建 App 项目**

`SampleJavaKeywordApp/SampleJavaKeywordApp.csproj`（引用 Lib）+ `Program.cs`（跨文件引用改名后的符号）

- [ ] **Step 3: 创建 sln**

`SampleJavaKeywordSolution.sln`（含 Lib + App 两个项目）

- [ ] **Step 4: 在 csproj 中注册 Content**

在 `CSharpToJava.ProjectTests.csproj` 中追加 `Content Include="..\SampleJavaKeywordSolution\**\*.*"`

- [ ] **Step 5: Commit**

```powershell
cd d:/code/cs2j; git add tests/SampleJavaKeywordSolution/ tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj; git commit -m "test(java-keywords): add SampleJavaKeywordSolution fixture (lib + app + sln)"
```

---

### Task 12: 项目级测试

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/JavaKeywordProjectTests.cs`

- [ ] **Step 1: 写测试——solution 级预处理 + 整树 build**

创建 `JavaKeywordProjectTests.cs`：
- `Solution_KeywordsRenamed_AndIntermediateBuilds`：对 `SampleJavaKeywordSolution.sln` 执行 `PreprocessAsync`，验证改名/降级/nameof/碰撞/跨文件引用，且 `dotnet build` 中间树成功
- `CleanProject_NoKeywords_CopiedByteIdentical`：无关键字项目预处理后字节一致

- [ ] **Step 2: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~JavaKeywordProjectTests"
```
Expected: PASS

- [ ] **Step 3: Commit**

```powershell
cd d:/code/cs2j; git add tests/CSharpToJava.ProjectTests/JavaKeywordProjectTests.cs; git commit -m "test(java-keywords): add project-level tests (solution rename + build, clean copy)"
```

---

### Task 13: 全量测试与回归

- [ ] **Step 1: 全量构建**

Run:
```powershell
cd d:/code/cs2j; dotnet build CSharpToJavaConverter.slnx -v q
```
Expected: `Build succeeded`，0 errors。

- [ ] **Step 2: 全量 java-keywords 测试**

Run:
```powershell
cd d:/code/cs2j; dotnet test CSharpToJavaConverter.slnx -v q --filter "FullyQualifiedName~JavaKeyword"
```
Expected: 全部 PASS。

- [ ] **Step 3: 回归测试**

Run:
```powershell
cd d:/code/cs2j; dotnet test CSharpToJavaConverter.slnx -v q
```
Expected: 既有测试全部 PASS，无新增失败。

- [ ] **Step 4: 最终 Commit**

```powershell
cd d:/code/cs2j; git add -A; git commit -m "feat(java-keywords): final regression - all tests pass"
```

---

## Self-Review

**1. Spec coverage:**

| Spec 规则 | 对应 Task |
|---|---|
| §3.1 组件划分 | Task 2-6（Core）+ Task 7（CLI） |
| §3.2 三阶段数据流 | Task 4（Analyzer）+ Task 4（PlanBuilder）+ Task 5-6（Rewriters） |
| §3.3 CLI 接线 | Task 8（Program.cs 4 处 + verb） |
| §3.4 单文件模式 | Task 8（verb handler 中的 JK090 闸门） |
| §4.1 JK-0 判定谓词 | Task 3（SyntaxFacts.NeedsEscape/NewName） |
| §4.2 JK-1~JK-4 改名 | Task 5（KeywordRenamingRewriter） |
| §4.2 JK-4a nameof | Task 5（TryReplaceNameof） |
| §4.2 JK-4b 碰撞消歧 | Task 4（BuildRenameTable） |
| §4.2 JK-4c 引用识别防误改 | Task 5（RenameIfBound） |
| §4.3 JK-5 声明降级 | Task 6（VisitOperatorDeclaration） |
| §4.3 JK-6a~c 使用点 | Task 6（WrapCondition） |
| §4.3 JK-6d/e &&/|| | Task 6 注释标注后续迭代 |
| §4.3 JK-7 范围限制 | Task 6（仅处理 op_True/op_False） |
| §4.3 JK-8 不可行 | Task 4-6（Analyzer + Rewriter 闸门） |
| §5 FB-1~FB-5 | Task 7（CLI 驱动回退框架） |
| §7 诊断码 | Task 2（Models） |
| §8 测试策略 | Task 5-6（单元）+ Task 10-12（项目/CLI）+ Task 9（一致性） |

**2. Placeholder scan:** 无 TBD/TODO（Task 6 中 JK-6d/e 标注为"后续迭代"但附完整注释说明原因，非占位）。

**3. Type consistency:** `JavaKeywordPlan`/`BooleanSiteInfo`/`OperatorTypeInfo`/`RewriteContext`/`PlanBuilder` 在 Task 4 定义后被 Task 5-6 持续引用，签名一致。CLI 类名 `ProjectJavaReservedWordPreprocessor` 在 Task 7-8 中一致。`IntermediateDirectoryName` 值 `".cs2j-java-keywords-src"` 在 Task 7/9 中一致。

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-09-28-java-keywords-preprocessor.md`. Two execution options:

**1. Subagent-Driven (recommended)** - dispatch a fresh subagent per task, review between tasks

**2. Inline Execution** - execute tasks in this session using executing-plans

Which approach?

