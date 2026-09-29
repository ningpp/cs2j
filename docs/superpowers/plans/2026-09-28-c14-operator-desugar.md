# C14 Operator 去糖（Operators 规则）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 member-desugar pass（C11 锚点已合入）内新增 C14 `Operators/` 规则：把源内用户自定义运算符与 implicit/explicit 转换运算符的声明归约为普通静态方法，并把全部调用点（二元/一元/复合赋值/隐式与显式转换站点，含提升形态）按 SemanticModel 绑定显式化为静态调用/转换链，产物为可编译 C#。

**Architecture:** 复用 C11 三阶段（分析→计划→逐文件重写）与整树迭代回退。`OperatorRule`（`[MemberDesugarRule(MemberDesugarKind.Operator, 40)]` 程序集扫描发现）自扫声明与站点并产出 `OperatorPlan`（每符号一 plan，原子单位）；`OperatorUseSiteRewriter` 在 `RewriteFile` 内**先于** Property rewriter 执行（原节点实例可绑定、合成节点不可绑定的机械约束），重写期对原始节点做 GetSymbolInfo/GetOperation 即时判定。共享文件只按设计 §3.2「post-merge 追加式编辑清单」做三处纯追加（Models 码位+计数器 / Preprocessor 派发 / Adapter 摘要行）。

**Tech Stack:** .NET 10 / C# (Roslyn Microsoft.CodeAnalysis 4.x Workspaces) / xunit；测试仿 C11 `MemberDesugarPropertyUnitTests` 内联源码管线；项目级走 `MemberDesugarPassAdapter` + `ProjectPassRunner`。

**Worktree:** `.worktrees/preproc-c14-operator-desugar`，分支 `feature/preproc-c14-operator-desugar`（base `67477551` + merge C11 `ac9bakab`，已完成）。**所有命令都在该 worktree 根执行。**

**设计文档:** [2026-09-28-c14-operator-desugar-design.md](../specs/2026-09-28-c14-operator-desugar-design.md)（评审修订版，§13 修订记录）。关键决策索引：命名=决策 1/1b/3/8；站点=决策 2/2b/4/5；边界=决策 6/7/12；链式顺序=§4.5；共享文件清单=§3.2。

**里程碑映射:** Task 1 = M0；Task 2-5 = M1；Task 6 = M2；Task 7-8 = M3；Task 9-11 = M4；Task 12 = 验收。

---

### Task 1: M0 — 探针 + 共享文件追加（Models 码位/计数器、Preprocessor 派发、Adapter 摘要行）

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs`（追加 5170-5183 枚举 + 9 个运算符计数器）
- Modify: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs`（RewriteFile 追加 OperatorPlan 派发：operator 先、property 后；UserSkipped 消息泛化）
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/MemberDesugarPassAdapter.cs`（BuildChainSummaryLine 追加 operators 分项）
- Create: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorProbes.cs`（探针 + 码位/派发占位测试）

- [ ] **Step 1.1: 写失败测试（探针：op_* 手写方法合法性事实 + implicit/explicit 同对共存）**

Create `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorProbes.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>M0 探针（设计决策 1/3、§9-M0）：只记录语言事实，不 gate 命名决策。</summary>
public class MemberDesugarOperatorProbes
{
    private static CSharpCompilation Compile(string src) => CSharpCompilation.Create(
        "probe",
        new[] { CSharpSyntaxTree.ParseText(src, new CSharpParseOptions(LanguageVersion.Preview)) },
        new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void Probe_OpMetadataNameAsMethod_DeclaredLegalityIsRecorded()
    {
        // 决策 1 评审修订：op_* 命名的充分依据是零漂移对齐；本探针只钉死「手写是否合法」事实。
        var comp = Compile("""
            public class C
            {
                public static int op_Addition(int a, int b) => a + b;
            }
            """);
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        // 无论合法与否都记录：合法则 0 错误；若未来编译器收紧，这里给出确切诊断号。
        Assert.True(errs.Count == 0,
            "op_Addition as plain method rejected: " + string.Join("; ", errs.Select(e => e.Id)));
        var tree = comp.SyntaxTrees.Single();
        Assert.Contains(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.ValueText == "op_Addition");
    }

    [Fact]
    public void Probe_ImplicitAndExplicitSamePair_IsRejectedByCSharp()
    {
        var comp = Compile("""
            public class C
            {
                public static implicit operator int(C c) => 1;
                public static explicit operator int(C c) => 2;
            }
            """);
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(errs); // 预期 CS0557 duplicate conversion —— 同 (S,T) 对两版共存非法
        Assert.Contains(errs, e => e.Id == "CS0557");
    }
}
```

- [ ] **Step 1.2: 运行探针**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorProbes"`
Expected: 2 PASS（探针记录事实；若 Probe_OpMetadataName 失败则按事实更新断言并记录到设计 §13，不影响后续任务）

- [ ] **Step 1.3: 向 MemberDesugarModels.cs 追加诊断码位与计数器**

在 `MemberDesugarDiagnosticCode` 枚举 `WithSite = 5124,` 之后追加：

```csharp
    // 5170-5183 operator rules (C14)
    OperatorPlanCreated = 5170,      // Info，仅 verbose
    ConversionPlanCreated = 5171,    // Info，仅 verbose
    OperatorNameDisambiguated = 5172,// Info，仅 verbose
    OperatorExpressionTreeSite = 5173, // Warning（strict Error）；force 可解除
    OperatorDynamicSite = 5174,      // Warning（strict Error）；force 不解除
    StaticAbstractInterfaceOperator = 5175, // Warning（strict Error）
    UnbuildableLift = 5176,          // Warning（strict Error）
    ConversionMirrorMiss = 5177,     // Warning（strict Error）
    OperatorNameExhausted = 5178,    // Warning（strict Error），理论兜底
    CompoundTargetUnsupported = 5179,// Warning（strict Error）
    GateOperatorRetained = 5180,     // Info：门控类型 & / | 保留供 JK
    IncrementDecrementDeferred = 5181, // Info：移交 C17
    MetadataOnlyOperatorSite = 5182, // Info：元数据-only 站点不动
    ResidualOperator = 5183,         // Warning（strict Error）：residual 闸门（OC-9）
```

在 `MemberDesugarStatistics` 的 `PlansRolledBack` 之后追加：

```csharp
    // C14 operators（设计 §3.2 追加清单）
    public int OperatorsPlanned { get; set; }
    public int OperatorBinarySitesRewritten { get; set; }
    public int OperatorUnarySitesRewritten { get; set; }
    public int OperatorCompoundSitesRewritten { get; set; }
    public int OperatorConversionSitesRewritten { get; set; }
    public int OperatorLiftedSitesExpanded { get; set; }
    public int GateOperatorsRetained { get; set; }
    public int IncrementDecrementDeferred { get; set; }
    public int MetadataOperatorSitesUntouched { get; set; }
```

- [ ] **Step 1.4: 改写 MemberDesugarPreprocessor.RewriteFile 为两族派发（operator 先、property 后）**

把 `RewriteFile` 中段（`var planned = active.OfType<Properties.PropertyPlan>()...` 到 `var newRoot = ...rewriter.Visit(root)!;`）替换为：

```csharp
        var plannedProps = active.OfType<Properties.PropertyPlan>()
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedOps = active.OfType<Operators.OperatorPlan>()
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        if (plannedProps.Count == 0 && plannedOps.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }
        statistics.PropertiesPlanned += plannedProps.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.OperatorsPlanned += plannedOps.Count(p => IsFirstDeclarationFile(p, filePath));

        // 链式顺序（设计 §4.5）：Operator 先、Property 后——未改写子树返回原节点实例，
        // 原始 SemanticModel 只能绑定原实例；先跑属性会重建运算符站点祖先并合成不可绑定的内层。
        var current = root;
        if (plannedOps.Count > 0)
        {
            var opRewriter = new Operators.OperatorUseSiteRewriter(
                semanticModel, plannedOps,
                active.OfType<Operators.OperatorPlan>().ToList(),
                _options, statistics, diagnostics, filePath);
            current = (CompilationUnitSyntax)opRewriter.Visit(current)!;
        }
        if (plannedProps.Count > 0)
        {
            var rewriter = new Properties.PropertyUseSiteRewriter(
                semanticModel, plannedProps, active.OfType<Properties.PropertyPlan>().ToList(),
                _options, statistics, diagnostics, filePath);
            current = (CompilationUnitSyntax)rewriter.Visit(current)!;
        }
        var newRoot = current;
```

同时把 `EmitSkipDiagnostics` 中 UserSkipped 消息 `"property plan skipped by overrides.skip configuration"` 改为 `"member-desugar plan skipped by overrides.skip configuration"`。

- [ ] **Step 1.5: Adapter 摘要行追加 operators 分项**

`MemberDesugarPassAdapter.BuildChainSummaryLine()` 末尾 return 前追加取出，改为：

```csharp
    public override string BuildChainSummaryLine()
    {
        _statistics.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualProperty, out var residual);
        _statistics.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualOperator, out var residualOp);
        return $"member-desugar: {_statistics.PropertiesPlanned} propert(y/ies) planned, " +
               $"{_statistics.BackingFieldsGenerated} field(s) generated, " +
               $"{_statistics.ReadSitesRewritten} read/{_statistics.WriteSitesRewritten} write/" +
               $"{_statistics.CompoundSitesRewritten} compound site(s) rewritten, " +
               $"{_statistics.InitializersRewritten} initializer(s), " +
               $"{_statistics.PlansSkipped} skipped, " +
               $"{_statistics.PlansRolledBack} rolled back, " +
               $"{residual} residual" +
               $" | operators: {_statistics.OperatorsPlanned} planned, " +
               $"{_statistics.OperatorBinarySitesRewritten} binary/" +
               $"{_statistics.OperatorUnarySitesRewritten} unary/" +
               $"{_statistics.OperatorCompoundSitesRewritten} compound/" +
               $"{_statistics.OperatorConversionSitesRewritten} conversion site(s), " +
               $"{_statistics.OperatorLiftedSitesExpanded} lifted, " +
               $"{_statistics.GateOperatorsRetained} gate-retained, " +
               $"{_statistics.IncrementDecrementDeferred} deferred(++/--), " +
               $"{_statistics.MetadataOperatorSitesUntouched} metadata-only, " +
               $"{residualOp} operator-residual";
    }
```

- [ ] **Step 1.6: 编译 + C11 属性回归绿（此时代码尚不编译——OperatorPlan/OperatorUseSiteRewriter 未建；本步在 Task 3 完成后复验，先只跑属性单测以外的检查）**

> 注：Step 1.4 引用了 `Operators.OperatorPlan` / `Operators.OperatorUseSiteRewriter`，在 Task 2/3 创建骨架后编译通过。为保持每步可编译，本任务先提交 Models/Adapter/探针，Preprocessor 派发改到 Task 3 骨架就绪后落地。

修正执行序：
- [ ] **Step 1.6a: 本任务只提交 Step 1.1/1.3/1.5**（Models 码位+计数器、Adapter 摘要行、探针）；运行 `dotnet build CSharpToJavaConverter.slnx -c Debug` 绿；`dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarPropertyUnitTests|FullyQualifiedName~MemberDesugarProjectTests|FullyQualifiedName~MemberDesugarOperatorProbes"` 全绿（属性零回归）。
- [ ] **Step 1.7: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): C14 M0 — operator diagnostic codes 5170-5183, statistics counters, adapter summary segment, language probes"
```

---

### Task 2: OperatorNames / OperatorFacts / OperatorDiagnostics（命名与谓词层）

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorDiagnostics.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorFacts.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorNames.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`（新建，本任务先写命名层测试）

- [ ] **Step 2.1: 写失败测试（命名表逐名对齐 + 镜像表 + 碰撞升级）**

Create `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`，先放 harness 与命名测试（harness 复用 `MemberDesugarPropertyUnitTests.Rewrite`，internal 同程序集可见）：

```csharp
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>C14 运算符去糖 Core 单测矩阵（设计 §7.1）。</summary>
public class MemberDesugarOperatorUnitTests
{
    internal static (string OutputCode, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diagnostics)
        Rewrite(string src, MemberDesugarOptions? options = null, params string[] extra)
        => MemberDesugarPropertyUnitTests.Rewrite(src, options, extra);

    internal static void AssertOutputCompiles(string output, params string[] extra)
        => MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, extra);

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    internal static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    internal static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    // ---- 决策 1：ConvertOperatorName 与 OpSymbolToJavaName 的 PascalCase 逐名对齐 ----

    [Theory]
    [InlineData("op_Addition", "Add")]
    [InlineData("op_Subtraction", "Subtract")]
    [InlineData("op_Multiply", "Multiply")]
    [InlineData("op_Division", "Divide")]
    [InlineData("op_Modulus", "Mod")]
    [InlineData("op_Equality", "ValueEquals")]
    [InlineData("op_Inequality", "NotEquals")]
    [InlineData("op_GreaterThan", "GreaterThan")]
    [InlineData("op_LessThan", "LessThan")]
    [InlineData("op_GreaterThanOrEqual", "GreaterThanOrEqual")]
    [InlineData("op_LessThanOrEqual", "LessThanOrEqual")]
    [InlineData("op_BitwiseAnd", "And")]
    [InlineData("op_BitwiseOr", "Or")]
    [InlineData("op_ExclusiveOr", "Xor")]
    [InlineData("op_LogicalNot", "Not")]
    [InlineData("op_OnesComplement", "OnesComplement")]
    [InlineData("op_LeftShift", "LeftShift")]
    [InlineData("op_RightShift", "RightShift")]
    [InlineData("op_UnsignedRightShift", "UnsignedRightShift")]
    [InlineData("op_UnaryNegation", "Negate")]
    [InlineData("op_UnaryPlus", "Plus")]
    [InlineData("op_CheckedAddition", "AddChecked")]
    [InlineData("op_CheckedSubtraction", "SubtractChecked")]
    [InlineData("op_CheckedMultiply", "MultiplyChecked")]
    public void OperatorNames_PascalCaseMirrorsConverterJavaNames(string metadataName, string expected)
    {
        Assert.Equal(expected, OperatorNames.ConvertOperatorName(metadataName));
        // 零漂移闭环：C# 名经 MethodTransformer.JavaName（首字母小写）== 转换器今发 Java 名。
        var javaToday = Transformers.Member.OperatorTransformer.OpSymbolToJavaName[
            metadataName.StartsWith("op_Checked", StringComparison.Ordinal)
                ? "op_" + metadataName["op_Checked".Length..]
                : metadataName];
        Assert.Equal(javaToday,
            char.ToLowerInvariant(expected[0]) + expected[1..]);
    }

    // ---- 决策 3：转换命名镜像表 ----

    [Theory]
    [InlineData("double", "ToDouble")]
    [InlineData("int", "ToInt")]
    [InlineData("long", "ToLong")]
    [InlineData("float", "ToFloat")]
    [InlineData("bool", "ToBoolean")]
    [InlineData("string", "ToString2")]   // ToString 与 object.ToString 冲突 → 消歧路径见碰撞测试
    [InlineData("decimal", "ToDecimal")]
    [InlineData("byte", "ToByte")]
    [InlineData("byte[]", "ToByteArray")]
    [InlineData("int[]", "ToIntArray")]
    public void OperatorNames_ConversionMirrorMatchesConverterSuffix(string targetType, string baseName)
    {
        // 基名 = "To" + BuildConversionMethodSuffix(javaType) 的 PascalCase 形态
        Assert.Equal(baseName, "To" + MirrorSuffix(targetType));
    }

    private static string MirrorSuffix(string javaType)
        => OperatorNames.TryGetConversionBaseSuffix(javaType, out var s) ? s : "";

    // ---- 决策 8：碰撞升级 ----

    [Fact]
    public void OperatorNames_CollisionUpgradesDeterministically()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "Add" };
        Assert.Equal("AddOperator", OperatorNames.Disambiguate("Add", taken));
        taken.Add("AddOperator");
        Assert.Equal("AddOperator2", OperatorNames.Disambiguate("Add", taken));
        taken.Add("AddOperator2");
        Assert.Equal("AddOperator3", OperatorNames.Disambiguate("Add", taken));
        taken.Clear();
        Assert.Equal("Add", OperatorNames.Disambiguate("Add", taken));
    }
}
```

- [ ] **Step 2.2: 运行确认失败**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: 编译失败（OperatorNames 不存在）

- [ ] **Step 2.3: 实现 OperatorDiagnostics / OperatorFacts / OperatorNames**

Create `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorDiagnostics.cs`:

```csharp
namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>CS2J5170-5183 消息（设计 §6）。格式统一 `[符号] 消息`，符号名由调用方拼接。</summary>
internal static class OperatorDiagnostics
{
    public static string PlanCreated(string name, int siteCount)
        => $"operator plan created ({name}, {siteCount} site(s))";
    public static string ConversionPlanCreated(string name, string targetType, string kind)
        => $"conversion operator plan created ({name}, {kind} → {targetType})";
    public static string NameDisambiguated(string baseName, string actualName)
        => $"generated name '{baseName}' collides with an existing member; using '{actualName}'";
    public static string ExpressionTree(string name)
        => $"operator/conversion '{name}' is referenced inside an expression-tree lambda; " +
           "the plan (declaration and all sites) is skipped atomically (overrides.force can release this)";
    public static string Dynamic(string name)
        => $"operator '{name}' may be dynamically dispatched; the plan is skipped atomically (force does not release this)";
    public static string StaticAbstractInterface(string name)
        => $"interface static abstract/virtual operator '{name}' (C# 11 generic math) is not supported; plan skipped";
    public static string UnbuildableLift(string name)
        => $"lifted operator/conversion '{name}' site cannot be rebuilt from the operation " +
           "(non-simple operand or unsupported lifted shape); plan skipped";
    public static string MirrorMiss(string name, string targetType)
        => $"conversion target type '{targetType}' is outside the naming mirror table; plan skipped (sdk triage)";
    public static string NameExhausted(string baseName)
        => $"name reservation for '{baseName}' exhausted disambiguation candidates; plan skipped";
    public static string CompoundUnsupported(string name, string reason)
        => $"compound-assignment target is unsupported ({reason}); the operator plan " +
           $"'{name}' is skipped atomically";
    public static string GateRetained(string typeName, string opName)
        => $"gate type '{typeName}' declares op_True/op_False; '{opName}' is retained for the java-keywords channel";
    public static string IncrementDeferred(string typeName)
        => $"op_Increment/op_Decrement on '{typeName}' is deferred to C17; declaration and sites are untouched";
    public static string MetadataSite(string opName)
        => $"site binds metadata-only operator '{opName}' (BCL/compat); left untouched";
    public static string Residual(string name)
        => $"operator/conversion declaration '{name}' has neither a plan nor a skip diagnostic (residual gate)";
}
```

Create `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorFacts.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>谓词层（设计 §4.1 OperatorFacts）：一切以绑定符号为准。</summary>
internal static class OperatorFacts
{
    public static bool IsIncrementOrDecrement(string metadataName)
        => metadataName is "op_Increment" or "op_Decrement";

    public static bool IsTrueOrFalse(string metadataName)
        => metadataName is "op_True" or "op_False";

    public static bool IsGateAndOr(string metadataName)
        => metadataName is "op_BitwiseAnd" or "op_BitwiseOr";

    /// <summary>声明了 op_True/op_False 的门控类型（设计决策 7④：其 & / | 保留给 JK）。</summary>
    public static bool IsGateType(INamedTypeSymbol type)
        => type.GetMembers().OfType<IMethodSymbol>().Any(m
            => IsTrueOrFalse(m.Name)
               && (m.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion
                   || m.MethodKind == MethodKind.Ordinary && m.IsStatic)); // 元数据形态兜底

    public static bool HasSourceDeclaration(IMethodSymbol m)
        => m.Locations.Any(l => l.IsInSource);

    /// <summary>接口 static abstract/virtual 运算符（C# 11 generic math，CS2J5175）。</summary>
    public static bool IsInterfaceStaticAbstract(IMethodSymbol m)
        => m.ContainingType is { TypeKind: TypeKind.Interface } && m.IsStatic
           && (m.IsAbstract || m.IsVirtual);

    public static bool IsUserDefinedOperatorKind(IMethodSymbol m)
        => m.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion;

    /// <summary>提升展开可安全引用两次的运算元形态（设计 §4.3 SB-Lifted 边界）：
    /// 局部/参数标识符，或 this/标识符接收者上的字段访问。</summary>
    public static bool IsSimpleLiftedOperand(ExpressionSyntax operand, SemanticModel model)
    {
        if (operand is IdentifierNameSyntax
            && model.GetSymbolInfo(operand).Symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol)
            return true;
        if (operand is MemberAccessExpressionSyntax ma
            && ma.Expression is ThisExpressionSyntax or IdentifierNameSyntax
            && model.GetSymbolInfo(ma).Symbol is IFieldSymbol)
            return true;
        return false;
    }
}
```

Create `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorNames.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>命名层（设计决策 1/1b/3/8）：C# 半区方法名 = 转换器 Java 名的 PascalCase 形态，
/// 经 MethodTransformer.JavaName（首字母小写）派生后与 OperatorTransformer 今发名逐名相等（零漂移）。</summary>
internal static class OperatorNames
{
    /// <summary>op 元数据名 → C# 方法基名（OpSymbolToJavaName 的 PascalCase 镜像，决策 1）。</summary>
    private static readonly IReadOnlyDictionary<string, string> OpBaseNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "op_Addition", "Add" },
            { "op_Subtraction", "Subtract" },
            { "op_Multiply", "Multiply" },
            { "op_Division", "Divide" },
            { "op_Modulus", "Mod" },
            { "op_Equality", "ValueEquals" },
            { "op_Inequality", "NotEquals" },
            { "op_GreaterThan", "GreaterThan" },
            { "op_LessThan", "LessThan" },
            { "op_GreaterThanOrEqual", "GreaterThanOrEqual" },
            { "op_LessThanOrEqual", "LessThanOrEqual" },
            { "op_BitwiseAnd", "And" },
            { "op_BitwiseOr", "Or" },
            { "op_ExclusiveOr", "Xor" },
            { "op_LogicalNot", "Not" },
            { "op_OnesComplement", "OnesComplement" },
            { "op_LeftShift", "LeftShift" },
            { "op_RightShift", "RightShift" },
            { "op_UnsignedRightShift", "UnsignedRightShift" },
            { "op_UnaryNegation", "Negate" },
            { "op_UnaryPlus", "Plus" },
        };

    /// <summary>决策 1b：checked 对 = 基名 + Checked（与 OperatorTransformer.cs:96-98 对齐）。</summary>
    public static string ConvertOperatorName(string metadataName)
    {
        if (metadataName.StartsWith("op_Checked", StringComparison.Ordinal))
        {
            var baseName = ConvertOperatorName("op_" + metadataName["op_Checked".Length..]);
            return baseName + "Checked";
        }
        return OpBaseNames.TryGetValue(metadataName, out var name) ? name : metadataName;
    }

    public static bool IsSupportedOperatorName(string metadataName)
        => OpBaseNames.ContainsKey(metadataName)
           || (metadataName.StartsWith("op_Checked", StringComparison.Ordinal)
               && OpBaseNames.ContainsKey("op_" + metadataName["op_Checked".Length..]));

    // ---- 决策 3：转换命名镜像表（Java 类型字符串 → 后缀；算法复刻 BuildConversionMethodSuffix）----

    private static readonly IReadOnlyDictionary<string, string> JavaTypeToSuffix =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "double", "Double" }, { "int", "Int" }, { "long", "Long" }, { "float", "Float" },
            { "boolean", "Boolean" }, { "String", "String" }, { "BigDecimal", "Decimal" },
            { "byte", "Byte" },
        };

    /// <summary>C# 目标类型 → 转换方法基名（不含消歧）；表外 → false（CS2J5177）。</summary>
    public static bool TryGetConversionBaseName(ITypeSymbol targetType, bool isExplicitOperator,
        out string baseName)
    {
        baseName = "";
        if (!TryGetJavaTypeName(targetType, out var javaType)) return false;
        if (!TryGetConversionBaseSuffix(javaType, out var suffix)) return false;
        baseName = "To" + suffix + (isExplicitOperator ? "Explicit" : "");
        return true;
    }

    /// <summary>镜像表判定与后缀（供单测直接核对）。</summary>
    public static bool TryGetConversionBaseSuffix(string javaType, out string suffix)
    {
        suffix = "";
        if (string.IsNullOrEmpty(javaType)) return false;
        var isByteArray = javaType.EndsWith("[]", StringComparison.Ordinal);
        var core = isByteArray ? javaType[..^2] : javaType;
        if (!JavaTypeToSuffix.TryGetValue(core, out var coreSuffix)) return false;
        suffix = isByteArray ? coreSuffix + "Array" : coreSuffix;
        return true;
    }

    /// <summary>C# 目标类型 → 镜像表 Java 类型名。 Nullable/泛型/tuple/指针 → false。</summary>
    private static bool TryGetJavaTypeName(ITypeSymbol t, out string javaType)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Double: javaType = "double"; return true;
            case SpecialType.System_Int32: javaType = "int"; return true;
            case SpecialType.System_Int64: javaType = "long"; return true;
            case SpecialType.System_Single: javaType = "float"; return true;
            case SpecialType.System_Boolean: javaType = "boolean"; return true;
            case SpecialType.System_String: javaType = "String"; return true;
            case SpecialType.System_Decimal: javaType = "BigDecimal"; return true;
            case SpecialType.System_Byte: javaType = "byte"; return true;
        }
        if (t is IArrayTypeSymbol { ElementType: { SpecialType: SpecialType.System_Byte } })
        {
            javaType = "byte[]"; return true;
        }
        if (t is IArrayTypeSymbol arr && arr.Rank == 1)
        {
            if (!TryGetJavaTypeName(arr.ElementType, out var elem)) { javaType = ""; return false; }
            javaType = elem + "[]";
            return true;
        }
        javaType = "";
        return false;
    }

    // ---- 决策 8：确定性碰撞升级（Add → AddOperator → AddOperator2…）----

    public static string Disambiguate(string baseName, IReadOnlyCollection<string> taken)
    {
        if (!taken.Contains(baseName)) return baseName;
        var candidate = baseName + "Operator";
        for (var i = 2; i < 1000; i++)
        {
            if (!taken.Contains(candidate)) return candidate;
            candidate = baseName + "Operator" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return baseName; // 耗尽由调用方 CS2J5178 兜底
    }
}
```

> 说明：`string` 目标基名 `ToString` 会与 object 方法冲突 —— 碰撞预留表自动升级为 `ToStringOperator`（决策 8 路径），单测锁该行为。

- [ ] **Step 2.4: 调整 Step 2.1 中 string 断言为升级形态并跑绿**

把 `[InlineData("string", "ToString2")]` 改为 `[InlineData("String", "String")]`（镜像表只断言后缀），并在碰撞测试补一条：

```csharp
        var taken2 = new HashSet<string>(StringComparer.Ordinal) { "ToString" };
        Assert.Equal("ToStringOperator", OperatorNames.Disambiguate("ToString", taken2));
```

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: PASS

- [ ] **Step 2.5: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): C14 operator naming/facts/diagnostics layer — PascalCase mirror of OpSymbolToJavaName, conversion mirror table, collision upgrade"
```

---

### Task 3: OperatorPlan + OperatorRule 骨架（声明收集/闸门/站点扫描/命名预留）+ Preprocessor 派发落地

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorPlan.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorRule.cs`
- Modify: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs`（落地 Step 1.4 的派发改写）
- Create: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs`（最小骨架）
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`（追加规则发现测试）

- [ ] **Step 3.1: 写失败测试（规则发现 + plan 建立 + inc/dec/true/false 白名单）**

在 `MemberDesugarOperatorUnitTests` 追加：

```csharp
    // ---- 规则接线（§4.2）：程序集扫描、Order=40、Kind=Operator ----

    [Fact]
    public void RuleDiscovery_ContainsOperatorRule_Ordered()
    {
        var rules = MemberDesugarRuleDiscovery.Discover();
        var opRule = Assert.Single(rules, r => r.Kind == MemberDesugarKind.Operator);
        Assert.Equal(40, opRule.Order);
        Assert.IsType<Operators.OperatorRule>(opRule);
        Assert.True(rules.IndexOf(rules.First(r => r.Kind == MemberDesugarKind.Property))
                    < rules.IndexOf(opRule));
    }

    [Fact]
    public void Plan_BuiltForArithmeticDeclaration_AndDesugared()
    {
        var (output, stats, diags) = Rewrite("""
            public struct Point
            {
                public int X, Y;
                public Point(int x, int y) { X = x; Y = y; }
                public static Point operator +(Point a, Point b)
                    => new Point(a.X + b.X, a.Y + b.Y);
            }
            """);
        Assert.Equal(1, stats.OperatorsPlanned);
        Assert.Contains(output, "public static Point Add(Point a, Point b)");
        Assert.DoesNotContain("operator", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IncrementDecrementDeclarations_DeferredNotPlanned()
    {
        var (output, stats, _) = Rewrite("""
            public class Counter
            {
                public int V;
                public static Counter operator ++(Counter c) { c.V++; return c; }
                public static Counter operator --(Counter c) { c.V--; return c; }
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Equal(2, stats.IncrementDecrementDeferred);
        Assert.Contains("operator ++", output);
        Assert.Contains("operator --", output);
    }

    [Fact]
    public void GateType_BitwiseAndOr_RetainedForJk()
    {
        var (output, stats, _) = Rewrite("""
            public struct SqlBool
            {
                public static SqlBool operator &(SqlBool a, SqlBool b) => a;
                public static SqlBool operator |(SqlBool a, SqlBool b) => a;
                public static bool operator true(SqlBool x) => true;
                public static bool operator false(SqlBool x) => false;
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Equal(2, stats.GateOperatorsRetained);
        Assert.Contains("operator &", output);
        Assert.Contains("operator |", output);
        Assert.Contains("operator true", output);
    }
```

- [ ] **Step 3.2: 运行确认失败**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: 编译失败（Operators.OperatorRule 不存在）

- [ ] **Step 3.3: 实现 OperatorPlan**

Create `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorPlan.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>每个源内运算符/转换符号一 plan（设计 §4.2）；plan 是原子回退单位。</summary>
internal sealed class OperatorPlan : MemberDesugarPlan
{
    public required IMethodSymbol Operator { get; init; }
    public required bool IsConversion { get; init; }
    /// <summary>命名预留后的最终方法名（决策 8）。</summary>
    public required string GeneratedMethodName { get; init; }
    /// <summary>碰撞前基名（== GeneratedMethodName 时无消歧）。</summary>
    public required string BaseMethodName { get; init; }

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Operator.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return string.Equals(memberName, GeneratedMethodName, StringComparison.Ordinal)
               || string.Equals(memberName, BaseMethodName, StringComparison.Ordinal)
               || string.Equals(memberName, Operator.Name, StringComparison.Ordinal);
    }

    public static string BuildId(IMethodSymbol op)
    {
        var span = op.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD:{op.ContainingType?.ToDisplayString()}::{op.Name}@{span}";
    }
}
```

- [ ] **Step 3.4: 实现 OperatorRule（三阶段）**

Create `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorRule.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>
/// C14 运算符规则（Kind=Operator, Order=40）。阶段 1 声明收集+声明级闸门（5175/5177）；
/// 阶段 2 站点扫描（5173/5174/5176/5179 升级 plan Infeasible）；阶段 3 命名预留+plan 产出。
/// Info 级白名单（inc/dec、门控 &amp;/|、op_True/False、表外槽位）不建 plan，重写期计数（设计 §6）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Operator, 40)]
internal sealed class OperatorRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Operator;
    public int Order => 40;

    private sealed class CandidateSet : Dictionary<IMethodSymbol, string>(
        SymbolEqualityComparer.Default) { }

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        // ---- 阶段 1：声明收集 + 声明级闸门 ----
        var candidates = new Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)>(
            SymbolEqualityComparer.Default);
        var failures = new Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        var userSkipped = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<OperatorDeclarationSyntax>())
                CollectDeclaration(decl, model, context.Options, candidates, failures, userSkipped);
            foreach (var decl in root.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>())
                CollectDeclaration(decl, model, context.Options, candidates, failures, userSkipped);
        }

        // ---- 阶段 2：站点扫描 ----
        var siteFiles = new Dictionary<IMethodSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        var siteFailures = new Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var filePath = model.SyntaxTree.FilePath;
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case BinaryExpressionSyntax bin
                        when !bin.IsKind(SyntaxKind.LogicalAndExpression)
                             && !bin.IsKind(SyntaxKind.LogicalOrExpression):
                        ScanOperatorSite(bin, model.GetSymbolInfo(bin).Symbol, model, filePath,
                            candidates, siteFiles, siteFailures, context.Options);
                        break;

                    case PrefixUnaryExpressionSyntax un when IsRewritableUnary(un):
                        ScanOperatorSite(un, model.GetSymbolInfo(un).Symbol, model, filePath,
                            candidates, siteFiles, siteFailures, context.Options);
                        break;

                    case AssignmentExpressionSyntax assign
                        when IsCompoundKind(assign):
                        ScanCompoundSite(assign, model, filePath, candidates, siteFiles, siteFailures,
                            context.Options);
                        break;

                    case CastExpressionSyntax cast:
                        ScanConversionNode(cast, model, filePath, candidates, siteFiles, siteFailures,
                            context.Options);
                        break;

                    case ExpressionSyntax expr when node.Parent is not CastExpressionSyntax:
                        // 隐式转换站点（决策 4）：非 cast 容器的表达式 operation 判定
                        ScanConversionNode(expr, model, filePath, candidates, siteFiles, siteFailures,
                            context.Options);
                        break;
                }
            }
        }

        // ---- 阶段 3：命名预留 + plan 产出 ----
        foreach (var group in candidates.Keys.GroupBy(o => o.ContainingType, SymbolEqualityComparer.Default))
        {
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in group.Key!.GetMembers()) reserved.Add(member.Name);

            foreach (var op in group.OrderBy(o =>
                         o.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0))
            {
                var (isConv, baseName) = candidates[op];
                var finalName = OperatorNames.Disambiguate(baseName, reserved);
                reserved.Add(finalName);

                var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var loc in op.Locations.Where(l => l.IsInSource))
                    touched.Add(loc.SourceTree!.FilePath);
                if (siteFiles.TryGetValue(op, out var files))
                    foreach (var f in files) touched.Add(f);

                if (siteFailures.TryGetValue(op, out var fail))
                {
                    builder.Add(new OperatorPlan
                    {
                        Id = OperatorPlan.BuildId(op), Kind = MemberDesugarKind.Operator,
                        Symbol = op, Outcome = MemberDesugarOutcome.Infeasible,
                        TouchedFiles = DeclarationFiles(op),
                        FailCode = fail.Code, FailReason = fail.Reason,
                        Operator = op, IsConversion = isConv,
                        GeneratedMethodName = finalName, BaseMethodName = baseName,
                    });
                    continue;
                }

                builder.Add(new OperatorPlan
                {
                    Id = OperatorPlan.BuildId(op), Kind = MemberDesugarKind.Operator,
                    Symbol = op, Outcome = MemberDesugarOutcome.Planned,
                    TouchedFiles = touched.ToList(),
                    Operator = op, IsConversion = isConv,
                    GeneratedMethodName = finalName, BaseMethodName = baseName,
                });
            }
        }

        foreach (var (op, fail) in failures)
            builder.Add(new OperatorPlan
            {
                Id = OperatorPlan.BuildId(op), Kind = MemberDesugarKind.Operator,
                Symbol = op, Outcome = MemberDesugarOutcome.Infeasible,
                TouchedFiles = DeclarationFiles(op),
                FailCode = fail.Code, FailReason = fail.Reason,
                Operator = op, IsConversion = op.MethodKind == MethodKind.Conversion,
                GeneratedMethodName = op.Name, BaseMethodName = op.Name,
            });

        foreach (var op in userSkipped)
            builder.Add(new OperatorPlan
            {
                Id = OperatorPlan.BuildId(op), Kind = MemberDesugarKind.Operator,
                Symbol = op, Outcome = MemberDesugarOutcome.UserSkipped,
                TouchedFiles = DeclarationFiles(op),
                Operator = op, IsConversion = op.MethodKind == MethodKind.Conversion,
                GeneratedMethodName = op.Name, BaseMethodName = op.Name,
            });
    }

    // ---- 声明收集 ----

    private static void CollectDeclaration(
        SyntaxNode decl, SemanticModel model, MemberDesugarOptions options,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        HashSet<IMethodSymbol> userSkipped)
    {
        if (model.GetDeclaredSymbol(decl) is not { } op
            || !OperatorFacts.IsUserDefinedOperatorKind(op)) return;
        if (candidates.ContainsKey(op) || failures.ContainsKey(op) || userSkipped.Contains(op)) return;

        // Info 级白名单：不建 plan（重写期计数）
        if (op.MethodKind == MethodKind.UserDefinedOperator)
        {
            if (OperatorFacts.IsIncrementOrDecrement(op.Name)) return;
            if (OperatorFacts.IsTrueOrFalse(op.Name)) return;
            if (OperatorFacts.IsGateAndOr(op.Name) && OperatorFacts.IsGateType(op.ContainingType!)) return;
            if (!OperatorNames.IsSupportedOperatorName(op.Name)) return;
        }

        if (MatchesSet(options.SkipQualifiedNames, op))
        {
            userSkipped.Add(op);
            return;
        }

        if (OperatorFacts.IsInterfaceStaticAbstract(op))
        {
            failures[op] = (MemberDesugarDiagnosticCode.StaticAbstractInterfaceOperator,
                OperatorDiagnostics.StaticAbstractInterface(op.Name));
            return;
        }

        if (op.MethodKind == MethodKind.Conversion)
        {
            var isExplicit = decl is ConversionOperatorDeclarationSyntax c
                && c.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ExplicitKeyword);
            if (!OperatorNames.TryGetConversionBaseName(op.ReturnType, isExplicit, out var convBase))
            {
                failures[op] = (MemberDesugarDiagnosticCode.ConversionMirrorMiss,
                    OperatorDiagnostics.MirrorMiss(op.Name, op.ReturnType.ToDisplayString()));
                return;
            }
            candidates[op] = (true, convBase);
        }
        else
        {
            candidates[op] = (false, OperatorNames.ConvertOperatorName(op.Name));
        }
    }

    // ---- 站点扫描 ----

    private static void ScanOperatorSite(
        ExpressionSyntax site, ISymbol? symbol, SemanticModel model, string filePath,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, HashSet<string>> siteFiles,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options)
    {
        if (symbol is not IMethodSymbol m
            || m.MethodKind != MethodKind.UserDefinedOperator) return;
        if (!TryNormalize(m, candidates, out var planOp)) return;
        Record(siteFiles, planOp, filePath);

        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(site, model)
            && !MatchesSet(options.ForceQualifiedNames, planOp))
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.OperatorExpressionTreeSite,
                OperatorDiagnostics.ExpressionTree(planOp.Name));
            return;
        }
        if (ContainsDynamicOperand(model.GetOperation(site)))
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.OperatorDynamicSite,
                OperatorDiagnostics.Dynamic(planOp.Name));
            return;
        }
        if (model.GetOperation(site) is { IsLifted: true } op
            && !LiftedSitesAreBuildable(op, model))
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                OperatorDiagnostics.UnbuildableLift(planOp.Name));
        }
    }

    private static void ScanCompoundSite(
        AssignmentExpressionSyntax assign, SemanticModel model, string filePath,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, HashSet<string>> siteFiles,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options)
    {
        if (model.GetOperation(assign) is not ICompoundAssignmentOperation compound) return;
        if (compound.OperatorMethod is not { } m) return; // 事件 += 是 EventAssignment，不进这里
        if (!TryNormalize(m, candidates, out var planOp)) return;
        Record(siteFiles, planOp, filePath);

        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(assign, model)
            && !MatchesSet(options.ForceQualifiedNames, planOp))
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.OperatorExpressionTreeSite,
                OperatorDiagnostics.ExpressionTree(planOp.Name));
            return;
        }

        // 决策 2b：属性/索引器目标 → 整 plan 跳过；复杂接收者且非语句位 → 不可提升
        if (model.GetSymbolInfo(assign.Left).Symbol is IPropertySymbol)
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.CompoundTargetUnsupported,
                OperatorDiagnostics.CompoundUnsupported(planOp.Name, "property/indexer target"));
            return;
        }
        if (assign.Left is not IdentifierNameSyntax && assign.Parent is not ExpressionStatementSyntax)
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.CompoundTargetUnsupported,
                OperatorDiagnostics.CompoundUnsupported(planOp.Name, "non-statement complex receiver"));
            return;
        }
        if (compound.IsLifted && !OperatorFacts.IsSimpleLiftedOperand(assign.Left, model))
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                OperatorDiagnostics.UnbuildableLift(planOp.Name));
            return;
        }

        MarkIfUnplannedUserConversion(compound.OutConversion, candidates, siteFailures, planOp);
        MarkIfUnplannedUserConversion(compound.InConversion, candidates, siteFailures, planOp);
    }

    private static void ScanConversionNode(
        ExpressionSyntax node, SemanticModel model, string filePath,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, HashSet<string>> siteFiles,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options)
    {
        if (model.GetOperation(node) is not IConversionOperation conv || !conv.IsUserDefined) return;
        if (conv.OperatorMethod is not { } m || !TryNormalize(m, candidates, out var planOp)) return;
        Record(siteFiles, planOp, filePath);

        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(node, model)
            && !MatchesSet(options.ForceQualifiedNames, planOp))
        {
            MarkFailure(siteFailures, planOp,
                MemberDesugarDiagnosticCode.OperatorExpressionTreeSite,
                OperatorDiagnostics.ExpressionTree(planOp.Name));
            return;
        }
        if (conv.Operand.Type is { TypeKind: TypeKind.Dynamic })
        {
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.OperatorDynamicSite,
                OperatorDiagnostics.Dynamic(planOp.Name));
            return;
        }

        // 提升转换 S? → T?（公式 ①）；其它 nullable 形态 → 5176
        if (conv.Operand.Type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } operandN)
        {
            var paramType = planOp.Parameters.FirstOrDefault()?.Type;
            var shapeOk = conv.Type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } targetN
                && paramType != null
                && SymbolEqualityComparer.Default.Equals(targetN.TypeArguments[0], planOp.ReturnType)
                && SymbolEqualityComparer.Default.Equals(operandN.TypeArguments[0], paramType);
            if (!shapeOk || !OperatorFacts.IsSimpleLiftedOperand(conv.Operand.Syntax, model))
                MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                    OperatorDiagnostics.UnbuildableLift(planOp.Name));
        }
    }

    private static void MarkIfUnplannedUserConversion(
        Conversion conversion,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        IMethodSymbol planOp)
    {
        if (!conversion.IsUserDefined || conversion.MethodSymbol is not { } cm) return;
        if (!TryNormalize(cm, candidates, out _))
            MarkFailure(siteFailures, planOp, MemberDesugarDiagnosticCode.UnbuildableLift,
                OperatorDiagnostics.UnbuildableLift(planOp.Name));
    }

    private static bool LiftedSitesAreBuildable(IOperation op, SemanticModel model)
    {
        foreach (var child in op.DescendantsAndSelf())
        {
            switch (child)
            {
                case IUnaryOperation { IsLifted: true } u
                    when !OperatorFacts.IsSimpleLiftedOperand(u.Operand.Syntax, model):
                    return false;
                case IBinaryOperation { IsLifted: true } b
                    when !OperatorFacts.IsSimpleLiftedOperand(b.LeftOperand.Syntax, model)
                         || !OperatorFacts.IsSimpleLiftedOperand(b.RightOperand.Syntax, model):
                    return false;
            }
        }
        return true;
    }

    private static bool ContainsDynamicOperand(IOperation? op)
        => op != null && op.DescendantsAndSelf().Any(d => d.Type is { TypeKind: TypeKind.Dynamic });

    internal static bool IsRewritableUnary(PrefixUnaryExpressionSyntax un)
        => un.OperatorToken.IsKind(SyntaxKind.MinusToken)
           || un.OperatorToken.IsKind(SyntaxKind.PlusToken)
           || un.OperatorToken.IsKind(SyntaxKind.ExclamationToken)
           || un.OperatorToken.IsKind(SyntaxKind.TildeToken);

    internal static bool IsCompoundKind(AssignmentExpressionSyntax a)
        => a.Kind() is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression
            or SyntaxKind.MultiplyAssignmentExpression or SyntaxKind.DivideAssignmentExpression
            or SyntaxKind.ModuloAssignmentExpression or SyntaxKind.AndAssignmentExpression
            or SyntaxKind.OrAssignmentExpression or SyntaxKind.ExclusiveOrAssignmentExpression
            or SyntaxKind.LeftShiftAssignmentExpression or SyntaxKind.RightShiftAssignmentExpression
            or SyntaxKind.UnsignedRightShiftAssignmentExpression;

    // ---- 小工具（镜像 C11 PropertyRule）----

    internal static bool TryNormalize(
        IMethodSymbol m,
        Dictionary<IMethodSymbol, (bool IsConversion, string BaseName)> candidates,
        out IMethodSymbol normalized)
    {
        normalized = m.IsDefinition ? m : m.OriginalDefinition;
        return candidates.ContainsKey(normalized);
    }

    private static void Record(
        Dictionary<IMethodSymbol, HashSet<string>> siteFiles, IMethodSymbol op, string filePath)
    {
        if (!siteFiles.TryGetValue(op, out var set))
            siteFiles[op] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(filePath);
    }

    private static void MarkFailure(
        Dictionary<IMethodSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        IMethodSymbol op, MemberDesugarDiagnosticCode code, string reason)
        => failures.TryAdd(op, (code, reason));

    private static bool MatchesSet(IReadOnlySet<string> names, IMethodSymbol op)
    {
        var type = op.ContainingType?.ToDisplayString();
        return names.Contains($"{type}::{op.Name}")
               || names.Contains($"{type}::conversion({op.ReturnType.ToDisplayString()})")
               || (type != null && names.Contains(type));
    }

    private static IReadOnlyList<string> DeclarationFiles(IMethodSymbol op)
        => op.Locations.Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
```

- [ ] **Step 3.5: 建 OperatorUseSiteRewriter 最小骨架 + 落地 Preprocessor 派发**

Create `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>OC-1..OC-9 站点/声明重写器（单遍，绑定一律取原始 SemanticModel，
/// 运行于 Property rewriter 之前——设计 §4.5）。构造与声明访问器先落地，站点访问器
/// 由 Task 4/5/6/7/8 逐步追加。</summary>
internal sealed partial class OperatorUseSiteRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<IMethodSymbol, OperatorPlan> _active;
    private readonly HashSet<IMethodSymbol> _knownSymbols;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public OperatorUseSiteRewriter(
        SemanticModel model,
        IReadOnlyList<OperatorPlan> plannedPlans,
        IReadOnlyList<OperatorPlan> allPlans,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _active = new Dictionary<IMethodSymbol, OperatorPlan>(SymbolEqualityComparer.Default);
        foreach (var p in plannedPlans) _active[p.Operator] = p;
        _knownSymbols = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var p in allPlans) _knownSymbols.Add(p.Operator);
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }
}
```

按 Step 1.4 的代码把 `MemberDesugarPreprocessor.RewriteFile` 中段改为两族派发（operator 先、property 后），并把 `EmitSkipDiagnostics` 的 UserSkipped 消息泛化为 `"member-desugar plan skipped by overrides.skip configuration"`。

- [ ] **Step 3.6: 运行**

Run: `dotnet build CSharpToJavaConverter.slnx -c Debug && dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests|FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: RuleDiscovery/IncrementDeferred/GateType PASS；`Plan_BuiltForArithmeticDeclaration` 仍 FAIL（声明重写在 Task 4）；属性测试全绿

- [ ] **Step 3.7: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): C14 operator rule skeleton — declaration collection, site-scan gates 5173-5179, name reservation, OperatorPlan + shared RewriteFile operator-first dispatch"
```

---

### Task 4: OD-1/OD-2 声明归约 + 白名单计数 + residual 闸门

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`

- [ ] **Step 4.1: 写失败测试（声明归约矩阵）**

在 `MemberDesugarOperatorUnitTests` 追加：

```csharp
    // ---- OD-1/OD-2 声明归约 ----

    [Fact]
    public void Declaration_FullOperatorMatrix_Desugared()
    {
        var (output, stats, _) = Rewrite("""
            public struct Swiss
            {
                public int V;
                public Swiss(int v) { V = v; }
                public static Swiss operator +(Swiss a, Swiss b) => new Swiss(a.V + b.V);
                public static Swiss operator -(Swiss a, Swiss b) => new Swiss(a.V - b.V);
                public static Swiss operator *(Swiss a, Swiss b) => new Swiss(a.V * b.V);
                public static Swiss operator /(Swiss a, Swiss b) => new Swiss(a.V / b.V);
                public static Swiss operator %(Swiss a, Swiss b) => new Swiss(a.V % b.V);
                public static bool operator ==(Swiss a, Swiss b) => a.V == b.V;
                public static bool operator !=(Swiss a, Swiss b) => a.V != b.V;
                public static bool operator >(Swiss a, Swiss b) => a.V > b.V;
                public static bool operator <(Swiss a, Swiss b) => a.V < b.V;
                public static bool operator >=(Swiss a, Swiss b) => a.V >= b.V;
                public static bool operator <=(Swiss a, Swiss b) => a.V <= b.V;
                public static Swiss operator &(Swiss a, Swiss b) => new Swiss(a.V & b.V);
                public static Swiss operator |(Swiss a, Swiss b) => new Swiss(a.V | b.V);
                public static Swiss operator ^(Swiss a, Swiss b) => new Swiss(a.V ^ b.V);
                public static Swiss operator ~(Swiss a) => new Swiss(~a.V);
                public static Swiss operator <<(Swiss a, int n) => new Swiss(a.V << n);
                public static Swiss operator >>(Swiss a, int n) => new Swiss(a.V >> n);
                public static Swiss operator >>>(Swiss a, int n) => new Swiss(a.V >>> n);
                public static Swiss operator -(Swiss a) => new Swiss(-a.V);
                public static Swiss operator +(Swiss a) => a;
                public static bool operator !(Swiss a) => a.V == 0;
                public override bool Equals(object? o) => o is Swiss s && s.V == V;
                public override int GetHashCode() => V;
            }
            """);
        Assert.Equal(21, stats.OperatorsPlanned);
        foreach (var name in new[] { "Add", "Subtract", "Multiply", "Divide", "Mod",
                     "ValueEquals", "NotEquals", "GreaterThan", "LessThan",
                     "GreaterThanOrEqual", "LessThanOrEqual", "And", "Or", "Xor",
                     "OnesComplement", "LeftShift", "RightShift", "UnsignedRightShift",
                     "Negate", "Plus", "Not" })
            Assert.Contains($"static Swiss {name}(", output);
        Assert.DoesNotContain("operator ", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_Conversions_ImplicitAndExplicit()
    {
        var (output, stats, _) = Rewrite("""
            public struct Temperature
            {
                public double C;
                public Temperature(double c) { C = c; }
                public static implicit operator double(Temperature t) => t.C;
                public static explicit operator Temperature(double c) => new Temperature(c);
                public static implicit operator int(Temperature t) => (int)t.C;
            }
            """);
        Assert.Equal(3, stats.OperatorsPlanned);
        Assert.Contains("public static double ToDouble(Temperature t)", output);
        Assert.Contains("public static Temperature ToDoubleExplicit(double c)", output);
        Assert.Contains("public static int ToInt(Temperature t)", output);
        Assert.DoesNotContain("operator", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_CheckedPair_BothEmitted_CheckedKeywordStripped()
    {
        var (output, stats, _) = Rewrite("""
            public struct Wide
            {
                public long Lo;
                public Wide(long lo) { Lo = lo; }
                public static Wide operator +(Wide a, Wide b) => new Wide(a.Lo + b.Lo);
                public static checked Wide operator +(Wide a, Wide b) => throw new System.OverflowException();
            }
            """);
        Assert.Equal(2, stats.OperatorsPlanned);
        Assert.Contains("public static Wide Add(Wide a, Wide b)", output);
        Assert.Contains("public static Wide AddChecked(Wide a, Wide b)", output);
        Assert.DoesNotContain("checked", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_GenericOperator_BecomesPlainStaticMethod()
    {
        var (output, stats, _) = Rewrite("""
            public struct Vec<T> where T : struct
            {
                public T X, Y;
                public Vec(T x, T y) { X = x; Y = y; }
                public static Vec<T> operator +(Vec<T> a, Vec<T> b) => new Vec<T>(a.X, b.Y);
            }
            """);
        Assert.Equal(1, stats.OperatorsPlanned);
        Assert.Contains("public static Vec<T> Add(Vec<T> a, Vec<T> b)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_CollisionWithUserMethod_UserMemberUntouched()
    {
        var (output, stats, diags) = Rewrite("""
            public class Money
            {
                public decimal Amount;
                public Money(decimal a) { Amount = a; }
                public static Money operator +(Money a, Money b) => new Money(a.Amount + b.Amount);
                public static Money Add(Money a, Money b) => a;
            }
            """);
        Assert.Equal(1, stats.OperatorsPlanned);
        Assert.Contains("public static Money AddOperator(Money a, Money b)", output);
        Assert.Contains("public static Money Add(Money a, Money b) => a;", output);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.OperatorNameDisambiguated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_ConversionToString_CollidesWithObjectMethod_Upgrades()
    {
        var (output, _, _) = Rewrite("""
            public class NameBox
            {
                public string V = "";
                public static implicit operator string(NameBox n) => n.V;
            }
            """);
        Assert.Contains("public static string ToStringOperator(NameBox n)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_AttributesAndXmlDoc_Preserved()
    {
        var (output, _, _) = Rewrite("""
            using System.Diagnostics;
            public struct Tagged
            {
                public int V;
                /// <summary>plus</summary>
                [DebuggerStepThrough]
                public static Tagged operator +(Tagged a, Tagged b) => new Tagged(a.V + b.V);
            }
            """);
        Assert.Contains("/// <summary>plus</summary>", output);
        Assert.Contains("[DebuggerStepThrough]", output);
        Assert.Contains("public static Tagged Add(", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_ExpressionBodyAndBlockBody_BothSupported()
    {
        var (output, _, _) = Rewrite("""
            public class E
            {
                public int V;
                public static E operator +(E a, E b) => new E { V = a.V + b.V };
                public static E operator -(E a, E b) { return new E { V = a.V - b.V }; }
            }
            """);
        Assert.Contains("public static E Add(E a, E b) =>", output);
        Assert.Contains("public static E Subtract(E a, E b)", output);
        AssertOutputCompiles(output);
    }
```

- [ ] **Step 4.2: 运行确认失败**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: 声明类测试 FAIL

- [ ] **Step 4.3: 实现声明重写**

在 `OperatorUseSiteRewriter` 追加：

```csharp
    // ---- OD-1/OD-2 声明归约 + 白名单计数 + OC-9 residual 闸门 ----

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        if (_model.GetDeclaredSymbol(node) is not { } op)
            return base.VisitOperatorDeclaration(node);

        if (OperatorFacts.IsIncrementOrDecrement(op.Name))
        {
            _stats.IncrementDecrementDeferred++;
            EmitInfo(MemberDesugarDiagnosticCode.IncrementDecrementDeferred,
                OperatorDiagnostics.IncrementDeferred(op.ContainingType?.Name ?? "?"), node);
            return base.VisitOperatorDeclaration(node);
        }
        if (OperatorFacts.IsGateAndOr(op.Name) && OperatorFacts.IsGateType(op.ContainingType!))
        {
            _stats.GateOperatorsRetained++;
            EmitInfo(MemberDesugarDiagnosticCode.GateRetained,
                OperatorDiagnostics.GateRetained(op.ContainingType!.Name, op.Name), node);
            return base.VisitOperatorDeclaration(node);
        }
        if (_active.TryGetValue(op, out var plan))
            return ReplaceOperatorWithMethod(
                node.Modifiers, node.AttributeLists, node.ReturnType,
                node.ParameterList, node.Body, node.ExpressionBody, node.SemicolonToken,
                node.OperatorToken, plan);

        if (!_knownSymbols.Contains(op) && OperatorNames.IsSupportedOperatorName(op.Name))
            EmitResidual(op.Name, node);
        return base.VisitOperatorDeclaration(node);
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        if (_model.GetDeclaredSymbol(node) is not { } op)
            return base.VisitConversionOperatorDeclaration(node);
        if (_active.TryGetValue(op, out var plan))
            return ReplaceOperatorWithMethod(
                node.Modifiers, node.AttributeLists, node.Type,
                node.ParameterList, node.Body, node.ExpressionBody, node.SemicolonToken,
                node.OperatorToken, plan);
        if (!_knownSymbols.Contains(op))
            EmitResidual(op.Name, node);
        return base.VisitConversionOperatorDeclaration(node);
    }

    private SyntaxNode ReplaceOperatorWithMethod(
        SyntaxTokenList modifiers, SyntaxList<AttributeListSyntax> attributeLists,
        TypeSyntax returnType, ParameterListSyntax parameterList,
        BlockSyntax? body, ArrowExpressionClauseSyntax? expressionBody, SyntaxToken semicolon,
        SyntaxToken operatorToken, OperatorPlan plan)
    {
        // checked 不进方法修饰符；implicit/explicit/operator 槽位由标识符替代（OD-1/OD-2）
        var cleaned = SyntaxFactory.TokenList(modifiers
            .Where(m => !m.IsKind(SyntaxKind.CheckedKeyword)));
        if (cleaned.Count == 0)
            cleaned = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        var visitedBody = body != null ? (BlockSyntax)Visit(body)! : null;
        var visitedExpr = expressionBody != null
            ? (ArrowExpressionClauseSyntax)Visit(expressionBody)! : null;

        var method = SyntaxFactory.MethodDeclaration(returnType,
                SyntaxFactory.Identifier(plan.GeneratedMethodName).WithTriviaFrom(operatorToken))
            .WithModifiers(cleaned)
            .WithParameterList(parameterList)
            .WithBody(visitedBody)
            .WithExpressionBody(visitedExpr)
            .WithSemicolonToken(semicolon)
            .WithAttributeLists(attributeLists)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

        var loc = operatorToken.Parent!;
        if (plan.GeneratedMethodName != plan.BaseMethodName)
            EmitInfo(MemberDesugarDiagnosticCode.OperatorNameDisambiguated,
                OperatorDiagnostics.NameDisambiguated(plan.BaseMethodName, plan.GeneratedMethodName), loc);
        if (plan.IsConversion)
            EmitInfo(MemberDesugarDiagnosticCode.ConversionPlanCreated,
                OperatorDiagnostics.ConversionPlanCreated(plan.GeneratedMethodName,
                    plan.Operator.ReturnType.ToDisplayString(), "conversion"), loc);
        else
            EmitInfo(MemberDesugarDiagnosticCode.OperatorPlanCreated,
                OperatorDiagnostics.PlanCreated(plan.GeneratedMethodName, 0), loc);
        return method;
    }

    private void EmitInfo(MemberDesugarDiagnosticCode code, string message, SyntaxNode node)
    {
        if (!_options.ReportInfo) return;
        var span = node.GetLocation().GetLineSpan().StartLinePosition;
        _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Info, code, message,
            _filePath, span.Line + 1, span.Character + 1, null));
    }

    private void EmitResidual(string name, SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan().StartLinePosition;
        _diagnostics.Add(new MemberDesugarDiagnostic(
            _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
            MemberDesugarDiagnosticCode.ResidualOperator,
            OperatorDiagnostics.Residual(name), _filePath, span.Line + 1, span.Character + 1, name));
        _stats.PlansSkipped++;
        _stats.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualOperator, out var n);
        _stats.SkippedByReason[MemberDesugarDiagnosticCode.ResidualOperator] = n + 1;
    }
```

- [ ] **Step 4.4: 跑测试绿**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests|FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: PASS 全绿

- [ ] **Step 4.5: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): C14 OD-1/OD-2 declarations to static methods (checked strip, collision upgrade, attr/doc passthrough, whitelist counters, residual gate)"
```

---

### Task 5: OC-1/OC-2 二元/一元站点显式化

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`

- [ ] **Step 5.1: 写失败测试**

在 `MemberDesugarOperatorUnitTests` 追加：

```csharp
    // ---- OC-1/OC-2 二元/一元站点 ----

    [Fact]
    public void BinarySites_Arithmetic_RewrittenToStaticCalls()
    {
        var (output, stats, _) = Rewrite("""
            public struct Point
            {
                public int X, Y;
                public Point(int x, int y) { X = x; Y = y; }
                public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
                public static Point operator *(Point a, int k) => new Point(a.X * k, a.Y * k);
                public static Point operator *(int k, Point a) => new Point(a.X * k, a.Y * k);
                public static double operator *(Point a, Point b) => a.X * b.X + a.Y * b.Y;
            }
            """,
            """
            public static class Use
            {
                public static Point Sum(Point p, Point q) => p + q;
                public static Point Scale(Point p) => p * 3;
                public static Point FlipScale(Point p) => 3 * p;
                public static double Dot(Point p, Point q) => p * q;
            }
            """);
        Assert.True(stats.OperatorBinarySitesRewritten >= 4);
        AssertContains(output, "Sum(Point p, Point q) => Point.Add(p, q)");
        AssertContains(output, "Scale(Point p) => Point.Multiply(p, 3)");
        AssertContains(output, "FlipScale(Point p) => Point.Multiply(3, p)");
        AssertContains(output, "Dot(Point p, Point q) => Point.Multiply(p, q)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BinarySites_ComparisonAndBitwise_Rewritten()
    {
        var (output, stats, _) = Rewrite("""
            public struct Cmp
            {
                public int V;
                public Cmp(int v) { V = v; }
                public static bool operator ==(Cmp a, Cmp b) => a.V == b.V;
                public static bool operator !=(Cmp a, Cmp b) => a.V != b.V;
                public static bool operator <(Cmp a, Cmp b) => a.V < b.V;
                public static Cmp operator &(Cmp a, Cmp b) => new Cmp(a.V & b.V);
                public override bool Equals(object? o) => o is Cmp c && c.V == V;
                public override int GetHashCode() => V;
            }
            """,
            """
            public static class Use
            {
                public static bool Eq(Cmp a, Cmp b) => a == b;
                public static bool Ne(Cmp a, Cmp b) => a != b;
                public static bool Lt(Cmp a, Cmp b) => a < b;
                public static Cmp Both(Cmp a, Cmp b) => a & b;
            }
            """);
        Assert.True(stats.OperatorBinarySitesRewritten >= 4);
        AssertContains(output, "Eq(Cmp a, Cmp b) => Cmp.ValueEquals(a, b)");
        AssertContains(output, "Ne(Cmp a, Cmp b) => Cmp.NotEquals(a, b)");
        AssertContains(output, "Lt(Cmp a, Cmp b) => Cmp.LessThan(a, b)");
        AssertContains(output, "Both(Cmp a, Cmp b) => Cmp.And(a, b)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void UnarySites_RewrittenWithSymbolDisambiguation()
    {
        var (output, stats, _) = Rewrite("""
            public struct Sign
            {
                public int V;
                public Sign(int v) { V = v; }
                public static Sign operator -(Sign a) => new Sign(-a.V);
                public static Sign operator +(Sign a) => a;
                public static bool operator !(Sign a) => a.V == 0;
                public static Sign operator ~(Sign a) => new Sign(~a.V);
                public static Sign operator -(Sign a, Sign b) => new Sign(a.V - b.V);
                public static Sign operator +(Sign a, Sign b) => new Sign(a.V + b.V);
            }
            """,
            """
            public static class Use
            {
                public static Sign Neg(Sign s) => -s;
                public static Sign Pos(Sign s) => +s;
                public static bool Not(Sign s) => !s;
                public static Sign Inv(Sign s) => ~s;
                public static Sign Sub(Sign a, Sign b) => a - b;
                public static Sign Add(Sign a, Sign b) => a + b;
            }
            """);
        Assert.True(stats.OperatorUnarySitesRewritten >= 4);
        AssertContains(output, "Neg(Sign s) => Sign.Negate(s)");
        AssertContains(output, "Pos(Sign s) => Sign.Plus(s)");
        AssertContains(output, "Not(Sign s) => Sign.Not(s)");
        AssertContains(output, "Inv(Sign s) => Sign.OnesComplement(s)");
        AssertContains(output, "Sub(Sign a, Sign b) => Sign.Subtract(a, b)");
        AssertContains(output, "Add2(Sign a, Sign b)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BinarySite_StaticQualifier_MinimalDisplay()
    {
        var (output, _, _) = Rewrite("""
            namespace Geom
            {
                public struct Vec2
                {
                    public int X, Y;
                    public Vec2(int x, int y) { X = x; Y = y; }
                    public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
                }
                public static class Use
                {
                    public static Vec2 Add(Vec2 a, Vec2 b) => a + b;
                }
            }
            """);
        AssertContains(output, "Add(Vec2 a, Vec2 b) => Vec2.Add(a, b)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BinarySites_OperatorBodyPrimitiveOps_Untouched()
    {
        var (output, stats, _) = Rewrite("""
            public struct P
            {
                public int X, Y;
                public P(int x, int y) { X = x; Y = y; }
                public static P operator +(P a, P b) => new P(a.X + b.X, a.Y + b.Y);
                public static P operator *(P a, P b) => new P(a.X * b.X + 3, 0);
            }
            """);
        // 运算符体内 int 原语运算保持原样（对照 OperatorMethodNameCollisionTests 形态）
        Assert.Contains("a.X + b.X", output);
        Assert.Contains("a.X * b.X + 3", output);
        Assert.Equal(0, stats.OperatorBinarySitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BinarySites_EnumDecimalBuiltin_Untouched()
    {
        var (output, stats, _) = Rewrite("""
            public enum E { A, B }
            public static class Use
            {
                public static int M(decimal a, decimal b) => a + b > 0 ? 1 : 0;
                public static bool Cmp(E a, E b) => a == b;
                public static int Ints(int a, int b) => a + b;
            }
            """);
        Assert.Contains("a + b > 0", output);
        Assert.Contains("a == b", output);
        Assert.Contains("a + b", output);
        Assert.Equal(0, stats.OperatorBinarySitesRewritten);
    }

    [Fact]
    public void BinarySites_MetadataOnlyOperator_UntouchedAndCounted()
    {
        var (output, stats, _) = Rewrite("""
            using System;
            public static class Use
            {
                public static DateTime Next(DateTime d) => d + TimeSpan.FromDays(1);
            }
            """);
        Assert.Contains("d + TimeSpan.FromDays(1)", output);
        Assert.True(stats.MetadataOperatorSitesUntouched >= 1);
    }
```

- [ ] **Step 5.2: 运行确认失败**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: OC-1/OC-2 测试 FAIL

- [ ] **Step 5.3: 实现二元/一元站点访问器**

在 `OperatorUseSiteRewriter` 追加：

```csharp
    // ---- OC-1/OC-2 二元/一元站点（绑定=原始模型 GetSymbolInfo；提升由 Task 8 扩展）----

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.LogicalAndExpression)
            && !node.IsKind(SyntaxKind.LogicalOrExpression)
            && _model.GetSymbolInfo(node).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } m
            && TryGetPlan(m, out var plan))
        {
            var left = (ExpressionSyntax)Visit(node.Left)!;
            var right = (ExpressionSyntax)Visit(node.Right)!;
            return RenderCall(plan, node, left, right);
        }
        return base.VisitBinaryExpression(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (OperatorRule.IsRewritableUnary(node)
            && _model.GetSymbolInfo(node).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } m
            && TryGetPlan(m, out var plan))
        {
            var operand = (ExpressionSyntax)Visit(node.Operand)!;
            return RenderCall(plan, node, operand);
        }
        return base.VisitPrefixUnaryExpression(node);
    }

    private ExpressionSyntax RenderCall(
        OperatorPlan plan, ExpressionSyntax site, params ExpressionSyntax[] args)
    {
        var qualifier = plan.Operator.ContainingType!
            .ToMinimalDisplayString(_model, site.SpanStart);
        var call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseTypeName(qualifier),
                SyntaxFactory.IdentifierName(plan.GeneratedMethodName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                args.Select(SyntaxFactory.Argument))))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

        var op = _model.GetOperation(site);
        if (op is { IsLifted: true })
        {
            _stats.OperatorLiftedSitesExpanded++;
            return LiftedOperatorExpansion.ExpandBinaryOrUnary(
                plan.GeneratedMethodName, qualifier, op, args);
        }
        if (site is BinaryExpressionSyntax) _stats.OperatorBinarySitesRewritten++;
        else _stats.OperatorUnarySitesRewritten++;
        return call;
    }

    private bool TryGetPlan(IMethodSymbol m, out OperatorPlan plan)
    {
        plan = null!;
        if (!m.IsDefinition) m = m.OriginalDefinition;
        return _active.TryGetValue(m, out plan!);
    }
```

并 Create `src/CSharpToJava.Core/MemberDesugar/Operators/LiftedOperatorExpansion.cs`（Task 8 完成公式前先放占位实现——本任务只投掷 NotSupportedException，因为提升站点已在分析期被 5176 闸门拦截，不可达）：

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>决策 5 六类提升公式（Task 8 完成实现；分析期保证不可达时先抛）。</summary>
internal static partial class LiftedOperatorExpansion
{
    public static Syntax.ExpressionSyntax ExpandBinaryOrUnary(
        string methodName, string qualifier, IOperation op, Syntax.ExpressionSyntax[] args)
        => throw new System.NotSupportedException(
            "lifted sites are gated to Infeasible (CS2J5176) before rewrite");
}
```

> 注：`RenderCall` 中 `LiftedOperatorExpansion` 的引用与统计顺序在 Task 8 会调整；本任务结束时提升站点全部由分析闸门转 Infeasible，占位抛不触发。命名空间别名 `Syntax` 为 `Microsoft.CodeAnalysis.CSharp`——落地时直接 `using Microsoft.CodeAnalysis.CSharp;` 并去掉 `Syntax.` 前缀。

- [ ] **Step 5.4: 跑测试绿**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests|FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: PASS

- [ ] **Step 5.5: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): C14 OC-1/OC-2 — binary/unary user-operator sites to static invocations (minimal qualifier, primitive/enum/decimal/metadata-only zero-touch)"
```

---

### Task 6: M2 — ConversionResolver + OC-4/OC-5 转换站点显式化（重点）

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/Operators/ConversionResolver.cs`
- Modify: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs`（转换配方表 + DefaultVisit 拦截 + Cast 访问器）
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`

- [ ] **Step 6.1: 写失败测试（显式 cast / 隐式站点全集 / 嵌套分层 / 残留 cast / bool 语义转换）**

在 `MemberDesugarOperatorUnitTests` 追加：

```csharp
    // ---- OC-4/OC-5 转换站点（决策 4）----

    [Fact]
    public void ConversionSites_ExplicitCast_RewrittenToMethodCall()
    {
        var (output, stats, _) = Rewrite("""
            public struct Temperature
            {
                public double C;
                public Temperature(double c) { C = c; }
                public static implicit operator double(Temperature t) => t.C;
                public static explicit operator Temperature(double c) => new Temperature(c);
            }
            """,
            """
            public static class Use
            {
                public static double Celsius(Temperature t) => t;          // 隐式
                public static Temperature FromCelsius(double c) => (Temperature)c; // 显式
            }
            """);
        Assert.True(stats.OperatorConversionSitesRewritten >= 2);
        AssertContains(output, "Celsius(Temperature t) => Temperature.ToDouble(t)");
        AssertContains(output, "FromCelsius(double c) => Temperature.ToDoubleExplicit(c)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ConversionSites_ImplicitSiteCatalogue_AllRewritten()
    {
        var (output, stats, _) = Rewrite("""
            using System.Collections.Generic;
            public struct Wrapper
            {
                public int V;
                public Wrapper(int v) { V = v; }
                public static implicit operator int(Wrapper w) => w.V;
                public static implicit operator long(Wrapper w) => w.V;
            }
            """,
            """
            using System.Collections.Generic;
            public static class Use
            {
                private static Wrapper _f = new Wrapper(7);
                public static int FieldTarget() { int x = _f; return x; }
                public static int ReturnSite() => _f;
                public static int ArgSite(Wrapper w) => Take(w);
                public static int Take(int i) => i;
                public static long BranchSite(bool c, Wrapper w) => c ? w : 5L;
                public static long CoalesceSite(Wrapper? w) => w ?? new Wrapper(3);
                public static List<int> CollectionSite(Wrapper w) => new List<int> { w, 2 };
                public static int YieldSite(Wrapper w)
                {
                    yield return w;
                    yield break;
                }
            }
            """);
        Assert.True(stats.OperatorConversionSitesRewritten >= 7);
        AssertContains(output, "int x = Wrapper.ToInt(_f)");
        AssertContains(output, "ReturnSite() => Wrapper.ToInt(_f)");
        AssertContains(output, "Take(Wrapper.ToInt(w))");
        AssertContains(output, "c ? Wrapper.ToInt(w) : 5");
        AssertContains(output, "Wrapper.ToInt(w ?? new Wrapper(3))");
        AssertContains(output, "new List<int> { Wrapper.ToInt(w), 2 }");
        AssertContains(output, "yield return Wrapper.ToInt(w)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ConversionSites_NestedStandardOuterExplicit_ResidualCastPreserved()
    {
        var (output, _, _) = Rewrite("""
            public struct Src
            {
                public int V;
                public Src(int v) { V = v; }
                public static implicit operator int(Src s) => s.V;
            }
            """,
            """
            public static class Use
            {
                public static byte Narrow(Src s) => (byte)s;      // 用户→int，再显式 int→byte
                public static object Box(Src s) { object o = s; return o; } // 用户→int，隐式装箱
                public static long Widen(Src s) { long l = s; return l; }  // 用户→int，隐式提升
            }
            """);
        AssertContains(output, "Narrow(Src s) => (byte)Src.ToInt(s)");
        AssertContains(output, "Box(Src s) { object o = Src.ToInt(s); return o; }");
        AssertContains(output, "long l = Src.ToInt(s)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ConversionSites_BoolImplicit_InIfCondition_Rewritten()
    {
        var (output, _, _) = Rewrite("""
            public struct Flag
            {
                public bool B;
                public Flag(bool b) { B = b; }
                public static implicit operator bool(Flag f) => f.B;
            }
            """,
            """
            public static class Use
            {
                public static int Check(Flag f)
                {
                    if (f) return 1;
                    return 0;
                }
            }
            """);
        // if(f) 绑定 op_Implicit→bool：落 SB-Conversion（决策 7⑤），改写为 ToBoolean 包裹
        AssertContains(output, "if (Flag.ToBoolean(f))");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ConversionSites_ExplicitCastOfUserConversion_InExpression()
    {
        var (output, _, _) = Rewrite("""
            public struct Money
            {
                public decimal Amount;
                public Money(decimal a) { Amount = a; }
                public static implicit operator decimal(Money m) => m.Amount;
            }
            """,
            """
            public static class Use
            {
                public static string Print(Money m) => ((decimal)m).ToString("F2");
            }
            """);
        AssertContains(output, "Print(Money m) => Money.ToDecimal(m).ToString(\"F2\")");
        AssertOutputCompiles(output);
    }
```

- [ ] **Step 6.2: 运行确认失败**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: 转换类 FAIL

- [ ] **Step 6.3: 实现 ConversionResolver**

Create `src/CSharpToJava.Core/MemberDesugar/Operators/ConversionResolver.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>决策 4：IConversionOperation 站点解析。构造期对整文件扫描一次建
/// 「运算元语法节点 → 改写配方」表；重写期按节点引用命中（operator rewriter 先行，
/// 全部为原始节点实例——设计 §4.2/§4.5）。</summary>
internal sealed class ConversionResolver
{
    public sealed record Recipe(
        OperatorPlan Plan,
        bool IsLifted,
        bool NeedsOuterCast,
        string OuterCastType);

    private readonly Dictionary<ExpressionSyntax, Recipe> _recipes = new();

    public IReadOnlyDictionary<ExpressionSyntax, Recipe> Recipes => _recipes;

    public ConversionResolver(CompilationUnitSyntax root, SemanticModel model,
        IReadOnlyList<OperatorPlan> planned)
    {
        var plannedByDef = new Dictionary<IMethodSymbol, OperatorPlan>(SymbolEqualityComparer.Default);
        foreach (var p in planned) plannedByDef[p.Operator] = p;

        foreach (var expr in root.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (model.GetOperation(expr) is not IConversionOperation conv
                || !conv.IsUserDefined
                || conv.OperatorMethod is not { } method)
                continue;
            var def = method.IsDefinition ? method : method.OriginalDefinition;
            if (!plannedByDef.TryGetValue(def, out var plan)) continue;

            var isLifted = conv.Operand.Type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            };
            // 残留外层标准转换：方法返回类型 → 目标类型 为显式时保留 cast（决策 4）
            var needsCast = false;
            var castDisplay = "";
            if (!isLifted
                && !SymbolEqualityComparer.Default.Equals(plan.Operator.ReturnType, conv.Type))
            {
                var classify = model.Compilation.ClassifyConversion(plan.Operator.ReturnType, conv.Type);
                if (classify.IsExplicit)
                {
                    needsCast = true;
                    castDisplay = conv.Type.ToMinimalDisplayString(model, expr.SpanStart);
                }
            }
            _recipes[expr] = new Recipe(plan, isLifted, needsCast, castDisplay);
        }
    }

    /// <summary>包装：非提升 = [cast] Qual.Name(visited)；提升 = 公式 ①（Task 8）。</summary>
    public ExpressionSyntax Wrap(
        Recipe recipe, ExpressionSyntax visited, SemanticModel model, int spanStart,
        MemberDesugarStatistics stats)
    {
        var qualifier = recipe.Plan.Operator.ContainingType!
            .ToMinimalDisplayString(model, spanStart);
        if (recipe.IsLifted)
        {
            stats.OperatorLiftedSitesExpanded++;
            return LiftedOperatorExpansion.ExpandLiftedConversion(
                recipe.Plan.GeneratedMethodName, qualifier, visited, recipe.Plan.Operator.ReturnType);
        }
        stats.OperatorConversionSitesRewritten++;
        var call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseTypeName(qualifier),
                SyntaxFactory.IdentifierName(recipe.Plan.GeneratedMethodName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                new[] { SyntaxFactory.Argument(visited) })));
        return recipe.NeedsOuterCast
            ? SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(recipe.OuterCastType), call)
            : call;
    }
}
```

- [ ] **Step 6.4: 把转换配方接入 OperatorUseSiteRewriter**

构造函数追加一行建表，并追加访问器：

```csharp
    private readonly ConversionResolver _conversions;

    // 构造函数末尾追加：
    _conversions = new ConversionResolver(
        (CompilationUnitSyntax)model.SyntaxTree.GetRoot(), model, plannedPlans);

    // ---- OC-4/OC-5：转换站点拦截。cast 站点 = CastExpressionSyntax 本身；
    //      隐式站点 = 运算元表达式节点（DefaultVisit 兜底拦截非重写类型节点）。----

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var inner = (ExpressionSyntax)Visit(node.Expression)!;
        if (_conversions.Recipes.TryGetValue(node, out var recipe))
            return recipe.Wrap(_conversions, _model, node.SpanStart, _stats)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation)
                .WithTriviaFrom(node)
                .WithLeadingTrivia(node.GetLeadingTrivia());
        // Wrap 重载见下——保留 cast 但子节点已改写
        return node.WithExpression(inner);
    }

    public override SyntaxNode? DefaultVisit(SyntaxNode node)
    {
        var visited = base.DefaultVisit(node);
        if (node is ExpressionSyntax original
            && visited is ExpressionSyntax visitedExpr
            && _conversions.Recipes.ContainsKey(original))
        {
            var recipe = _conversions.Recipes[original];
            return _conversions.Wrap(recipe, visitedExpr, _model, original.SpanStart, _stats)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }
```

同时在 `VisitBinaryExpression`/`VisitPrefixUnaryExpression` 的 return 前追加外层转换包裹检查（二元/一元节点自身可能是隐式转换站点）：

```csharp
    private ExpressionSyntax ApplyOuterConversion(ExpressionSyntax original, ExpressionSyntax visited)
        => _conversions.Recipes.TryGetValue(original, out var recipe)
            ? _conversions.Wrap(recipe, visited, _model, original.SpanStart, _stats)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation)
            : visited;
```

并把 `RenderCall` 的返回改为 `return ApplyOuterConversion(site, call);`（提升分支返回 `ApplyOuterConversion(site, expanded)`）。

> 实现注记 1：`ConversionResolver.Wrap` 作为 record 方法引用 `_model/_stats` 不成立——落地时 Wrap 为 ConversionResolver 的实例方法但统计由调用方增；本计划落地时把 `Wrap(Recipe, ExpressionSyntax, SemanticModel, int, MemberDesugarStatistics)` 实现为 **ConversionResolver 实例方法**（持有 model），`stats` 传参计数。上面访问器代码按此签名写。
>
> 实现注记 2：`VisitCastExpression` 中 cast 命中时**不需要**先 Visit 内层再包——配方包的是 conv.Operand（即 cast 的 Expression）；`Wrap` 的 visited 参数应是 `(ExpressionSyntax)Visit(node.Expression)!`。修正落地形态：

```csharp
    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var inner = (ExpressionSyntax)Visit(node.Expression)!;
        if (_conversions.Recipes.TryGetValue(node, out var recipe))
            return _conversions.Wrap(recipe, inner, node.SpanStart, _stats)
                .WithTriviaFrom(node)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        return node.WithExpression(inner);
    }
```

（`Wrap` 实例方法签名 `(Recipe recipe, ExpressionSyntax visited, int spanStart, MemberDesugarStatistics stats)`，内部用构造时持有的 model。）

- [ ] **Step 6.5: 跑测试绿**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests|FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: PASS（`YieldSite` 里迭代器方法返回类型为 IEnumerable 而非 int——落地时把该用例改为返回 `System.Collections.Generic.IEnumerable<int>`，源码里写全名）

- [ ] **Step 6.6: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): C14 M2 OC-4/OC-5 — IConversionOperation site explicitization (implicit site catalogue, nested standard conversions, residual explicit cast)"
```

---

### Task 7: M3 — OC-3 复合赋值（变量/字段目标 + 回转链 + 临时提升）

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorUseSiteRewriter.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`

- [ ] **Step 7.1: 写失败测试**

在 `MemberDesugarOperatorUnitTests` 追加：

```csharp
    // ---- OC-3 复合赋值（决策 2b）----

    [Fact]
    public void CompoundAssignment_LocalTarget_Inlined()
    {
        var (output, stats, _) = Rewrite("""
            public struct Acc
            {
                public int V;
                public Acc(int v) { V = v; }
                public static Acc operator +(Acc a, Acc b) => new Acc(a.V + b.V);
                public static Acc operator *(Acc a, Acc b) => new Acc(a.V * b.V);
            }
            """,
            """
            public static class Use
            {
                public static Acc Bump(Acc x, Acc v)
                {
                    x += v;
                    x *= v;
                    return x;
                }
            }
            """);
        Assert.True(stats.OperatorCompoundSitesRewritten >= 2);
        AssertContains(output, "x = Acc.Add(x, v)");
        AssertContains(output, "x = Acc.Multiply(x, v)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void CompoundAssignment_FieldTargetWithSideEffectReceiver_LiftedOnce()
    {
        var (output, stats, _) = Rewrite("""
            public struct Acc
            {
                public int V;
                public Acc(int v) { V = v; }
                public static Acc operator +(Acc a, Acc b) => new Acc(a.V + b.V);
            }
            """,
            """
            public class Holder
            {
                public Acc Total;
                public static Holder Instance() => new Holder();
            }
            public static class Use
            {
                public static void Bump(Acc v)
                {
                    Holder.Instance().Total += v;
                }
            }
            """);
        Assert.True(stats.OperatorCompoundSitesRewritten >= 1);
        AssertContains(output, "__cs2j_recv0 = Holder.Instance()");
        AssertContains(output, ".Total = Acc.Add(__cs2j_recv0.Total, v)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void CompoundAssignment_PropertyTarget_SkipsPlanAtomically()
    {
        var (output, stats, diags) = Rewrite("""
            public struct Acc
            {
                public int V;
                public Acc(int v) { V = v; }
                public static Acc operator +(Acc a, Acc b) => new Acc(a.V + b.V);
            }
            """,
            """
            public class Box
            {
                public Acc P { get; set; }
            }
            public static class Use
            {
                public static void Bump(Box b, Acc v)
                {
                    b.P += v;
                }
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned); // plan Infeasible → 计数不涨
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.CompoundTargetUnsupported);
        Assert.Contains("operator +(Acc a, Acc b)", output);   // 声明不动
        Assert.Contains("b.P += v", output);                    // 站点不动
    }

    [Fact]
    public void CompoundAssignment_BackConversionUserDefined_ChainsExplicit()
    {
        var (output, _, _) = Rewrite("""
            public struct Small
            {
                public int V;
                public Small(int v) { V = v; }
                public static Wide operator +(Small a, Small b) => new Wide(a.V + b.V);
                public static explicit operator Small(Wide w) => new Small((int)w.Hi);
            }
            public struct Wide
            {
                public long Hi;
                public Wide(long h) { Hi = h; }
            }
            """,
            """
            public static class Use
            {
                public static void Bump(Small x, Small v)
                {
                    x += v; // op+: Small+Small→Wide，回转 Wide→Small 显式用户转换
                }
            }
            """);
        AssertContains(output, "x = Small.ToSmallExplicit(Wide.Add(x, v))");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void CompoundAssignment_EventAdd_NotTouched()
    {
        var (output, stats, _) = Rewrite("""
            using System;
            public class Pub
            {
                public event EventHandler? E;
                public void On() => E?.Invoke(this, EventArgs.Empty);
            }
            public static class Use
            {
                public static void Wire(Pub p, EventHandler h) { p.E += h; }
            }
            """);
        Assert.Contains("p.E += h", output);
        Assert.Equal(0, stats.OperatorCompoundSitesRewritten);
    }
```

- [ ] **Step 7.2: 运行确认失败**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: 复合赋值类 FAIL

- [ ] **Step 7.3: 实现复合赋值重写**

在 `OperatorUseSiteRewriter` 追加（含语句拼接，镜像 C11 的 Splice 形态）：

```csharp
    // ---- OC-3 复合赋值：简单左值内联；复杂接收者语句位提升 __cs2j_recv ----

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (OperatorRule.IsCompoundKind(node)
            && _model.GetOperation(node) is ICompoundAssignmentOperation compound
            && compound.OperatorMethod is { } m && TryGetPlan(m, out var plan)
            && node.Left is IdentifierNameSyntax)
        {
            var left = (ExpressionSyntax)Visit(node.Left)!;
            var right = (ExpressionSyntax)Visit(node.Right)!;
            var inner = RenderCompoundInner(plan, compound, left, right);
            _stats.OperatorCompoundSitesRewritten++;
            var result = SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, left, inner);
            return compound.IsLifted
                ? LiftedOperatorExpansion.WrapLiftedCompound(
                    result, left, plan.Operator.ReturnType)
                : result;
        }
        return base.VisitAssignmentExpression(node);
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // 复杂接收者（字段访问）复合：提升接收者只求值一次（设计决策 2b）
        if (node.Expression is AssignmentExpressionSyntax assign
            && OperatorRule.IsCompoundKind(assign)
            && _model.GetOperation(assign) is ICompoundAssignmentOperation compound
            && compound.OperatorMethod is { } m && TryGetPlan(m, out var plan)
            && assign.Left is MemberAccessExpressionSyntax access
            && access.Expression is not (ThisExpressionSyntax or IdentifierNameSyntax
                or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }))
        {
            var recv = (ExpressionSyntax)Visit(access.Expression)!;
            var right = (ExpressionSyntax)Visit(assign.Right)!;
            var temp = MemberDesugarSyntaxFacts.TempName(ref _tempCounter, "recv");
            var tempIdent = SyntaxFactory.IdentifierName(temp);
            var leftRewritten = access.WithExpression(tempIdent);
            var inner = RenderCompoundInner(plan, compound, leftRewritten, right);
            _stats.OperatorCompoundSitesRewritten++;
            var store = SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, leftRewritten, inner);
            return SyntaxFactory.Block(
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Space),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(temp)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(recv))))),
                    SyntaxFactory.ExpressionStatement(store))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    private IEnumerable<StatementSyntax> SpliceStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var stmt in statements)
        {
            if (Visit(stmt) is not StatementSyntax visited) continue;
            if (visited is BlockSyntax spliced
                && spliced.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind))
            {
                var list = spliced.Statements.ToList();
                if (list.Count > 0)
                {
                    list[0] = list[0].WithLeadingTrivia(
                        spliced.GetLeadingTrivia().Concat(list[0].GetLeadingTrivia()));
                    list[^1] = list[^1].WithTrailingTrivia(spliced.GetTrailingTrivia());
                }
                foreach (var s in list) yield return s;
                continue;
            }
            yield return visited;
        }
    }

    /// <summary>内层 = [入转换] Qual.Name(left, [入转换] right) + 出转换包裹（决策 2b）。</summary>
    private ExpressionSyntax RenderCompoundInner(
        OperatorPlan plan, ICompoundAssignmentOperation compound,
        ExpressionSyntax left, ExpressionSyntax right)
    {
        var qualifier = plan.Operator.ContainingType!
            .ToMinimalDisplayString(_model, compound.Syntax.SpanStart);
        var argRight = ApplyInConversion(compound.InConversion, right, plan.Operator.Parameters);
        var call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseTypeName(qualifier),
                SyntaxFactory.IdentifierName(plan.GeneratedMethodName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Argument(left),
                SyntaxFactory.Argument(argRight),
            })));
        return ApplyOutConversion(compound.OutConversion, call, compound.Syntax.SpanStart);
    }

    private ExpressionSyntax ApplyInConversion(
        Conversion inConversion, ExpressionSyntax value, ImmutableArray<IParameterSymbol> @params)
    {
        if (inConversion.IsUserDefined && inConversion.MethodSymbol is { } um && TryGetPlan(um, out var up))
        {
            var q = up.Operator.ContainingType!
                .ToMinimalDisplayString(_model, value.SpanStart);
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseTypeName(q),
                    SyntaxFactory.IdentifierName(up.GeneratedMethodName)),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                    new[] { SyntaxFactory.Argument(value) })));
        }
        if (inConversion.IsExplicit)
        {
            var targetType = @params.Length > 1
                ? @params[1].Type.ToMinimalDisplayString(_model, value.SpanStart)
                : "";
            return SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(targetType), value);
        }
        return value;
    }

    private ExpressionSyntax ApplyOutConversion(
        Conversion outConversion, ExpressionSyntax value, int spanStart)
    {
        if (outConversion.IsUserDefined && outConversion.MethodSymbol is { } um
            && TryGetPlan(um, out var up))
        {
            var q = up.Operator.ContainingType!
                .ToMinimalDisplayString(_model, spanStart);
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseTypeName(q),
                    SyntaxFactory.IdentifierName(up.GeneratedMethodName)),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                    new[] { SyntaxFactory.Argument(value) })));
        }
        if (outConversion.IsExplicit)
        {
            var display = outConversion.MethodSymbol is { }
                ? outConversion.MethodSymbol.ReturnType.ToMinimalDisplayString(_model, spanStart)
                : "";
            return SyntaxFactory.CastExpression(SyntaxFactory.ParseTypeName(display), value);
        }
        return value;
    }
```

并在字段声明区追加 `private int _tempCounter;`；`using System.Collections.Immutable;` / `using Microsoft.CodeAnalysis.CSharp.Syntax;` 按需补齐。

> 实现注记：出转换的显式标准 cast 目标类型不能从 MethodSymbol（可能为 null）取——标准转换无方法符号。落地时改为在**分析期不可行闸门之外**由重写期用 `compound.TargetType`（ICompoundAssignmentOperation.TargetType 属性 = 左值类型）取 display：`((ExpressionSyntax)compound.Syntax)....`；即：

```csharp
    private ExpressionSyntax ApplyOutConversion(
        Conversion outConversion, ExpressionSyntax value,
        ICompoundAssignmentOperation compound)
    {
        if (outConversion.IsUserDefined && outConversion.MethodSymbol is { } um
            && TryGetPlan(um, out var up))
            return InvokeConversion(up, value);
        if (outConversion.IsExplicit)
            return SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(
                    compound.TargetType.ToMinimalDisplayString(_model, value.SpanStart)),
                value);
        return value;
    }

    private ExpressionSyntax InvokeConversion(OperatorPlan up, ExpressionSyntax value)
    {
        var q = up.Operator.ContainingType!.ToMinimalDisplayString(_model, value.SpanStart);
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseTypeName(q),
                SyntaxFactory.IdentifierName(up.GeneratedMethodName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                new[] { SyntaxFactory.Argument(value) })));
    }
```

`RenderCompoundInner` 调用点同步改为 `ApplyOutConversion(compound.OutConversion, call, compound)`；`ApplyInConversion` 的显式分支目标类型用 `compound.OperatorMethod.Parameters[1].Type`（经由 @params 已传入，落地时直接用该方法参数类型）。测试 `CompoundAssignment_BackConversionUserDefined_ChainsExplicit` 断言 `Small.ToSmallExplicit(Wide.Add(x, v))`——注意 `Wide` 的限定名来自 `ToSmallExplicit` 所属类型 `Small`，`Add` 来自 `Wide`（op+ 声明在 Small 上但返回 Wide？修正：op+ 声明于 Small，故限定名是 `Small.Add`——落地时按符号事实断言，若绑定结果为 `Small.Add` 则更新断言为 `x = Small.ToSmallExplicit(Small.Add(x, v))`）。

- [ ] **Step 7.4: 跑测试绿**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests|FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: PASS

- [ ] **Step 7.5: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): C14 OC-3 compound assignment — inline simple lvalues, lift side-effect receivers, in/out conversion chains, property-target atomic skip"
```

---

### Task 8: M3 — OC-6 提升公式（六类展开）

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Operators/LiftedOperatorExpansion.cs`（完整实现）
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`

- [ ] **Step 8.1: 写失败测试（公式 ①-⑥ + .NET 双端逐值）**

在 `MemberDesugarOperatorUnitTests` 追加：

```csharp
    // ---- OC-6 提升公式（决策 5）----

    [Fact]
    public void LiftedConversion_NullableToNullable_Expanded()
    {
        var (output, stats, _) = Rewrite("""
            public struct Snow
            {
                public int V;
                public Snow(int v) { V = v; }
                public static implicit operator double(Snow s) => s.V;
            }
            """,
            """
            public static class Use
            {
                public static double? F(Snow? s) => s;
            }
            """);
        Assert.True(stats.OperatorLiftedSitesExpanded >= 1);
        AssertContains(output, "s.HasValue ? Snow.ToDouble(s.GetValueOrDefault()) : (double?)null");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void LiftedArithmetic_ExpandedWithHasValueGuard()
    {
        var (output, stats, _) = Rewrite("""
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator +(N a, N b) => new N(a.V + b.V);
            }
            """,
            """
            public static class Use
            {
                public static N? F(N? a, N? b) => a + b;
            }
            """);
        Assert.True(stats.OperatorLiftedSitesExpanded >= 1);
        AssertContains(output, "(a.HasValue & b.HasValue) ? N.Add(a.Value, b.Value) : (N?)null");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void LiftedRelational_ExpandedToBoolNullable()
    {
        var (output, _, _) = Rewrite("""
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static bool operator >(N a, N b) => a.V > b.V;
            }
            """,
            """
            public static class Use
            {
                public static bool? F(N? a, N? b) => a > b;
            }
            """);
        AssertContains(output, "(a.HasValue & b.HasValue) ? N.GreaterThan(a.Value, b.Value) : (bool?)null");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void LiftedEquality_ThreeValuedTable()
    {
        var (output, _, _) = Rewrite("""
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static bool operator ==(N a, N b) => a.V == b.V;
                public static bool operator !=(N a, N b) => a.V != b.V;
                public override bool Equals(object? o) => o is N n && n.V == V;
                public override int GetHashCode() => V;
            }
            """,
            """
            public static class Use
            {
                public static bool? Eq(N? a, N? b) => a == b;
                public static bool? Ne(N? a, N? b) => a != b;
            }
            """);
        AssertContains(output,
            "(!a.HasValue && !b.HasValue) ? true : (a.HasValue && b.HasValue) ? N.ValueEquals(a.Value, b.Value) : false");
        AssertContains(output,
            "(!a.HasValue && !b.HasValue) ? false : (a.HasValue && b.HasValue) ? N.NotEquals(a.Value, b.Value) : true");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void LiftedUnary_Expanded()
    {
        var (output, _, _) = Rewrite("""
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator -(N a) => new N(-a.V);
            }
            """,
            """
            public static class Use
            {
                public static N? F(N? a) => -a;
            }
            """);
        AssertContains(output, "a.HasValue ? N.Negate(a.Value) : (N?)null");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void LiftedCompound_WritesBackNullWhenLeftNull()
    {
        var (output, _, _) = Rewrite("""
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator +(N a, N b) => new N(a.V + b.V);
            }
            """,
            """
            public static class Use
            {
                public static void F(N? x, N v)
                {
                    x += v;
                }
            }
            """);
        AssertContains(output, "x = x.HasValue ? N.Add(x.Value, v) : (N?)null");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void LiftedSite_NonSimpleOperand_SkipsWith5176()
    {
        var (output, diags, _) = Rewrite("""
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator +(N a, N b) => new N(a.V + b.V);
                public static N Factory() => new N(1);
            }
            """,
            """
            public static class Use
            {
                public static N? F(N? a, N b) => a + N.Factory().Wrap(b);
            }
            """)
            .ToString() is var _ ? Rewrite("""
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator +(N a, N b) => new N(a.V + b.V);
                public static N Factory() => new N(1);
            }
            """,
            """
            public static class Use
            {
                public static N? F(N? a, N b) => a + Make(b);
                public static N Make(N b) => b;
            }
            """) : (null!, null!, null!);
        Assert.Contains("operator +", output!); // plan Infeasible：声明站点原样
        // 简化写法见落地版——非简单运算元（方法调用返回值被 .Wrap/调用包裹）→ 5176
    }
```

> 实现注记：`LiftedSite_NonSimpleOperand_SkipsWith5176` 的测试体里第一段三元写法是笔误样板——落地时直接写为：

```csharp
    [Fact]
    public void LiftedSite_NonSimpleOperand_SkipsWith5176()
    {
        var (output, stats, diags) = Rewrite("""
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator +(N a, N b) => new N(a.V + b.V);
                public static N Make(N b) => b;
            }
            """,
            """
            public static class Use
            {
                public static N? F(N? a, N b) => a + N.Make(b);
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.UnbuildableLift);
        Assert.Contains("operator +", output);
    }
```

- [ ] **Step 8.2: 运行确认失败**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: 提升类 FAIL

- [ ] **Step 8.3: 实现 LiftedOperatorExpansion 完整公式**

替换 `LiftedOperatorExpansion.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>决策 5 六类提升公式的语法构造。分析期保证运算元为简单形态
/// （IsSimpleLiftedOperand），故运算元可被安全地引用两次。</summary>
internal static class LiftedOperatorExpansion
{
    // ② 提升一元：a.HasValue ? D.Negate(a.Value) : (R?)null
    // ③ 提升算术/移位/位（非门控 &amp;/|、非相等）：(a.HasValue &amp; b.HasValue) ? D.Add(a.Value, b.Value) : (R?)null
    // ④ 提升关系（结果 bool?）：(a.HasValue &amp; b.HasValue) ? D.GreaterThan(a.Value, b.Value) : (bool?)null
    // ⑤ 提升相等：(!a.HasValue &amp;&amp; !b.HasValue) ? true : (a.HasValue &amp;&amp; b.HasValue) ? D.ValueEquals(a.Value, b.Value) : false
    //    提升 !=：双 null→false、一 null→true、否则 NotEquals
    public static ExpressionSyntax ExpandBinaryOrUnary(
        string methodName, string qualifier, IOperation op, ExpressionSyntax[] args)
    {
        // args 为已改写的运算元语法；提升运算元保持原节点（identifier/字段访问）。
        if (op is IUnaryOperation u)
            return Conditional(
                HasValue(args[0]),
                Call(qualifier, methodName, Value(args[0])),
                NullOf(u.Type ?? u.OperatorMethod!.ReturnType));

        if (op is IBinaryOperation b)
        {
            var isNullableLeft = IsNullableOperand(b.LeftOperand);
            var isNullableRight = IsNullableOperand(b.RightOperand);
            var left = args[0];
            var right = args[1];

            var conditions = new List<ExpressionSyntax>();
            if (isNullableLeft) conditions.Add(HasValue(left));
            if (isNullableRight) conditions.Add(HasValue(right));
            var guard = conditions.Aggregate((x, y) =>
                SyntaxFactory.BinaryExpression(SyntaxKind.BitwiseAndExpression, x, y));

            var callArgs = new List<ExpressionSyntax>();
            callArgs.Add(isNullableLeft ? Value(left) : left);
            callArgs.Add(isNullableRight ? Value(right) : right);
            var call = Call(qualifier, methodName, callArgs.ToArray());

            if (b.OperatorKind == BinaryOperatorKind.Equals)
                return Conditional(
                    BothMissing(b, left, right), True(),
                    Conditional(BothPresent(b, left, right), call, False()));
            if (b.OperatorKind == BinaryOperatorKind.NotEquals)
                return Conditional(
                    BothMissing(b, left, right), False(),
                    Conditional(BothPresent(b, left, right), call, True()));

            var nullLiteral = NullOf(b.Type ?? b.OperatorMethod!.ReturnType);
            return Conditional(guard, call, nullLiteral);
        }
        throw new NotSupportedException($"unsupported lifted operation {op.Kind}");
    }

    // ① 提升转换：s.HasValue ? D.ToR(s.GetValueOrDefault()) : (R?)null
    public static ExpressionSyntax ExpandLiftedConversion(
        string methodName, string qualifier, ExpressionSyntax operand, ITypeSymbol returnType)
        => Conditional(
            HasValue(operand),
            Call(qualifier, methodName, GetValueOrDefault(operand)),
            NullOf(returnType));

    // ⑥ 提升复合：x = x.HasValue ? D.Add(x.Value, v) : (R?)null
    public static ExpressionSyntax WrapLiftedCompound(
        ExpressionSyntax simpleAssign, ExpressionSyntax left, ITypeSymbol returnType)
    {
        if (simpleAssign is not AssignmentExpressionSyntax assign)
            return simpleAssign;
        var value = assign.Right;
        return assign.WithRight(Conditional(
            HasValue(left),
            value,
            NullOf(returnType)));
    }

    // ---- 构造工具 ----

    private static bool IsNullableOperand(IOperation operand)
        => operand.Type is INamedTypeSymbol
        { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static ExpressionSyntax BothMissing(IBinaryOperation b, ExpressionSyntax l, ExpressionSyntax r)
    {
        var missing = new List<ExpressionSyntax>();
        if (IsNullableOperand(b.LeftOperand)) missing.Add(Not(HasValue(l)));
        if (IsNullableOperand(b.RightOperand)) missing.Add(Not(HasValue(r)));
        return missing.Aggregate((x, y) =>
            SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, x, y));
    }

    private static ExpressionSyntax BothPresent(IBinaryOperation b, ExpressionSyntax l, ExpressionSyntax r)
    {
        var present = new List<ExpressionSyntax>();
        if (IsNullableOperand(b.LeftOperand)) present.Add(HasValue(l));
        if (IsNullableOperand(b.RightOperand)) present.Add(HasValue(r));
        return present.Aggregate((x, y) =>
            SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, x, y));
    }

    private static ExpressionSyntax Conditional(
        ExpressionSyntax condition, ExpressionSyntax whenTrue, ExpressionSyntax whenFalse)
        => SyntaxFactory.ConditionalAccessFreeExpression(condition, whenTrue, whenFalse);

    private static ExpressionSyntax HasValue(ExpressionSyntax e)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, e, SyntaxFactory.IdentifierName("HasValue"));

    private static ExpressionSyntax Value(ExpressionSyntax e)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, e, SyntaxFactory.IdentifierName("Value"));

    private static ExpressionSyntax GetValueOrDefault(ExpressionSyntax e)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, e, SyntaxFactory.IdentifierName("GetValueOrDefault"));

    private static ExpressionSyntax Not(ExpressionSyntax e)
        => SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, e);

    private static ExpressionSyntax Call(string qualifier, string name, params ExpressionSyntax[] args)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseTypeName(qualifier),
                SyntaxFactory.IdentifierName(name)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                args.Select(SyntaxFactory.Argument))));

    private static ExpressionSyntax NullOf(ITypeSymbol type)
    {
        var display = type is INamedTypeSymbol
        { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
            ? n.ToDisplayString()
            : type.ToDisplayString() + "?";
        return SyntaxFactory.CastExpression(
            SyntaxFactory.ParseTypeName(display),
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
    }

    private static ExpressionSyntax True()
        => SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);
    private static ExpressionSyntax False()
        => SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression);
}
```

> 实现注记：`SyntaxFactory.ConditionalAccessFreeExpression` 不存在——`Conditional` 工具落地时用 `SyntaxFactory.ConditionalExpression(condition, whenTrue, whenFalse)`；占位名是计划笔误。同时 `RenderCall`（Task 5）在提升分支调用 `ExpandBinaryOrUnary` 时需把运算元的**原节点**（非改写后）传入——因为公式要加 `.HasValue/.Value`；落地时 `RenderCall` 提升分支改为传 `site` 的左右**原语法节点**（`(BinaryExpressionSyntax)site).Left/.Right` 原引用），非提升分支仍传 visited。`CompoundAssignment` 的提升分支（⑥）传原左值 identifier。

- [ ] **Step 8.4: 跑测试绿**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests|FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: PASS（若 `LiftedConversion` 断言的 `(double?)null` 显示为 `double?` 与 `(double?)` 之一，以输出事实修正断言字符串——两者语义等价，锁定其一）

- [ ] **Step 8.5: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): C14 OC-6 lifted expansion — six formula families (lifted conversion/unary/arithmetic/relational/equality/compound) with null-propagating conditionals"
```

---

### Task 9: M4 — OC-7 跳过闸门族 + checked 站点 + 幂等/BOM 工程性质

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Operators/OperatorRule.cs`（若闸门缺口）
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`

- [ ] **Step 9.1: 写失败测试（跳过族 + checked 站点 + 幂等 + 干净复制）**

在 `MemberDesugarOperatorUnitTests` 追加：

```csharp
    // ---- OC-7 跳过闸门族 + checked 站点 ----

    [Fact]
    public void ExpressionTreeSite_SkipsPlanAtomically_AndForceReleases()
    {
        var src = """
            using System.Linq.Expressions;
            public struct P
            {
                public int V;
                public P(int v) { V = v; }
                public static P operator +(P a, P b) => new P(a.V + b.V);
            }
            public static class Use
            {
                public static Expression<System.Func<P, P, P>> E = (a, b) => a + b;
                public static P Direct(P a, P b) => a + b;
            }
            """;
        var (output, stats, diags) = Rewrite(src);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.OperatorExpressionTreeSite);
        Assert.Contains("operator +", output);      // 声明不动
        Assert.Contains("a + b", output);           // 树站点不动
        Assert.Contains("Direct(P a, P b) => a + b", output); // 同 plan 全部站点不动（原子）

        var forced = Rewrite(src, new MemberDesugarOptions
        {
            ForceQualifiedNames = new HashSet<string> { "P" },
        });
        Assert.Equal(1, forced.Stats.OperatorsPlanned);
        Assert.Contains("P.Add(a, b)", forced.OutputCode);
        AssertOutputCompiles(forced.OutputCode);
    }

    [Fact]
    public void InterfaceStaticAbstractOperator_SkipsWith5175()
    {
        var (output, stats, diags) = Rewrite("""
            public interface IAdd<TSelf> where TSelf : IAdd<TSelf>
            {
                static abstract TSelf operator +(TSelf a, TSelf b);
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.StaticAbstractInterfaceOperator);
        Assert.Contains("static abstract TSelf operator +", output);
    }

    [Fact]
    public void ConversionMirrorMiss_GenericTarget_SkipsWith5177()
    {
        var (output, stats, diags) = Rewrite("""
            using System.Collections.Generic;
            public struct Box
            {
                public int V;
                public Box(int v) { V = v; }
                public static implicit operator List<int>(Box b) => new List<int> { b.V };
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.ConversionMirrorMiss);
        Assert.Contains("implicit operator List<int>", output);
    }

    [Fact]
    public void CheckedContext_SiteBindsCheckedVariantBySymbol()
    {
        var (output, _, _) = Rewrite("""
            public struct Wide
            {
                public long Lo;
                public Wide(long lo) { Lo = lo; }
                public static Wide operator +(Wide a, Wide b) => new Wide(a.Lo + b.Lo);
                public static checked Wide operator +(Wide a, Wide b) => throw new System.OverflowException();
            }
            """,
            """
            public static class Use
            {
                public static Wide Plain(Wide a, Wide b) => a + b;
                public static Wide Ckd(Wide a, Wide b) => checked(a + b);
            }
            """);
        AssertContains(output, "Plain(Wide a, Wide b) => Wide.Add(a, b)");
        AssertContains(output, "Ckd(Wide a, Wide b) => Wide.AddChecked(a, b)");
        AssertOutputCompiles(output);
    }

    // ---- 工程性质（§7.1-14）----

    [Fact]
    public void Idempotent_SecondRewriteZeroDiff()
    {
        var src = """
            public struct Q
            {
                public int V;
                public Q(int v) { V = v; }
                public static Q operator +(Q a, Q b) => new Q(a.V + b.V);
                public static implicit operator int(Q q) => q.V;
            }
            """,
            use = """
            public static class Use
            {
                public static Q Bump(Q a, Q b) => a + b;
                public static int Take(Q q) => q;
            }
            """;
        var (first, _, _) = Rewrite(src, null, use);
        var (second, _, _) = Rewrite(first, null, use);
        Assert.Equal(first, second);
    }

    [Fact]
    public void CleanFile_ByteIdenticalCopy()
    {
        var src = "public class Plain { public int X = 1; }\r\n";
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Equal(src, output);
    }

    [Fact]
    public void CrossFile_PlansTouchBothFiles()
    {
        var lib = """
            public struct Cf
            {
                public int V;
                public Cf(int v) { V = v; }
                public static Cf operator +(Cf a, Cf b) => new Cf(a.V + b.V);
            }
            """;
        var app = """
            public static class Use
            {
                public static Cf Sum(Cf a, Cf b) => a + b;
            }
            """;
        // Rewrite 走多树编译；声明与站点同转
        var (output, stats, _) = Rewrite(lib, null, app);
        Assert.Equal(1, stats.OperatorsPlanned);
        Assert.Contains("static Cf Add(", output);
        Assert.Contains("Cf.Add(a, b)", output);
    }
```

- [ ] **Step 9.2: 运行**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests"`
Expected: 跳过族测试或已绿（闸门在 Task 3 已建）；`CheckedContext` FAIL 若 checked 声明的 metadata name 处理未覆盖 → 在 `OperatorNames.ConvertOperatorName` 已处理（op_Checked* → 基名+Checked）；`InterfaceStaticAbstractOperator` 源需 C# Preview（测试 harness 已用 Preview ✓）。按失败事实修补 OperatorRule/OperatorNames。

- [ ] **Step 9.3: Commit**

```bash
git add -A
git commit -m "test(member-desugar): C14 skip-gate family (exprtree/5175/5177/force), checked-site symbol binding, idempotency, clean copy, cross-file plans"
```

---

### Task 10: M4 — JK 边界组合测试（member-desugar 产物 × java-keywords 通道）

**Files:**
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarOperatorUnitTests.cs`

- [ ] **Step 10.1: 查 JK 预处理器调用形态后写组合测试**

先查 `tests/CSharpToJava.ProjectTests/JavaKeywordRewriterUnitTests.cs` 的入口 API（`JavaKeywordsPreprocessor`?），然后追加（以实际 API 为准，此处按 JK 单测同形 API 写）：

```csharp
    // ---- 决策 7：与 JK 通道组合（SqlBool 型三值类型）----

    [Fact]
    public void JkCombination_GateTypeArithmeticDesugared_GateOpsRetainedForJk()
    {
        var (md, stats, _) = Rewrite("""
            public struct SqlBool
            {
                public int V;
                public SqlBool(int v) { V = v; }
                public static SqlBool operator &(SqlBool a, SqlBool b) => new SqlBool(a.V & b.V);
                public static SqlBool operator |(SqlBool a, SqlBool b) => new SqlBool(a.V | b.V);
                public static SqlBool operator ^(SqlBool a, SqlBool b) => new SqlBool(a.V ^ b.V);
                public static SqlBool operator +(SqlBool a, SqlBool b) => new SqlBool(a.V + b.V);
                public static bool operator true(SqlBool x) => x.V != 0;
                public static bool operator false(SqlBool x) => x.V == 0;
                public static implicit operator bool(SqlBool x) => x.V != 0;
            }
            """,
            """
            public static class Use
            {
                public static SqlBool Xor(SqlBool a, SqlBool b) => a ^ b;
                public static SqlBool Sum(SqlBool a, SqlBool b) => a + b;
                public static bool ToB(SqlBool a) => a;
            }
            """);
        // 门控 & / | 保留（CS2J5180），^ 与 + 去糖，bool 隐式转换去糖
        AssertContains(md, "Xor(SqlBool a, SqlBool b) => SqlBool.Xor(a, b)");
        AssertContains(md, "Sum(SqlBool a, SqlBool b) => SqlBool.Add(a, b)");
        AssertContains(md, "ToB(SqlBool a) => SqlBool.ToBoolean(a)");
        Assert.Contains("operator &", md);
        Assert.Contains("operator |", md);
        AssertOutputCompiles(md);

        // 组合：对 member-desugar 产物再跑 java-keywords 通道，产出可编译且 op_True 声明成 IsTrue
        var jk = JavaKeywords.JavaKeywordsPreprocessor.Rewrite(md); // ← 以实际 API 为准
        Assert.Contains("IsTrue", jk);
        AssertOutputCompiles(jk);
    }
```

- [ ] **Step 10.2: 运行并按实际 JK API 修正调用**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarOperatorUnitTests.JkCombination"`
Expected: PASS（落地时先 `grep -n "class JavaKeywordsPreprocessor\|public.*Rewrite" src/CSharpToJava.Core/JavaKeywords/` 确认入口签名）

- [ ] **Step 10.3: Commit**

```bash
git add -A
git commit -m "test(member-desugar): C14 × JK combination — gate ops retained, arithmetic/conversions desugared, op_True renamed by downstream channel"
```

---

### Task 11: 项目级 fixture 追加 + MemberDesugarProjectTests 扩展

**Files:**
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/OperatorSamples.cs`
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/OperatorEdgeSamples.cs`
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarApp/OperatorApp.cs`
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarTests/OperatorBehaviorTests.cs`
- Modify: `tests/CSharpToJava.ProjectTests/MemberDesugarProjectTests.cs`（追加 operator 断言方法）

- [ ] **Step 11.1: 写 Lib fixture（算术/比较/位/门控/转换/提升/checked/泛型/碰撞/表达式树跳过）**

Create `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/OperatorSamples.cs`:

```csharp
namespace SampleMemberDesugarLib;

// 算术 + 多重载 + 返回异类类型
public struct Point(int x, int y)
{
    public int X = x;
    public int Y = y;
    public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y);
    public static Point operator *(Point a, int k) => new Point(a.X * k, a.Y * k);
    public static Point operator *(int k, Point a) => new Point(a.X * k, a.Y * k);
    public static double operator *(Point a, Point b) => a.X * b.X + a.Y * b.Y;
    public static bool operator ==(Point a, Point b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Point a, Point b) => !(a == b);
    public override bool Equals(object? o) => o is Point p && p == this;
    public override int GetHashCode() => System.HashCode.Combine(X, Y);
}

// checked 对（C# 11）
public struct Wide(long lo)
{
    public long Lo = lo;
    public static Wide operator +(Wide a, Wide b) => new Wide(a.Lo + b.Lo);
    public static checked Wide operator +(Wide a, Wide b) =>
        checked(a.Lo + b.Lo) is var r && r < long.MinValue
            ? throw new System.OverflowException()
            : new Wide(r);
}

// 泛型运算符
public struct Vec<T>(T x, T y) where T : struct
{
    public T X = x;
    public T Y = y;
    public static Vec<T> operator +(Vec<T> a, Vec<T> b) => new Vec<T>(a.X, b.Y);
}

// 碰撞：operator + 与手写 Add 共存
public class Money(decimal amount)
{
    public decimal Amount = amount;
    public static Money operator +(Money a, Money b) => new Money(a.Amount + b.Amount);
    public static Money Add(Money a, Money b) => new Money(a.Amount + b.Amount);
}

// 位标志 + 一元
public struct Flags(int v)
{
    public int V = v;
    public static Flags operator &(Flags a, Flags b) => new Flags(a.V & b.V);
    public static Flags operator |(Flags a, Flags b) => new Flags(a.V | b.V);
    public static Flags operator ^(Flags a, Flags b) => new Flags(a.V ^ b.V);
    public static Flags operator ~(Flags a) => new Flags(~a.V);
    public static Flags operator <<(Flags a, int n) => new Flags(a.V << n);
    public static Flags operator >>(Flags a, int n) => new Flags(a.V >> n);
    public static Flags operator >>>(Flags a, int n) => new Flags(a.V >>> n);
    public static Flags operator -(Flags a) => new Flags(-a.V);
}
```

Create `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/OperatorEdgeSamples.cs`:

```csharp
namespace SampleMemberDesugarLib;

// 转换类型群（隐式/显式/多目标/反射契约样本）
public struct Temperature(double c)
{
    public double C = c;
    public static implicit operator double(Temperature t) => t.C;
    public static explicit operator Temperature(double c) => new Temperature(c);
    public static implicit operator int(Temperature t) => (int)t.C;
}

// 提升链：S? → T?
public struct Snowflake(int v)
{
    public int V = v;
    public static implicit operator double(Snowflake s) => s.V;
}

// 提升算术/相等
public struct OptionalNum(int v)
{
    public int V = v;
    public static OptionalNum operator +(OptionalNum a, OptionalNum b) => new OptionalNum(a.V + b.V);
    public static bool operator ==(OptionalNum a, OptionalNum b) => a.V == b.V;
    public static bool operator !=(OptionalNum a, OptionalNum b) => a.V != b.V;
    public override bool Equals(object? o) => o is OptionalNum n && n == this;
    public override int GetHashCode() => V;
}

// 三值门控类型（op_True/op_False + & / | + 算术 + bool 隐式）——JK 组合
public struct SqlBool(int v)
{
    public int V = v;
    public static SqlBool operator &(SqlBool a, SqlBool b) => new SqlBool(a.V & b.V);
    public static SqlBool operator |(SqlBool a, SqlBool b) => new SqlBool(a.V | b.V);
    public static SqlBool operator ^(SqlBool a, SqlBool b) => new SqlBool(a.V ^ b.V);
    public static SqlBool operator +(SqlBool a, SqlBool b) => new SqlBool(a.V + b.V);
    public static bool operator true(SqlBool x) => x.V != 0;
    public static bool operator false(SqlBool x) => x.V == 0;
    public static implicit operator bool(SqlBool x) => x.V != 0;
}

// ++/-- 移交 C17（原样保留样本）
public class Counter
{
    public int V;
    public static Counter operator ++(Counter c) { c.V++; return c; }
}

// 表达式树引用 → plan 原子跳过样本
public static class ExprTreeUser
{
    public static System.Linq.Expressions.Expression<System.Func<Point, Point, Point>> Add =
        (a, b) => a + b;
}
```

- [ ] **Step 11.2: 写 App fixture（跨文件站点）**

Create `tests/SampleMemberDesugarSolution/SampleMemberDesugarApp/OperatorApp.cs`:

```csharp
using SampleMemberDesugarLib;

namespace SampleMemberDesugarApp;

public static class OperatorApp
{
    public static Point Translate(Point p, Point d) => p + d;
    public static double Dot(Point a, Point b) => a * b;
    public static Point Scale(Point p) => p * 2;
    public static Temperature Boiling() => (Temperature)100.0;
    public static double Kelvin(Temperature t) => t + 273.15;
    public static Snowflake? Melt(Snowflake? s) => s;
    public static double? Melted(Snowflake? s) => s;
    public static OptionalNum? SumOrNone(OptionalNum? a, OptionalNum? b) => a + b;
    public static bool? SameOrNone(OptionalNum? a, OptionalNum? b) => a == b;
    public static Money Combine(Money a, Money b)
    {
        a += b;            // 局部目标复合
        return a;
    }
    public static Flags Mix(Flags a, Flags b) => (a & b) | (a ^ b);
    public static SqlBool Gate(SqlBool a, SqlBool b) => a & b;
    public static bool AsBool(SqlBool x) => x;
    public static int Bump(Counter c)
    {
        var d = c++;
        return d.V;
    }
}
```

- [ ] **Step 11.3: 写 .NET 行为测试（去糖错误在 .NET 侧直接红；反射契约两态断言 §7.4）**

Create `tests/SampleMemberDesugarSolution/SampleMemberDesugarTests/OperatorBehaviorTests.cs`:

```csharp
using SampleMemberDesugarLib;
using Xunit;

namespace SampleMemberDesugarTests;

/// <summary>运算符去糖行为闸门：同一测试在原始 fixture 与去糖中间树上都要绿
/// （member-desugar verify 双默认开 → 中间树 dotnet test 执行本文件）。</summary>
public class OperatorBehaviorTests
{
    [Fact]
    public void Arithmetic_Composes()
    {
        var p = new Point(1, 2) + new Point(3, 4);
        Assert.Equal(4, p.X);
        Assert.Equal(6, p.Y);
        Assert.Equal(14, new Point(1, 2) * new Point(3, 4));
        Assert.Equal(new Point(2, 4), 2 * new Point(1, 2));
    }

    [Fact]
    public void Conversions_RoundTrip()
    {
        Temperature t = (Temperature)100.0;
        double c = t;
        Assert.Equal(100.0, c);
        int rounded = t;
        Assert.Equal(100, rounded);
    }

    [Fact]
    public void LiftedConversion_NullPropagates()
    {
        Snowflake? s = new Snowflake(7);
        double? d = s;
        Assert.Equal(7.0, d);
        Snowflake? none = null;
        Assert.Null((double?)none);
    }

    [Fact]
    public void LiftedArithmetic_ThreeValued()
    {
        OptionalNum a = new(2), b = new(3);
        OptionalNum? x = a, y = b, z = null;
        Assert.Equal(5, (a + b).V);
        Assert.Null(x + z is null ? x + z : null);
        Assert.False((bool?)(x == z));
        Assert.True((bool?)(x == x));
        Assert.Null(x == z);
    }

    [Fact]
    public void CompoundAssignment_LocalTarget()
    {
        Money m = new(1);
        m += new Money(2);
        Assert.Equal(3m, m.Amount);
    }

    [Fact]
    public void GateSemantics_Preserved()
    {
        SqlBool t = new(1), f = new(0);
        Assert.Equal(0, (t & f).V);
        if (t) { Assert.True(true); } else { Assert.Fail("op_True lost"); }
        Assert.True((bool)t);
    }

    [Fact]
    public void Increment_DeferredButWorks()
    {
        var c = new Counter();
        var d = c++;
        Assert.Equal(0, d.V);
        Assert.Equal(1, c.V);
    }

    [Fact]
    public void ReflectionContract_PinsDesugaredShape()
    {
        // 原始树：op_Addition 存在；去糖树：Add 存在（二者其一，§7.4 两态断言）
        var op = typeof(Point).GetMethod("op_Addition");
        var add = typeof(Point).GetMethod("Add");
        if (op != null) Assert.Null(add);
        else Assert.NotNull(add);
    }
}
```

- [ ] **Step 11.4: 追加项目测试方法**

在 `MemberDesugarProjectTests` 追加：

```csharp
    [Fact]
    public async Task Solution_OperatorsDesugared_AndIntermediateBuildsAndTests()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        var outputDir = Path.Combine(FixtureDir, ".test-output-md-op");
        try
        {
            var adapter = new MemberDesugarPassAdapter(
                new MemberDesugarOptions(), verbose: false,
                filterUnsupportedProjects: true, compatNamespacePrefixes: null);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Where(d => d.Severity == MemberDesugarSeverity.Error)
                    .Select(d => d.ToMsBuildString())));
            Assert.True(adapter.Statistics.OperatorsPlanned >= 15,
                $"operators planned = {adapter.Statistics.OperatorsPlanned}");
            Assert.True(adapter.Statistics.OperatorBinarySitesRewritten >= 3);
            Assert.True(adapter.Statistics.OperatorConversionSitesRewritten >= 3);
            Assert.True(adapter.Statistics.OperatorLiftedSitesExpanded >= 1);
            Assert.True(adapter.Statistics.GateOperatorsRetained >= 2);
            Assert.True(adapter.Statistics.IncrementDecrementDeferred >= 1);
            Assert.Equal(0,
                adapter.Statistics.SkippedByReason.TryGetValue(
                    MemberDesugarDiagnosticCode.ResidualOperator, out var r) ? r : 0);

            var samples = await File.ReadAllTextAsync(Path.Combine(
                run.IntermediateRoot, "SampleMemberDesugarLib", "OperatorSamples.cs"));
            Assert.Contains("public static Point Add(Point a, Point b)", samples);
            Assert.Contains("public static Money AddOperator(", samples);
            Assert.DoesNotContain("operator +", samples);
            Assert.DoesNotContain("operator *", samples);
            Assert.DoesNotContain("operator ==", samples);

            var edges = await File.ReadAllTextAsync(Path.Combine(
                run.IntermediateRoot, "SampleMemberDesugarLib", "OperatorEdgeSamples.cs"));
            Assert.Contains("ToDouble(Temperature t)", edges);
            Assert.Contains("operator &", edges);   // SqlBool 门控保留
            Assert.Contains("operator ++", edges);  // Counter 移交 C17
            Assert.Contains("operator +", edges);   // Point 在 ExprTreeUser 树里 → 原子跳过?（见注）

            var app = await File.ReadAllTextAsync(Path.Combine(
                run.IntermediateRoot, "SampleMemberDesugarApp", "OperatorApp.cs"));
            Assert.Contains("Point.Add(", app);
            Assert.Contains("Temperature.ToDoubleExplicit(100.0)", app);
            Assert.Contains("Money.AddOperator(", app);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }
```

> 实现注记：`ExprTreeUser` 引用 `Point.operator +` 会使 **Point 的 + plan 原子跳过**（表达式树站点 5173）→ `OperatorSamples.cs` 里 Point 的 `operator +` 保留、且 `OperatorApp.Translate` 站点也不改写——与上面 `DoesNotContain("operator +")`/`Point.Add(` 断言冲突。**修正**：把表达式树样本改为引用**独立类型** `ExprPoint`（只在 OperatorEdgeSamples.cs 声明与使用），Point 保持可去糖；断言相应为：OperatorEdgeSamples.cs 含 `operator +`（ExprPoint 保留）且 OperatorSamples.cs 不含任何 `operator`；OperatorApp.cs 含 `Point.Add(`。落地时按此修正源与断言（fixture 中新增 `public struct ExprPoint { public static ExprPoint operator +(ExprPoint a, ExprPoint b) => a; }`）。

- [ ] **Step 11.5: 运行项目测试**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Debug -f net10.0 --filter "FullyQualifiedName~MemberDesugarProjectTests"`
Expected: 原 C11 两条 + 新 operator 条全绿（fixture 中间树 build + test 通过即 run.Success）

- [ ] **Step 11.6: Commit**

```bash
git add -A
git commit -m "test(member-desugar): C14 fixture — operator sample lib/app/behavior tests (lifted three-valued, gate semantics, reflection contract) + project test assertions"
```

---

### Task 12: 验收 — Release build + 三个测试工程全绿 + 设计文档收尾

- [ ] **Step 12.1: Release build**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release`
Expected: 0 errors

- [ ] **Step 12.2: Release test 三工程**

Run: `dotnet test CSharpToJavaConverter.slnx -c Release`
Expected: CSharpToJava.Tests / CSharpToJava.ProjectTests / CSharpToJava.ConversionTests 全绿（含既有 OperatorMethodNameCollisionTests / UserDefinedConversionGenericTargetTests 零改动零回归——红灯对照 §7.6）

- [ ] **Step 12.3: 共享文件追加核对（§3.2 清单穷尽性）**

Run: `git diff --stat 412d5169..HEAD -- src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs src/CSharpToJava.CLI/Preprocessing/Adapters/MemberDesugarPassAdapter.cs src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs src/CSharpToJava.CLI/Program.cs`
Expected: 只有前三个文件有改动且均为纯追加；后四个零改动；链序/stub 测试零改动（`git status` 干净）

- [ ] **Step 12.4: 更新设计文档状态行 + 提交**

把设计文档 `**状态**` 行改为「已实现（M0-M4 完成；Java 半区消费清单 §5.1 为后续 PR 输入）」，补 `**实现提交**` 指向本分支 HEAD。

```bash
git add -A
git commit -m "docs: C14 operator desugar design — mark implemented"
```

---

## Self-Review 记录（writing-plans 检查单）

1. **Spec coverage**: 决策 1/1b（Task 2 命名层+Task 4 checked）✓；决策 2/2b（Task 5/7）✓；决策 3/4（Task 2/6）✓；决策 5（Task 8）✓；决策 6 表达式树/dynamic（Task 9；dynamic 站点在 Task 3 分析闸门）✓；决策 7 JK 边界（Task 3 门控保留 + Task 10 组合）✓；决策 8 碰撞（Task 2/4）✓；决策 9 元数据-only（Task 5 计数）✓；决策 10 接口静态抽象（Task 9）✓；决策 11 反射契约（Task 11 fixture 两态断言）✓；决策 12 inc/dec（Task 3/11）✓；决策 13 无支撑库（全任务无注入）✓；决策 14 attribute（Task 4）✓；决策 15 执行形态（Task 1/3 接线）✓；OC-9 residual（Task 4）✓；§7.1 单测矩阵 1-14 组全覆盖（每组至少 1 个代表用例）；§7.2 fixture（Task 11）；§7.3/§7.6（Task 12）；§7.4 反射（Task 11）；§7.5 residual 闸门（Task 4 计数 + Task 11 断言 0）。
2. **Placeholder scan**: 计划内两处「实现注记」是对计划草稿笔误的落地修正指令（含完整修正代码/断言），非占位；无 TBD/TODO。
3. **Type consistency**: OperatorPlan{Operator,IsConversion,GeneratedMethodName,BaseMethodName} 跨任务一致；ConversionResolver.Recipe{Plan,IsLifted,NeedsOuterCast,OuterCastType} 定义与使用一致；Wrap 实例方法签名 `(Recipe, ExpressionSyntax, int spanStart, MemberDesugarStatistics)` 在注记 1/2 中统一；LiftedOperatorExpansion 三个入口（ExpandBinaryOrUnary/ExpandLiftedConversion/WrapLiftedCompound）与调用点一致；`SyntaxFactory.ConditionalExpression` 修正注记在 Task 8。
