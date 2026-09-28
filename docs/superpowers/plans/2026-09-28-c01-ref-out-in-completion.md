# C01 RefOutIn 接线收尾 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成 ref-out-in pass 的接线收尾（catalog C01）：C# 中间树可编译闭环（语句级提升、ref 返回、lambda、ref struct holder）、Java 侧 ByRef holder 映射（compat 新类 + TypeMappings + 转换器识别）、旧断言迁移与全量回归。

**Architecture:** 不新增 pass、不改链契约（spec §3 冻结表）。Core 侧重写器获得一个新语句提升器 `RefOutInStatementLifter`（短路保持的通用提升）；支撑库新增泛型 `RefStructByRef<T>`（Q2-A 定案，槽模式）；Java compat 新增 `ByRef`/`RefStructByRef` 与 C# 形态同构；TypeMappings 注册 arity 形式条目（`Cs2jByRef.ByRef\`1`）；`.Value` 走转换器既有属性约定（Q3 定案零配置）；后置自检升级并以 CS2J3305 接入生产路径。

**Tech Stack:** .NET 10 / Roslyn（CSharpSyntaxRewriter + SemanticModel）、xunit、JUnit 5 + Maven（compat）、JSON TypeMappings。

**Spec:** `docs/superpowers/specs/2026-09-28-c01-ref-out-in-completion-design.md`（已核订版）

**Worktree:** 按 spec §12 —— `git worktree add .worktrees/preproc-c01-refout-finish -b feature/preproc-c01-refout-finish <dev-3.x HEAD>`（base 含 spec 核订提交 `28b132cb`，创建前 `git rev-parse dev-3.x` 现场核实）。所有任务在该 worktree 内执行；每个 Task 结束 `git commit`。

**构建/测试命令（worktree 根目录）:**
- Core + CLI 构建：`dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q`
- 全部测试：`dotnet test CSharpToJavaConverter.slnx -c Release --nologo --filter "FullyQualifiedName~RefOutIn"`
- 单测项目：`dotnet test tests/CSharpToJava.ProjectTests -c Release --nologo`
- compat：`cd java/csharptojava-compat && mvn -q test`

---

## File Structure（新建/修改文件总览）

```
src/CSharpToJava.Core/RefOutInPreprocessor/
  RefOutInStatementLifter.cs        [新建] 语句级提升引擎（C1/C2/C3/C6/C13）
  RefOutInSyntaxRewriter.cs         [修改] 接入提升器；ref 返回/属性/索引器（C4）；
                                            lambda/局部函数（C5）；RefStructByRef 形参（C7）；
                                            外部转发 .Value（C6）；槽名作用域栈
  RefOutInAliasAnnotator.cs         [修改] lambda/局部函数分类；in 碰撞判定（C9）；
                                            ref 字段 fail-fast（C12/Q5）；IsLocked 提为 internal
  RefOutInRewriter.cs               [修改] using 注入条件放宽（C8）；并行诊断码
  RefOutInPostVerifier.cs           [重写] 输出树语义断言（C10/CS2J3305）
  RefOutInDiagnosticCodes.cs        [新建] CS2J33xx 常量
  RefOutInMangling.cs               [删除] Q2-A 落地后不再需要
  RefOutInRewriterStatistics.cs     [修改] 计数微调（RefFieldsRejected 等）
src/Cs2jRefOutIn/
  RefStructByRef.cs                 [新建] 泛型槽模式 ref struct holder（Q2-A）
src/CSharpToJava.CLI/
  RefOutInSupportFiles.cs           [修改] 释放 RefStructByRef.cs 资源；删除按类型生成（S1）
  Preprocessing/Adapters/RefOutInPassAdapter.cs [修改] 收集输出；AfterFilesAsync 跑自检；
                                            并行 CS2J33xx 码（S5）
config/TypeMappings.json            [修改] J1/J2 类型条目 + J4 Create 方法条目
src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs [修改] J4/J8
java/csharptojava-compat/src/main/java/io/github/ningpp/compat/
  ByRef.java                        [新建]
  RefStructByRef.java               [新建]
java/csharptojava-compat/src/test/java/io/github/ningpp/compat/
  ByRefTest.java                    [新建]
tests/SampleRefOutInV2Solution/     [修改] csproj OutputType + Tests 工程 + 矩阵场景（S4）
tests/CSharpToJava.ProjectTests/
  RefOutInRewriterUnitTests.cs      [修改] 真实编译断言 + §7.1 全矩阵
  RefOutInPreprocessorTests.cs      [修改] 打开 verify
tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs [修改] §7.4 迁移
tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs [修改] §7.4 迁移
tests/CSharpToJava.Tests/RefOutSameVariableTests.cs [删除]（空壳，22 行无测试）
prompts/preprocessor-catalog.md     [修改] C01 状态（M4）
ROADMAP.md                          [修改]（M4）
```

---

### Task 0: Worktree 准备

- [ ] **Step 1: 创建 worktree**

```bash
cd /d/code/cs2j
BASE=$(git rev-parse dev-3.x)
git worktree add .worktrees/preproc-c01-refout-finish -b feature/preproc-c01-refout-finish "$BASE"
cd .worktrees/preproc-c01-refout-finish
```

Expected: `Preparing worktree ... new branch feature/preproc-c01-refout-finish`；后续所有命令在该目录执行。

- [ ] **Step 2: 确认基线构建绿**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q | tail -3`
Expected: `Build succeeded` / 0 错误（若基线本身红，停下来报告，不要带病开工）。

---

### Task 1: Q2 探针——`ref struct RefStructByRef<T>` 可行性（spec §2.2 Q2 的 10 行探针）

**Files:**
- Create: `$TMP/probe/RefStructByRef.cs`（临时，不入库）

- [ ] **Step 1: 写探针并编译运行**

```bash
mkdir -p /tmp/refprobe && cat > /tmp/refprobe/Program.cs <<'EOF'
using System;

var slot = Probe.RefStructByRef<Span<int>>.Create(stackalloc int[3].ToArray()); // Span 通过 T 字段承载
Span<int> s = new int[] { 1, 2, 3 };
var h = Probe.RefStructByRef<Span<int>>.Create(s);
h.Value = s.Slice(1);
Console.WriteLine(h.Value[0] == 2);
var e = Probe.RefStructByRef<Span<int>>.Create();
Console.WriteLine(e.Value == default);

namespace Probe
{
    public ref struct RefStructByRef<T>
    {
        private T _slot;
        public RefStructByRef() { _slot = default!; }
        private RefStructByRef(T initial) { _slot = initial; }
        public static RefStructByRef<T> Create() => new();
        public static RefStructByRef<T> Create(T initial) => new(initial);
        public T Value { get => _slot; set => _slot = value; }
    }
}
EOF
cat > /tmp/refprobe/probe.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
EOF
dotnet run --project /tmp/refprobe -c Release
```

Expected stdout: `True` 和 `True`（0 退出码）。**若编译失败**（CS8345 类），回退 Q2 方案 B 并停下重评 spec——不要继续 Task 8 之前的任何 ref struct 工作。

- [ ] **Step 2: 记录结论**

在 commit message 中记录探针结果：`git commit --allow-empty -m "chore: Q2 probe passed — ref struct RefStructByRef<T> viable on net10.0"`

---

### Task 2: S4 前半——V2 fixture 可构建（OutputType + 测试工程）

**Files:**
- Modify: `tests/SampleRefOutInV2Solution/SampleRefOutInV2App/SampleRefOutInV2App.csproj`
- Create: `tests/SampleRefOutInV2Solution/SampleRefOutInV2Tests/SampleRefOutInV2Tests.csproj`
- Create: `tests/SampleRefOutInV2Solution/SampleRefOutInV2Tests/ByRefSemanticsTests.cs`

- [ ] **Step 1: 修 App csproj（CS8805）**

`SampleRefOutInV2App.csproj` 的 `<PropertyGroup>` 加入 `<OutputType>Exe</OutputType>`（放在 `<TargetFramework>` 之前）。

- [ ] **Step 2: 新建测试工程**

`SampleRefOutInV2Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SampleRefOutInV2Lib\SampleRefOutInV2Lib.csproj" />
  </ItemGroup>
</Project>
```

（版本号先查 `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` 中现有 xunit 版本并保持一致。）
把测试工程加入 `SampleRefOutInV2.sln`（用 `dotnet sln add`）。

- [ ] **Step 3: 写语义测试（这些断言就是中间树 `dotnet test` 的预言机）**

```csharp
using SampleRefOutInV2Lib;
using Xunit;

namespace SampleRefOutInV2Tests;

public class ByRefSemanticsTests
{
    [Fact]
    public void Swap_WritesBackBothSides()
    {
        int a = 1, b = 2;
        Calculator.Swap(ref a, ref b);
        Assert.Equal(2, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void TryGet_WritesOutParam()
    {
        var calc = new Calculator();
        Assert.True(calc.TryGet(out var result));
        Assert.Equal(42, result);
    }

    [Fact]
    public void TryParse_DeclarationOut()
    {
        Assert.True(Calculator.TryParse("x", out var p));
        Assert.Equal(0, p.X);
    }

    [Fact]
    public void UpdateField_WritesArrayElement()
    {
        var calc = new Calculator();
        int[] arr = { 5, 6 };
        calc.UpdateField(ref arr[0]);
        Assert.Equal(6, arr[0]);
    }

    [Fact]
    public void RefReturn_PreservesAliasing()
    {
        var calc = new Calculator();
        int[] arr = { 7, 8 };
        ref int slot = ref calc.GetRef(arr, 1);
        slot = 100;
        Assert.Equal(100, arr[1]);
    }

    [Fact]
    public void GenericHolder_RefAndOutRoundTrip()
    {
        var holder = new GenericHolder<int>();
        holder.Value = 10;
        holder.Update(ref holder.Value);
        Assert.Equal(10, holder.Value);
        Assert.True(holder.TryGet(out var v));
        Assert.Equal(10, v);
    }
}
```

- [ ] **Step 4: 源 fixture 自身 build/test 绿（预处理前基线）**

Run: `dotnet test tests/SampleRefOutInV2Solution/SampleRefOutInV2.sln -c Release --nologo | tail -5`
Expected: 全部通过（6 个用例）。

- [ ] **Step 5: Commit**

```bash
git add tests/SampleRefOutInV2Solution
git commit -m "test(c01): make SampleRefOutInV2 compilable (OutputType) and add xunit test project (S4)"
```

---

### Task 3: C1/C2/C3——语句级提升引擎 `RefOutInStatementLifter`

**Files:**
- Create: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInStatementLifter.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs`（槽名集合改作用域栈，见 Step 2）

**设计要点（实现前必读）:**
1. **所有语义查询只打在原始树节点上**；生成片段是 ParseExpression 文本构造，无 ref/out 关键字，可安全被 visitor 重访（fragment 内标识符由 `VisitIdentifierName` 统一注入 `.Value`，因此**提升器自己绝不预注入 .Value**）。
2. 直传槽（holder slot 直接作为内部 ref/out 实参）以 `SyntaxAnnotation` 标记豁免注入。
3. 短路保持：`&&/||/??/?:` 的条件侧操作数经 if-块条件拼接；无条件位置的操作数正常提升。
4. 提升顺序：按「深度优先降序 + 文档序」；读回语句按实参逆序（D3：前置左→右、读回逆序）。
5. `out` 实参一律走**槽 + 读回**（避免未赋值局部被 getter 闭包捕获触发 CS0165）；`ref` 实参走**访问器闭包**（别名保真），但表达式内含 ref struct 局部/参数时退化为**拷入拷出槽**（闭包不能捕获 ref struct，CS8175）。
6. 数组元素：一维数组走 `(arr, i)` 数组工厂；多维 → CS2J3303；非数组索引器（含 Span）→ 拷入拷出槽。
7. 声明式 out（`out var x`/`out T x`）→ 前置声明 + 槽 + 读回；`out _` → 仅槽。
8. C# 合法的 ref/out 实参必为左值，穷举：IdentifierName / ElementAccess / MemberAccess（含 this.f）/ DeclarationExpression / Discard；其余形状 → CS2J3303。

- [ ] **Step 1: 写 `RefOutInStatementLifter.cs`（完整文件）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Statement-level lifting of internal ref/out call sites (C1/C2/C3).
/// Splices ByRef proxy construction out of arbitrary expression positions while
/// preserving short-circuit evaluation, left-to-right side effects and reverse
/// read-back order. Semantic lookups only ever target ORIGINAL-tree nodes.
/// Generated fragments contain no ref/out keywords, so the visitor may re-visit
/// them safely; .Value injection on original identifiers stays in
/// RefOutInSyntaxRewriter.VisitIdentifierName (this class never pre-injects).
/// </summary>
internal sealed class RefOutInStatementLifter
{
    internal static readonly SyntaxAnnotation DirectPassAnnotation = new("Cs2jDirectPass");

    private readonly RefOutInSyntaxRewriter _owner;
    private List<StatementSyntax> _out = new();
    private readonly Stack<List<StatementSyntax>> _scope = new();

    public RefOutInStatementLifter(RefOutInSyntaxRewriter owner) => _owner = owner;

    // ── entry point ─────────────────────────────────────────────────────────

    /// <summary>
    /// Splice one expression. Appends hoisted statements to <paramref name="hoisted"/>
    /// and returns the remainder (unchanged when nothing was lifted).
    /// </summary>
    public ExpressionSyntax SpliceExpression(ExpressionSyntax expr, out List<StatementSyntax> hoisted)
    {
        _out = new List<StatementSyntax>();
        var remainder = Splice(expr);
        hoisted = _out;
        return remainder;
    }

    public bool ContainsLiftable(ExpressionSyntax expr)
    {
        foreach (var inv in InvocationsOutsideLambdas(expr))
            if (IsLiftable(inv)) return true;
        return false;
    }

    // ── expression splicing ─────────────────────────────────────────────────

    private ExpressionSyntax Splice(ExpressionSyntax expr)
    {
        if (expr is InvocationExpressionSyntax inv && IsLiftable(inv))
            return LiftInvocation(inv);

        switch (expr.Kind())
        {
            case SyntaxKind.LogicalAndExpression:
            case SyntaxKind.LogicalOrExpression:
                return SpliceShortCircuit((BinaryExpressionSyntax)expr);
            case SyntaxKind.CoalesceExpression:
                return SpliceCoalesce((BinaryExpressionSyntax)expr);
            case SyntaxKind.ConditionalExpression:
                return SpliceConditional((ConditionalExpressionSyntax)expr);
            default:
                return SpliceChildren(expr);
        }
    }

    private ExpressionSyntax SpliceShortCircuit(BinaryExpressionSyntax bin)
    {
        var left = Splice(bin.Left);
        if (!ContainsLiftable(bin.Right))
            return bin.WithLeft(left).WithRight(Splice(bin.Right));

        bool isAnd = bin.IsKind(SyntaxKind.LogicalAndExpression);
        var temp = _owner.NextTemp(isAnd ? "__sc" : "__sc");
        _out.Add(ParseStmt($"bool {temp} = {(isAnd ? "false" : "true")};"));
        _scope.Push(_out);
        _out = new List<StatementSyntax>();
        var rightValue = Splice(bin.Right);
        _out.Add(ParseStmt($"{temp} = {rightValue};"));
        var conditional = BlockAndPop();

        var gate = isAnd ? left : Negate(left);
        _out.Add(SyntaxFactory.IfStatement(gate, conditional));
        return Identifier(temp);
    }

    private ExpressionSyntax SpliceCoalesce(BinaryExpressionSyntax bin)
    {
        var left = Splice(bin.Left);
        if (!ContainsLiftable(bin.Right))
            return bin.WithLeft(left).WithRight(Splice(bin.Right));

        var typeName = TypeNameOf(bin);
        var temp = _owner.NextTemp("__cs");
        _out.Add(ParseStmt($"{typeName} {temp} = {left};"));
        _scope.Push(_out);
        _out = new List<StatementSyntax>();
        var rightValue = Splice(bin.Right);
        _out.Add(ParseStmt($"{temp} = {rightValue};"));
        var conditional = BlockAndPop();
        _out.Add(SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, Identifier(temp),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            conditional));
        return Identifier(temp);
    }

    private ExpressionSyntax SpliceConditional(ConditionalExpressionSyntax cond)
    {
        var condition = Splice(cond.Condition);
        var typeName = TypeNameOf(cond);
        var temp = _owner.NextTemp("__cond");
        var defaultValue = typeName is { Length: > 0 } tn && IsReferenceLike(tn) ? "default!" : "default";
        _out.Add(ParseStmt($"{typeName} {temp} = {defaultValue};"));

        _scope.Push(_out);
        _out = new List<StatementSyntax>();
        _out.Add(ParseStmt($"{temp} = {Splice(cond.WhenTrue)};"));
        var trueBlock = BlockAndPop();

        _scope.Push(_out);
        _out = new List<StatementSyntax>();
        _out.Add(ParseStmt($"{temp} = {Splice(cond.WhenFalse)};"));
        var falseBlock = BlockAndPop();

        _out.Add(SyntaxFactory.IfStatement(condition, trueBlock,
            SyntaxFactory.ElseClause(falseBlock)));
        return Identifier(temp);
    }

    private ExpressionSyntax SpliceChildren(ExpressionSyntax expr)
    {
        // Top-most descendants that contain liftables get recursively spliced
        // (bottom-up ReplaceNodes with a top-most-only filter — conditional
        // positions inside them are handled by their own Splice dispatch).
        ExpressionSyntax? found = null;
        foreach (var desc in expr.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (desc is InvocationExpressionSyntax or BinaryExpressionSyntax or ConditionalExpressionSyntax
                && ContainsLiftable(desc))
            {
                if (found == null || IsDescendant(found, desc)) found = desc;
            }
        }
        if (found == null) return expr;
        var replaced = expr.ReplaceNode(found, Splice(found));
        return Splice(replaced);
    }

    private static bool IsDescendant(ExpressionSyntax candidate, ExpressionSyntax other)
        => other.DescendantNodes().Contains(candidate);

    // ── invocation lifting ──────────────────────────────────────────────────

    private ExpressionSyntax LiftInvocation(InvocationExpressionSyntax invocation)
    {
        var method = GetSymbol(invocation)!;
        var leading = new List<StatementSyntax>();
        var readbacks = new List<StatementSyntax>();
        var newArgs = new List<ArgumentSyntax>();
        bool anyLifted = false;

        for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var arg = invocation.ArgumentList.Arguments[i];
            if (arg.RefKindKeyword.IsKind(SyntaxKind.InKeyword))
            {
                // CD8: kept-in methods keep the keyword; stripped methods drop it.
                if (_owner.IsInKept(method, i)) { newArgs.Add(arg); continue; }
                newArgs.Add(SyntaxFactory.Argument(_owner.InjectSlotValues(arg.Expression)));
                continue;
            }
            if (!arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                && !arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                newArgs.Add(SyntaxFactory.Argument(Splice(arg.Expression)));
                continue;
            }

            bool isOut = arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword);
            var (proxyDecl, readback, newArg) = LiftArgument(method, i, arg, isOut);
            if (proxyDecl != null) leading.Add(proxyDecl);
            if (readback != null) readbacks.Add(readback);
            newArgs.Add(newArg);
            anyLifted = true;
        }

        if (!anyLifted)
            return invocation;

        _stats_CallSiteLifted();
        var newInvocation = invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));

        // leading (declaration order), then the call, then readbacks (reverse order)
        _out.AddRange(leading);
        var returnType = method.ReturnsVoid ? null : _owner.TypeDisplay(method.ReturnType);
        if (returnType == null)
        {
            _out.Add(SyntaxFactory.ExpressionStatement(newInvocation));
        }
        else
        {
            var temp = _owner.NextTemp("__call");
            _out.Add(ParseStmt($"var {temp} = {newInvocation};"));
            readbacks.Reverse();
            _out.AddRange(readbacks);
            return Identifier(temp);
        }
        readbacks.Reverse();
        _out.AddRange(readbacks);
        return newInvocation;
    }

    private (StatementSyntax? decl, StatementSyntax? readback, ArgumentSyntax arg) LiftArgument(
        IMethodSymbol method, int index, ArgumentSyntax arg, bool isOut)
    {
        var expr = arg.Expression;
        while (expr is ParenthesizedExpressionSyntax p) expr = p.Expression;
        var paramType = _owner.TypeDisplay(method.Parameters[index].Type);

        // slot direct pass (internal callee now expects ByRef<T>)
        if (expr is IdentifierNameSyntax slotId && _owner.IsHolderName(slotId.Identifier.Text))
        {
            var id = SyntaxFactory.IdentifierName(slotId.Identifier.Text)
                .WithAdditionalAnnotations(DirectPassAnnotation);
            return (null, null, SyntaxFactory.Argument(id));
        }

        // discard: out _ → bare slot, no readback, no declaration
        if (expr is IdentifierNameSyntax { Identifier.ValueText: "_" })
        {
            var decl = SlotDecl(paramType, outVar: null, out var slotName);
            return (decl, null, SyntaxFactory.Argument(Identifier(slotName)));
        }

        // declaration expression: out var x / out T x
        if (expr is DeclarationExpressionSyntax declExpr)
        {
            var designation = declExpr.Designation;
            if (designation is SingleVariableDesignationSyntax svd)
            {
                var varName = svd.Identifier.Text;
                var declStmt = ParseStmt($"{paramType} {varName} = DefaultFor(paramType);");
                var decl = SlotDecl(paramType, varName, out var slotName);
                var readback = ParseStmt($"{varName} = {slotName}.Value;");
                return (decl, readback, SyntaxFactory.Argument(Identifier(slotName)));
            }
            Report3303(arg, "unsupported out designation");
            return (null, null, arg);
        }

        // element access: rank-1 array → array factory; otherwise copy-in/out
        if (expr is ElementAccessExpressionSyntax elem)
        {
            var arrayType = _owner.GetType(elem.Expression) as IArrayTypeSymbol;
            if (arrayType == null)
            {
                var (d, r, a) = CopyInOut(paramType, elem, isOut);
                return (d, r, a);
            }
            if (arrayType.Rank != 1)
            {
                Report3303(arg, $"multi-dimensional array element ref (rank {arrayType.Rank})");
                return (null, null, arg);
            }
            var innerType = _owner.TypeDisplay(arrayType.ElementType);
            var indices = string.Join(", ", elem.ArgumentList.Arguments.Select(a => a.Expression));
            var slotDecl = ParseStmt(
                $"var {{slot}} = Cs2jByRef.ByRef<{innerType}>.Create({elem.Expression}, {indices});");
            ...
        }
        ...
    }
}
```

> **注：** 上面最后一段是占位缩写——**落地时必须写成完整方法**，形状按以下完整规则逐 case 实现（这是唯一允许"边写边补全"的方法，其余代码必须逐字照抄）。完整 case 表：

| 实参形状 | ref | out |
|---|---|---|
| IdentifierName（槽名） | 直传（注解豁免），去关键字 | 同左 |
| IdentifierName `_` | —（非法 C#） | `var __outN = ByRef<T>.Create();` 无读回 |
| IdentifierName（普通） | `var __refN = ByRef<T>.Create(() => e, v => e = v);` 无读回 | `var __outN = ByRef<T>.Create();` + 读回 `e = __outN.Value;` |
| ElementAccess（IArrayTypeSymbol Rank==1） | `var __refN = ByRef<TElem>.Create(arr, i);` 无读回 | 同左 + 读回 `arr[i] = __outN.Value;` |
| ElementAccess（其它/Span 索引器） | 拷入拷出：`var __vN = e; var __refN = ByRef<T>.Create(__vN);` + 读回 `e = __refN.Value;` | 同左（槽无 seed `Create()`） |
| MemberAccessExpression | 访问器 `Create(() => e, v => e = v)`；若 e 内任一标识符类型为 ref struct → 拷入拷出 | 槽 + 读回 `e = __outN.Value;`（无需访问器，无捕获问题） |
| DeclarationExpression（out var x / out T x） | — | 前置 `{paramType} x = default(_!);` + 槽 + 读回 `x = __outN.Value;` |
| 其它 | CS2J3303 诊断，实参原样保留（后续构建失败 + 可读消息） | 同左 |

`T` 一律取 `method.Parameters[index].Type` 的 display（泛型 T 原样发射）；数组工厂元素类型取 `IArrayTypeSymbol.ElementType`。

- [ ] **Step 2: 改造 `RefOutInSyntaxRewriter.cs`——作用域栈、注入器、集成提升器**

对现有文件做以下修改（保持既有方法语义）：

1. 把 `private readonly HashSet<string> _currentMethodHolderNames = new();` 换成栈：

```csharp
    private readonly Stack<HashSet<string>> _holderNameScopes = new();
    private HashSet<string> CurrentHolderNames => _holderNameScopes.Peek();
    private bool IsHolderName(string name) =>
        _holderNameScopes.Any(s => s.Contains(name));
```

`VisitMethodDeclaration`/`VisitConstructorDeclaration`/`VisitDelegateDeclaration` 的 `_currentMethodHolderNames.Clear();` 换成 `_holderNameScopes.Push(new HashSet<string>());`，方法末尾（return 前）`_holderNameScopes.Pop();`（用 try/finally 保证异常安全）。体内所有 `_currentMethodHolderNames.Contains(...)` 改走 `IsHolderName(...)`。

2. 新增公共辅助（供提升器复用）：

```csharp
    private readonly RefOutInStatementLifter _lifter;
    // ctor 里：_lifter = new RefOutInStatementLifter(this);

    internal int NextTemp(string prefix) => _tempCounter++; // 片段内自行拼 $"{prefix}{n}"

    internal ITypeSymbol? GetType(ExpressionSyntax e) =>
        _semanticModel.GetTypeInfo(e).Type ?? _semanticModel.GetTypeInfo(e).ConvertedType;

    internal string TypeDisplay(ITypeSymbol t) => t.ToDisplayString();

    internal bool IsHolderName(string name);            // 作用域栈查询
    internal bool IsInKept(IMethodSymbol method, int index); // C9 碰撞表（Task 5 前恒 false）
    internal ExpressionSyntax InjectSlotValues(ExpressionSyntax expr); // 见下
    internal IMethodSymbol? ResolveInvocation(InvocationExpressionSyntax inv);
    internal void Report(CS2J code, string message, Location? loc);
```

3. `VisitIdentifierName` 调整（C6 + 直传注解豁免）：

```csharp
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (node.HasAnnotations(RefOutInStatementLifter.DirectPassAnnotation))
            return base.VisitIdentifierName(node);
        var name = node.Identifier.Text;
        if (IsInDeclarationContext(node) || !IsHolderName(name))
            return base.VisitIdentifierName(node);
        _stats.HoldersInjected++;
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, node,
            SyntaxFactory.IdentifierName("Value"));
    }
```

（删除 `IsHolderSlotDirectPass`/`IsExternalRefOutArgument` 两个方法——直传由注解判定；外部 ref/out 槽实参落入默认注入即得到 C6 要求的 `Ext(ref a.Value)`。）

4. 语句访问接入提升器（fragment 不重访、原生子树经 base.Visit 下行）：

```csharp
    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var lifted = _lifter.SpliceExpression(node.Expression, out var hoisted);
        if (hoisted.Count == 0) return base.VisitExpressionStatement(node);
        var newStmt = SyntaxFactory.ExpressionStatement(lifted);
        return SyntaxFactory.Block(hoisted.Append(newStmt));
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression == null) return base.VisitReturnStatement(node);
        var lifted = _lifter.SpliceExpression(node.Expression, out var hoisted);
        if (hoisted.Count == 0) return base.VisitReturnStatement(node);
        return SyntaxFactory.Block(hoisted.Append(node.WithExpression(lifted)));
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var cond = _lifter.SpliceExpression(node.Condition, out var hoisted);
        if (hoisted.Count == 0) return base.VisitIfStatement(node);
        var visitedThen = (StatementSyntax)base.Visit(node.Statement)!;
        var elseClause = node.Else != null
            ? SyntaxFactory.ElseClause((StatementSyntax)base.Visit(node.Else.Statement)!)
            : null;
        var newIf = SyntaxFactory.IfStatement(cond, visitedThen, elseClause);
        return SyntaxFactory.Block(hoisted.Append(newIf));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var cond = _lifter.SpliceExpression(node.Condition, out var hoisted);
        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;
        if (hoisted.Count == 0)
            return cond == node.Condition ? base.VisitWhileStatement(node)
                : SyntaxFactory.WhileStatement(cond, visitedBody);
        // while(C)B → while(true){ hoisted; if(!C) break; B; }（continue 语义不变）
        return SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(hoisted.Cast<StatementSyntax>()
                .Append(SyntaxFactory.IfStatement(
                    SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                        SyntaxFactory.ParenthesizedExpression(cond)),
                    SyntaxFactory.BreakStatement()))
                .Append(visitedBody)));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        // for(I;C;U)B → I; while(true){ hoisted; if(!C) break; B; U; }
        var initializers = node.Initializers;
        var anyInitLift = false;
        var initStmts = new List<StatementSyntax>();
        if (node.Declaration != null)
            initStmts.Add((LocalDeclarationStatementSyntax)SpliceLocalDeclaration(node.Declaration, ref anyInitLift));
        foreach (var init in initializers)
        {
            var lifted = _lifter.SpliceExpression(init, out var h);
            if (h.Count > 0) anyInitLift = true;
            initStmts.AddRange(h);
            initStmts.Add(SyntaxFactory.ExpressionStatement(lifted));
        }
        ExpressionSyntax? cond = node.Condition;
        var condHoisted = new List<StatementSyntax>();
        if (cond != null) cond = _lifter.SpliceExpression(cond, out condHoisted);
        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;
        var visitedIncrementors = node.Incrementors
            .Select(i => (ExpressionSyntax)base.Visit(i)!).ToSeparatedList();

        if (!anyInitLift && condHoisted.Count == 0 && cond == node.Condition)
            return base.VisitForStatement(node);

        var stmts = new List<StatementSyntax>();
        stmts.AddRange(initStmts);
        var loopBody = new List<StatementSyntax>();
        loopBody.AddRange(condHoisted);
        if (cond != null)
            loopBody.Add(SyntaxFactory.IfStatement(
                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                    SyntaxFactory.ParenthesizedExpression(cond)),
                SyntaxFactory.BreakStatement()));
        loopBody.Add(visitedBody);
        loopBody.Add(SyntaxFactory.ExpressionStatement(
            SyntaxFactory.SeparatedList(visitedIncrementors).Count > 0
                ? SyntaxFactory.SequenceExpression(visitedIncrementors)
                : SyntaxFactory.ParseExpression(";"))); // 空 incrementors 时省略该语句
        stmts.Add(SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(loopBody)));
        return SyntaxFactory.Block(stmts);
    }
```

> **注（同上）：** `VisitForStatement` 的空 incrementors 分支、`SpliceLocalDeclaration`、`ToSeparatedList` 等辅助在落地时以最小代码补全（`SequenceExpression` 仅在 Incrementors.Count>0 时使用）。do-while 条件含可提升调用 → `Report(CS2J3303, ...)` 并 `return base.VisitDoStatement(node)`（continue 语义无解，spec C1 允许受限拒绝）；foreach 集合表达式、lock/switch/using 表达式同走 `SpliceExpression`（`using` 资源声明经 `VisitUsingStatement` 对 Declaration 的 initializer 提升）；`VisitArrowExpressionClause`：表达式体含提升时改块体 `{ hoisted; return expr; }`。

- [ ] **Step 3: 单测（新文件段落，加入 `RefOutInRewriterUnitTests.cs`）**

复用现有 `ParseWithSemantics` harness，新增：

```csharp
    [Fact]
    public void OutVar_InIfCondition_HoistedAndReadBack()          // C2
    {
        var src = @"
public class C
{
    public bool TryGet(out int r) { r = 42; return true; }
    public int Use()
    {
        if (TryGet(out var v)) { return v; }
        return 0;
    }
}";
        var result = RewriteAndAssertCompiles(src);   // harness 见 Task 3 Step 4
        Assert.Contains("ByRef<int> __out", result);
        Assert.Contains("v = __out", result);
        Assert.Contains("if (__call", result);
    }

    [Fact]
    public void RefCall_InAssignmentRhs_Hoisted()                  // C1
    {
        var src = @"
public class C
{
    public void Bump(ref int v) { v++; }
    public int Use()
    {
        int x = 1;
        int y = 0;
        y = 5 + Bump2(x);  // Bump2(ref x) returns int
        return x + y;
    }
    public int Bump2(ref int v) { v++; return v; }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("ByRef<int>.Create(() => x, v => x = v)", result);
    }

    [Fact]
    public void MemberChain_RefArg_AccessorProxy()                 // C3
    {
        var src = @"
public class H { public int Value; public void Update(ref int v) { v++; } }
public class C
{
    public void Use()
    {
        var h = new H();
        h.Value = 1;
        h.Update(ref h.Value);
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("Create(() => h.Value, v => h.Value = v)", result);
    }

    [Fact]
    public void ShortCircuit_RightSideNotUnconditionallyExecuted() // C1 短路
    {
        var src = @"
public class C
{
    public bool Flag = false;
    public bool TryGet(out int r) { r = 1; return true; }
    public int Use()
    {
        if (Flag && TryGet(out var v)) { return v; }
        return 0;
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("if (Flag)", result);        // 右侧被包进条件块
    }
```

- [ ] **Step 4: 测试 harness 升级——`RewriteAndAssertCompiles`（真实编译断言，全部后续测试复用）**

```csharp
    private static string RewriteAndAssertCompiles(string src, bool expectChanged = true)
    {
        var byRefSrc = File.ReadAllText(
            FindRepoRoot("src/Cs2jRefOutIn/ByRef.cs"));
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.Equal(expectChanged, result.Changed);

        var outTree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var supportTree = CSharpSyntaxTree.ParseText(byRefSrc);
        var compilation = CSharpCompilation.Create("refout-verify",
            new[] { outTree, supportTree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(diagnostics.Count == 0,
            "output must compile: " + string.Join("; ", diagnostics.Select(d => d.ToString())));
        return result.OutputCode;
    }

    internal static string FindRepoRoot(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, relative);
    }
```

- [ ] **Step 5: 运行测试**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Release --nologo --filter "FullyQualifiedName~RefOutInRewriterUnitTests"`
Expected: 原有 16 用例 + 新增用例全绿（旧用例若因 `.Value` 注入语义微调而失败，逐条核对产物后修正断言——迁移而非回退）。

- [ ] **Step 6: Commit**

```bash
git add -A src/CSharpToJava.Core tests/CSharpToJava.ProjectTests
git commit -m "feat(c01): statement-level ref/out lifting engine (C1/C2/C3) with short-circuit preservation"
```

---

### Task 4: C4——ref 返回、ref 属性、ref 索引器

**Files:**
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs`

- [ ] **Step 1: `VisitReturnStatement` 增加 ref 返回改写（在 Task 3 的方法里加分支）**

```csharp
    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression is RefExpressionSyntax refExpr)
        {
            // return ref e → return <proxy(e)>（按位置种类选工厂）
            _stats.RefReturnsRewritten++;
            UsedHolder = true;
            var inner = refExpr.Expression;
            var (decl, proxy) = BuildReturnProxy(inner);
            if (decl != null)
                return SyntaxFactory.Block(decl, node.WithExpression(proxy));
            return node.WithExpression(proxy);
        }
        // ...（Task 3 的普通 return 提升分支不变）
    }

    private (StatementSyntax? decl, ExpressionSyntax proxy) BuildReturnProxy(ExpressionSyntax inner)
    {
        // 数组元素 → ByRef<T>.Create(arr, i)；其余 → 访问器闭包
        if (inner is ElementAccessExpressionSyntax elem)
        {
            var (arr, indices) = DecomposeElementAccess(elem);
            var t = _semanticModel.GetTypeInfo(elem).Type is IArrayTypeSymbol at
                ? TypeDisplay(at.ElementType) : "int";
            return (null, ParseExpr($"Cs2jByRef.ByRef<{t}>.Create({arr}, {indices})"));
        }
        var name = inner.ToString();
        var type = TypeDisplay(_semanticModel.GetTypeInfo(inner).Type
            ?? _semanticModel.GetTypeInfo(inner).ConvertedType!);
        return (null, ParseExpr($"Cs2jByRef.ByRef<{type}>.Create(() => {name}, v => {name} = v)"));
    }
```

> 方法返回类型的 ref 剥离已在 L92-98 存在；ref 局部接收 `ref int s = ref calc.GetRef(...)` → 提升后 `ByRef<int> s = calc.GetRef(...)`（GetRef 返回 ByRef<int>），`s.Value` 注入由 VisitIdentifierName 完成——ref 局部初始化分支（VisitLocalDeclarationStatement 已有）在 initializer 为 InvocationExpression 时**原样保留表达式**（返回值已是 ByRef），只对 `ref e` 形式构造代理。此点必须实现：`VisitLocalDeclarationStatement` 中当 initializer 是 `RefExpressionSyntax` 包着**方法调用**（非数组/字段）时，若调用目标是 ref 返回方法 → 直接去 `ref` 关键字保留调用。

- [ ] **Step 2: ref 属性 / ref 索引器声明改写**

新增两个 Visit（放 RefOutInSyntaxRewriter）：

```csharp
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null || (symbol.RefKind != RefKind.Ref && symbol.RefKind != RefKind.RefReadOnly))
            return base.VisitPropertyDeclaration(node);
        // ref T P { get { return ref e; } } → ByRef<T> P { get { return <proxy>; } }
        // 返回类型去 ref（RefTypeSyntax→Type）；体由 VisitReturnStatement 分支改写
        UsedHolder = true;
        return base.VisitPropertyDeclaration(node.WithType(
            node.Type is RefTypeSyntax rt ? rt.Type : node.Type));
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node) => 同上模式;
```

外部使用点（`M(ref calc.P)`）由 Task 3 提升器天然覆盖：实参 `calc.P` 是 MemberAccess → 访问器闭包 `Create(() => calc.P.Value, v => calc.P.Value = v)`？——**否**：提升器对 ref-返回属性使用点的正确形态是实参 `calc.P`（类型已是 `ByRef<T>`）直接传（无需包装）。实现：`LiftArgument` 的 MemberAccess 分支之前加判定——若 `_semanticModel.GetSymbolInfo(expr).Symbol is IPropertySymbol p && p.RefKind is Ref or RefReadOnly` → 直接传 `Argument(expr)`（属性返回的已是 ByRef 引用）。

- [ ] **Step 3: 测试**

```csharp
    [Fact]
    public void RefReturn_ArrayElement_BecomesByRefFactory()       // C4
    {
        var src = @"
public class C
{
    public ref int GetRef(int[] arr, int index) { return ref arr[index]; }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("public int GetRef(int[] arr, int index)", result);
        Assert.Contains("return Cs2jByRef.ByRef<int>.Create(arr, index);", result);
    }

    [Fact]
    public void RefProperty_DeclarationAndBodyRewritten()          // C4
    {
        var src = @"
public class C
{
    private int _f;
    public ref int P => throw new System.NotSupportedException();
}";
        // 用块体版本测（表达式体 ref 属性走 VisitArrowExpressionClause 分支）
        var src2 = @"
public class C
{
    private int _f;
    public ref int P
    {
        get { return ref _f; }
    }
}";
        var result = RewriteAndAssertCompiles(src2);
        Assert.Contains("public Cs2jByRef.ByRef<int> P", result);
        Assert.Contains("Create(() => _f", result);
    }
```

- [ ] **Step 4: 运行 + Commit**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Release --nologo --filter "FullyQualifiedName~RefOutInRewriterUnitTests"` → 全绿。

```bash
git add -A && git commit -m "feat(c01): ref returns/properties/indexers rewritten to ByRef proxies (C4)"
```

---

### Task 5: C8/C9——using 注入放宽 + in 碰撞保留

**Files:**
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInRewriter.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs`

- [ ] **Step 1: C8——`UsedHolder` 在提升路径置位**

`RefOutInStatementLifter.LiftInvocation` 在 `anyLifted` 时调用 `_owner.MarkHolderUsed();`（rewriter 暴露 `internal void MarkHolderUsed() => UsedHolder = true;`）。纯调用点文件即获得 `using Cs2jByRef;`。

- [ ] **Step 2: C9——碰撞判定共享 helper（新建于 AliasAnnotator 文件内）**

```csharp
    /// <summary>CD8：去 in 后若与同类型同名方法的重载在「仅 in 修饰差异」上碰撞则保留 in。</summary>
    internal static bool IsInOverloadCollision(IMethodSymbol method, ITypeSymbol containingType)
    {
        if (containingType == null) return false;
        foreach (var other in containingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(other, method)) continue;
            if (other.Parameters.Length != method.Parameters.Length) continue;
            bool differsOnlyByIn = false;
            bool same = true;
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var a = method.Parameters[i]; var b = other.Parameters[i];
                if (!SymbolEqualityComparer.Default.Equals(a.Type, b.Type)) { same = false; break; }
                if ((a.RefKind == RefKind.In) != (b.RefKind == RefKind.In)) differsOnlyByIn = true;
            }
            if (same && differsOnlyByIn) return true;
        }
        return false;
    }
```

`ClassifyMethod` 中对含 `in` 参数的 internal 方法：碰撞 → **不**把该参数计入 strip 名单，改记 `_stats.InOverloadKept++` 并发 CS2J3302 Warning 诊断。rewriter 的 `StripModifiers` 路径相应改为「annotator 标记为 kept 的参数不动」（在 `RewriteParameter`/`StripModifiers` 判定处查 `_annotator.IsInKept(methodSymbol, index)`——annotator 增加 `IReadOnlySet<IParameterSymbol> InKeptParameters`）。

- [ ] **Step 3: 调用点 in 关键字**——Task 3 的 `IsInKept` 接通 `_annotator.InKeptParameters.Contains(paramSymbol)`。

- [ ] **Step 4: 测试**

```csharp
    [Fact]
    public void InOverloadCollision_KeepsIn_AndWarns()             // C9/CD8
    {
        var src = @"
public class C
{
    public void M(int a) { }
    public void M(in int a) { }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("in int a", result.OutputCode);
        Assert.Equal(1, result.Statistics.InOverloadKept);
        Assert.Contains(result.Diagnostics, d => d.Code == "CS2J3302");
    }

    [Fact]
    public void PureCallSiteFile_GetsUsingInjection()              // C8
    {
        var src = @"
public class Caller
{
    public void Use()
    {
        int x = 1;
        Other.Bump(ref x);
    }
}
public class Other { public static void Bump(ref int v) { v++; } }";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("using Cs2jByRef;", result);
    }
```

- [ ] **Step 5: 运行 + Commit**（同前模式）`git commit -m "feat(c01): using injection on call-site files (C8) + in overload collision keep (C9/CD8)"`

---

### Task 6: C7——RefStructByRef 形参、调用点与 ref 局部重绑定（S1/S2/S3）

**Files:**
- Create: `src/Cs2jRefOutIn/RefStructByRef.cs`
- Delete: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInMangling.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs`
- Modify: `src/CSharpToJava.CLI/RefOutInSupportFiles.cs`
- Modify: `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`（嵌入资源清单加 `Cs2jRefOutIn.RefStructByRef.cs`）
- Modify: `src/CSharpToJava.Core/CSharpToJava.Core.csproj`（同上，如单文件模式需要）
- Modify: `src/CSharpToJava.CLI/Program.cs`（单文件模式 `_Cs2jRefOutIn.cs` 合并写入两源）

- [ ] **Step 1: 写 `src/Cs2jRefOutIn/RefStructByRef.cs`（嵌入资源，LogicalName `Cs2jRefOutIn.RefStructByRef.cs`）**

```csharp
using System;

namespace Cs2jByRef;

/// <summary>
/// 可变位置槽，承接 C# ref struct 形参（Span/ReadOnlySpan 等）。
/// 仅槽模式：ref struct 不能作为数组元素（CS0611），数组模式是死代码（spec Q2-A）。
/// Java 侧映射为 io.github.ningpp.compat.RefStructByRef（普通类）。
/// </summary>
public ref struct RefStructByRef<T>
{
    private T _slot;

    public RefStructByRef() { _slot = default!; }
    private RefStructByRef(T initial) { _slot = initial; }

    public static RefStructByRef<T> Create() => new();
    public static RefStructByRef<T> Create(T initial) => new(initial);

    public T Value
    {
        get => _slot;
        set => _slot = value;
    }
}
```

CLI csproj 嵌入资源项（与现有 `Cs2jRefOutIn.ByRef.cs` 并列）：

```xml
<EmbeddedResource Include="..\Cs2jRefOutIn\RefStructByRef.cs" LogicalName="Cs2jRefOutIn.RefStructByRef.cs" />
```

- [ ] **Step 2: `RefOutInSupportFiles.ReleaseAsync` 改为释放两资源、删除 `GenerateRefStructHolder` 与按类型循环**

```csharp
    public static async Task ReleaseAsync(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(Path.Combine(destinationDirectory, "ByRef.cs"),
            GetByRefFileContent(), new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(destinationDirectory, "RefStructByRef.cs"),
            GetRefStructByRefFileContent(), new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(destinationDirectory, "Cs2jRefOutIn.csproj"),
            GetProjectFileContent(), new System.Text.UTF8Encoding(false));
    }
```

`RefOutInPassAdapter` 调用点同步（`_requiredRefStructTypes` 字段与 `RequiredRefStructTypes` 属性删除；`RequiredRefStructTypes` 相关的 `RefOutInRewriterResult` 字段一并移除）。单文件模式 `Program.cs` L2602-2604 合并写入：`_Cs2jRefOutIn.cs` = ByRef.cs + "\n" + RefStructByRef.cs。

- [ ] **Step 3: 重写器 ref struct 形参（C7）**

`RewriteParameter` 的 `type.IsRefLikeType` 分支：

```csharp
        if (type.IsRefLikeType)
        {
            var elemDisplay = TypeDisplay(type);
            newType = SyntaxFactory.ParseTypeName($"Cs2jByRef.RefStructByRef<{elemDisplay}>");
        }
```

`RefOutInAliasAnnotator.ClassifyMethod` 删除 `_requiredRefStructTypes`/`RefOutInMangling` 依赖（ref struct ref/out 参数照常进 HolderSymbols，`RefStructParamsRewritten` 计数保留）。

调用点（`LiftArgument` 前加分支）：实参表达式类型 `IsRefLikeType` 且目标方法 internal → 槽模式：

```csharp
        // ref struct 实参 → RefStructByRef<T> 槽（拷入 + 拷出；ref struct 不能被闭包捕获）
        var argTypeInfo = _owner.GetType(arg.Expression);
        if (argTypeInfo != null && argTypeInfo.IsRefLikeType)
        {
            var t = _owner.TypeDisplay(argTypeInfo);
            var slot = _owner.NextTemp(isOut ? "__out" : "__ref");
            var seed = isOut ? "" : $"({arg.Expression})";
            var decl = ParseStmt($"var {slot} = Cs2jByRef.RefStructByRef<{t}>.Create{...};");
            var readback = ParseStmt($"{arg.Expression} = {slot}.Value;");
            return (decl, readback, SyntaxFactory.Argument(Identifier(slot)));
        }
```

- [ ] **Step 4: ref 局部重绑定**——`VisitAssignmentExpression`（新增）：若 LHS 是 ref 局部名（`IsHolderName`）且 RHS 是 `RefExpressionSyntax` → 改普通赋值 `LHS = <proxy(rhs.Expression)>`（复用 Task 4 的 `BuildReturnProxy`）。

- [ ] **Step 5: 测试**

```csharp
    [Fact]
    public void RefStructParam_UsesRefStructByRefSlot()            // C7/Q2-A
    {
        var src = @"
using System;
public class C
{
    public void M(ref Span<int> s) { s = s.Slice(1); }
    public void Use()
    {
        Span<int> span = new int[] { 1, 2, 3 };
        M(ref span);
    }
}";
        var result = RewriteAndAssertCompiles(src, allowUnsafe: true);
        Assert.Contains("Cs2jByRef.RefStructByRef<System.Span<int>> s", result);
        Assert.Contains(".Create(span)", result);
        Assert.Contains("span = __ref", result);   // 拷出读回
    }
```

（`RewriteAndAssertCompiles` 增加可选 `bool allowUnsafe = false` 与 `System` 引用已有。）

- [ ] **Step 6: 运行 + Commit** `git commit -m "feat(c01): generic RefStructByRef<T> slot holder replaces per-type mangling (C7, Q2-A, S1-S3)"`

---

### Task 7: C5——lambda / 局部函数 ref-out 形参

**Files:**
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs`

- [ ] **Step 1: Annotate 增扫 lambda/局部函数**

`RefOutInAliasAnnotator.Annotate()` 末尾追加：

```csharp
        // Lambda/anonymous method params classified against their converted delegate type
        foreach (var lambda in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            if (_semanticModel.GetTypeInfo(lambda).ConvertedType is not INamedTypeSymbol del
                || del.DelegateInvokeMethod == null) continue;
            var invoke = del.DelegateInvokeMethod;
            if (GetSymbolClass(del) != SymbolClass.Internal) continue;
            var lambdaParams = lambda switch
            {
                SimpleLambdaExpressionSyntax s => new[] { s.Parameter },
                ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.ToArray(),
                AnonymousMethodExpressionSyntax am => am.ParameterList?.Parameters.ToArray() ?? Array.Empty<ParameterSyntax>(),
                _ => Array.Empty<ParameterSyntax>(),
            };
            for (int i = 0; i < lambdaParams.Length && i < invoke.Parameters.Length; i++)
            {
                var p = invoke.Parameters[i];
                if (p.RefKind is RefKind.Ref or RefKind.Out)
                {
                    var sym = _semanticModel.GetDeclaredSymbol(lambdaParams[i]);
                    if (sym != null) _holderSymbols.Add(sym);
                }
            }
        }

        foreach (var lf in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            var sym = _semanticModel.GetDeclaredSymbol(lf);
            if (sym != null) ClassifyMethod(sym);
        }
```

- [ ] **Step 2: 重写器新增三个 Visit（参数改写复用 `RewriteParameter`，但 lambda param 的符号经 `GetDeclaredSymbol(param)` 取）**

```csharp
    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var sym = _semanticModel.GetDeclaredSymbol(node.Parameter);
        bool holder = sym != null && Annotator.HolderSymbols.Contains(sym);
        _holderNameScopes.Push(new HashSet<string>());
        try
        {
            ParameterSyntax newParam = node.Parameter;
            if (holder) { CurrentHolderNames.Add(node.Parameter.Identifier.Text); newParam = RewriteParameter(node.Parameter, sym!); }
            var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(
                node.WithParameter(newParam))!;
            return visited;
        }
        finally { _holderNameScopes.Pop(); }
    }
    // VisitParenthesizedLambdaExpression / VisitAnonymousMethodExpression /
    // VisitLocalFunctionStatement：同一模式（局部函数直接复用 VisitMethodDeclaration 的参数循环逻辑）
```

- [ ] **Step 3: 测试**

```csharp
    [Fact]
    public void Lambda_RefParam_RewrittenForInternalDelegate()     // C5
    {
        var src = @"
public class C
{
    public delegate void Bump(ref int v);
    public void Use()
    {
        Bump b = (ref int x) => { x = x + 1; };
        int n = 0;
        b(ref n);
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("ByRef<int> x", result);
        Assert.Contains("x.Value = x.Value + 1", result);
        Assert.Contains("b(__ref", result);          // 调用点直传槽
    }
```

- [ ] **Step 4: 运行 + Commit** `git commit -m "feat(c01): lambda/local-function ref-out params rewritten (C5)"`

---

### Task 8: C11/C12/C13——无解边界 fail-fast（CS2J3303/3304）与 ref 字段拒绝

**Files:**
- Create: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInDiagnosticCodes.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInRewriterStatistics.cs`

- [ ] **Step 1: 诊断码常量**

```csharp
namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>CS2J33xx（ABI/holder 段）诊断码——spec §6。ROI 旧码并行保留一个版本。</summary>
public static class RefOutInDiagnosticCodes
{
    public const string LockedMember = "CS2J3301";         // Info（并行 ROI001）
    public const string InOverloadCollision = "CS2J3302";  // Warning（CD8）
    public const string UnproxyablePosition = "CS2J3303";  // Error
    public const string RefStructSlotForwarded = "CS2J3304"; // Error
    public const string InternalResidue = "CS2J3305";      // Error（后置自检）
    public const string ReparseFailed = "CS2J3310";        // Error（并行 ROI099）
    public const string RestoreFailed = "CS2J3311";        // Error（并行 ROI100）
    public const string BuildFailed = "CS2J3312";          // Error（并行 ROI101）
    public const string TestsFailed = "CS2J3313";          // Error（并行 ROI102）
    public const string UnmappedByRefForm = "CS2J3320";    // Error（Java 侧）
}
```

- [ ] **Step 2: C12——ref 字段改拒绝（Q5 定案：sdk 0 命中）**

`VisitFieldDeclaration` 的 RefType 分支替换为：

```csharp
        if (node.Declaration.Type is RefTypeSyntax)
        {
            _diagnostics.Add(new RefOutInRewriterDiagnostic(
                RefOutInDiagnosticCodes.UnproxyablePosition,
                "ref fields are not supported by the ref/out/in preprocessor (no known usage); " +
                "declare the field as a normal field or wrap it explicitly.",
                RefOutInSeverity.Error, node.GetLocation()));
            return base.VisitFieldDeclaration(node);   // 保留原样 → 构建失败 + 可读诊断
        }
```

`RefOutInRewriterStatistics`：`RefFieldsRewritten` 改名/加 `RefFieldsRejected`（annotator 计数同步）。

- [ ] **Step 3: C11/C13 触发点**（Task 3 已埋 `Report3303`；补 CS2J3304）：`LiftArgument` 直传判定中，若目标是**外部**方法且实参标识符是 ref struct 槽（`argTypeInfo.IsRefLikeType`）→ CS2J3304。

- [ ] **Step 4: 测试**

```csharp
    [Fact]
    public void RefField_ExplicitError_CS2J3303()                  // C12/Q5
    {
        var src = @"public class C { ref int _f; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.Contains(result.Diagnostics, d => d.Code == "CS2J3303" && d.Severity == RefOutInSeverity.Error);
    }

    [Fact]
    public void MultiDimArrayRef_ExplicitError_CS2J3303()          // C13/Q6
    {
        var src = @"
public class C
{
    public void M(ref int v) { }
    public void Use() { int[,] grid = new int[2,2]; M(ref grid[0,1]); }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.Contains(result.Diagnostics, d => d.Code == "CS2J3303");
    }
```

- [ ] **Step 5: 运行 + Commit** `git commit -m "feat(c01): fail-fast unsolvable positions (CS2J3303/3304), ref fields rejected (C11-C13, Q5/Q6)"`

---

### Task 9: C10/S5——后置自检升级 + 适配器接线 + 并行诊断码

**Files:**
- Rewrite: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInPostVerifier.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs`（`IsLocked` → `internal static`）
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/RefOutInPassAdapter.cs`
- Modify: `src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInRewriter.cs`（ROI099 并行 CS2J3310）
- Modify: `tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs`（旧 verifier 单测适配新签名）

- [ ] **Step 1: 重写 `RefOutInPostVerifier.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Post-rewrite residual gate (C10, D3 §7): asserts the rewritten output has zero
/// INTERNAL ref/out declarations, ref returns, ref locals/fields or ref expressions
/// outside external/locked call arguments. Violations carry CS2J3305.
/// </summary>
public static class RefOutInPostVerifier
{
    public static IReadOnlyList<ResidualViolation> Verify(
        IEnumerable<SyntaxTree> outputTrees, CSharpCompilation compilation)
    {
        var violations = new List<ResidualViolation>();
        foreach (var tree in outputTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();

            foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(method);
                if (symbol == null) continue;
                foreach (var param in symbol.Parameters)
                {
                    if (param.RefKind is RefKind.Ref or RefKind.Out
                        && param.Locations.Any(l => l.IsInSource)
                        && !RefOutInAliasAnnotator.IsLocked(symbol))
                        violations.Add(new ResidualViolation(tree.FilePath,
                            $"internal '{symbol.ToDisplayString()}' still declares ref/out parameter '{param.Name}'"));
                }
                if (symbol.RefKind is RefKind.Ref or RefKind.RefReadOnly
                    && !RefOutInAliasAnnotator.IsLocked(symbol))
                    violations.Add(new ResidualViolation(tree.FilePath,
                        $"'{symbol.ToDisplayString()}' still declares a ref return"));
            }

            foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                         .Where(l => l.Declaration.Type is RefTypeSyntax))
                violations.Add(new ResidualViolation(tree.FilePath, "ref local survived rewriting"));

            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                         .Where(f => f.Declaration.Type is RefTypeSyntax))
                violations.Add(new ResidualViolation(tree.FilePath, "ref field survived rewriting"));

            foreach (var refExpr in root.DescendantNodes().OfType<RefExpressionSyntax>())
            {
                if (refExpr.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any()) continue;
                var arg = refExpr.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
                if (arg != null)
                {
                    var inv = arg.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    var target = inv != null
                        ? model.GetSymbolInfo(inv).Symbol as IMethodSymbol : null;
                    if (target != null && (target.Locations.All(l => !l.IsInSource)
                        || RefOutInAliasAnnotator.IsLocked(target)))
                        continue;   // 外部/锁定残留合法
                }
                violations.Add(new ResidualViolation(tree.FilePath,
                    "ref expression outside external/locked arguments survived"));
            }
        }
        return violations;
    }
}

public sealed record ResidualViolation(string FilePath, string Message);
```

旧单测（L307 引用处）改为直接调用新签名或删除（迁移而非保留假接口）。

- [ ] **Step 2: `IsLocked` 可见性** `private static bool IsLocked` → `internal static bool IsLocked`。

- [ ] **Step 3: 适配器接线（S5）**

`RefOutInPassAdapter`：
1. 新增 `private readonly Dictionary<string, string> _finalOutputs = new();`；`RewriteFileAsync` 中记录（Changed → OutputCode，否则 SourceCode）。
2. `AfterFilesAsync` 最前：若有转换文件 → 取首个 `context.SemanticModel.Compilation.References`（在 RewriteFileAsync 时缓存 `MetadataReference[]`）→ 用 `_finalOutputs` 构 `CSharpCompilation`（`allowUnsafe: true`）→ `RefOutInPostVerifier.Verify` → 每个 violation 加 `CS2J3305 Error` 诊断。
3. 并行码：`OnRestoreFailed` 同时加 `CS2J3311`；`ReportVerifyOutcome` build/test 失败同时加 `CS2J3312`/`CS2J3313`（消息同 ROI 版本）。annotator 的 ROI001 处并行加 `CS2J3301`。

- [ ] **Step 4: 测试**

```csharp
    [Fact]
    public void Verifier_FlagsInternalRefParamResidue()            // C10
    {
        var tree = CSharpSyntaxTree.ParseText("public class C { public void M(ref int a) { } }");
        var compilation = CSharpCompilation.Create("v", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var violations = RefOutInPostVerifier.Verify(new[] { tree }, compilation);
        Assert.Contains(violations, v => v.Message.Contains("still declares ref/out"));
    }

    [Fact]
    public void Verifier_AllowsExternalRefArguments()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
public class C
{
    public void M(string s, out int r) { r = 0; }
    public void Use() { M(""x"", out var v); }
}");
        var compilation = CSharpCompilation.Create("v", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var violations = RefOutInPostVerifier.Verify(new[] { tree }, compilation);
        Assert.DoesNotContain(violations, v => v.Message.Contains("ref expression"));
    }
```

- [ ] **Step 5: 运行 + Commit** `git commit -m "feat(c01): post-verify residual gate wired into production (C10/S5), CS2J33xx parallel codes"`

---

### Task 10: C14 + V2 fixture 矩阵补齐 + 项目级测试打开 verify

**Files:**
- Modify: `tests/SampleRefOutInV2Solution/*`（矩阵场景）
- Modify: `tests/CSharpToJava.ProjectTests/RefOutInPreprocessorTests.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/RefOutInPassAdapter.cs`（verbose 分类计数行，C14）

- [ ] **Step 1: fixture 补场景**——`SampleRefOutInV2Lib/Calculator.cs` 增加成员链场景（`public void AddTo(ref Point p) { ... }` 类），`Program.cs` 增加 `ref` 返回使用与 ref 局部重绑定：

```csharp
// ref return + ref local rebinding
ref int slot = ref calc.GetRef(arr, 1);
slot = 99;
slot = ref arr[2];      // rebinding
slot = 42;
```

- [ ] **Step 2: `RefOutInPreprocessorTests` 三个用例删除 `VerifyBuild = false, VerifyTests = false`（继承 descriptor 默认 true）**，断言 `result.Success` 为真。

- [ ] **Step 3: C14**——`BuildChainDiagnosticLines` verbose 分支下，为每个转换文件输出一行分类计数（`transformed/clean` 行已有，追加 `locked=N external-kept=M`，取自该 pass 统计）。

- [ ] **Step 4: 运行项目级测试（真实 verify）**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Release --nologo --filter "FullyQualifiedName~RefOutInPreprocessorTests"`
Expected: 3 用例全绿（中间树 build + test 真跑）。**这是 M1 出口标准。**

- [ ] **Step 5: Commit** `git commit -m "test(c01): V2 fixture matrix + verify enabled — M1 C# closure gate green"`

---

### Task 11: §7.1 单测矩阵补全

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/RefOutInRewriterUnitTests.cs`

- [ ] **Step 1: 按 spec §7.1 清单补用例**（每个用例都是 `RewriteAndAssertCompiles` 模式）：

do/for/foreach/using/lock 条件与资源提升；`&&/||/??/?:` 组合；多实参顺序断言（`__ref1`/`__ref2` 出现顺序）；读回逆序（两个 out 的读回行序）；嵌套调用实参；`out _`；`out int x`；静态字段成员链；ref 局部接收 ref 返回；`ref this.f`；跨文件接口（两个语法树同一 compilation，声明/实现签名一致——用 `CSharpCompilation.Create` 双树）；partial 类两段；干净文件 `Changed=false` 且无 using。统计计数逐项断言（`CallSitesLifted`/`AccessorProxies`/`ArrayElementProxies`/`InOverloadKept`/`ExternalRefArgumentsKept`）。

- [ ] **Step 2: 全量 Core 测试绿 + Commit** `git commit -m "test(c01): full §7.1 unit matrix"`

---

### Task 12: M1 出口检查（暂停点）

- [ ] **Step 1: 全仓测试** Run: `dotnet test CSharpToJavaConverter.slnx -c Release --nologo | tail -5` → 无新增失败。
- [ ] **Step 2: verb 直跑 fixture** Run: `dotnet src/CSharpToJava.CLI/bin/Release/net10.0/CSharpToJava.CLI.dll ref-out-in -s tests/SampleRefOutInV2Solution/SampleRefOutInV2.sln -d /tmp/c01-m1` → 退出 0，`ref-out-in: ... locked members` 摘要行正常。
- [ ] **Step 3: Commit（若有零星修正）+ 报告 M1 完成。**

---

### Task 13: M3——compat ByRef/RefStructByRef + JUnit

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ByRef.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/RefStructByRef.java`
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/ByRefTest.java`

- [ ] **Step 1: `ByRef.java`（spec §5.1 定稿）**

```java
package io.github.ningpp.compat;

import java.util.function.Consumer;
import java.util.function.Supplier;

/**
 * C# ByRef&lt;T&gt; position model (internal calling convention only — never across ABI).
 * Mirrors src/Cs2jRefOutIn/ByRef.cs: same factories, slot/array-element/accessor kinds.
 * Reads/writes go through getValue/setValue (converter property conventions).
 */
public abstract class ByRef<T> {
    public abstract T getValue();
    public abstract void setValue(T value);

    /** out scratch slot (no seed). */
    public static <T> ByRef<T> create() { return new SlotByRef<>(null); }
    /** ref scratch slot (seeded). */
    public static <T> ByRef<T> create(T initial) { return new SlotByRef<>(initial); }
    /** aliasing array element: reads/writes pass through array[index]. */
    public static <T> ByRef<T> create(T[] array, int index) {
        return new ArrayElementByRef<>(array, index);
    }
    /** field/member-chain alias: accessor closures. */
    public static <T> ByRef<T> create(Supplier<T> getter, Consumer<T> setter) {
        return new AccessorByRef<>(getter, setter);
    }

    private static final class SlotByRef<T> extends ByRef<T> {
        private T slot;
        SlotByRef(T v) { this.slot = v; }
        @Override public T getValue() { return slot; }
        @Override public void setValue(T v) { this.slot = v; }
    }

    private static final class ArrayElementByRef<T> extends ByRef<T> {
        private final T[] array;
        private final int index;
        ArrayElementByRef(T[] array, int index) { this.array = array; this.index = index; }
        @Override public T getValue() { return array[index]; }
        @Override public void setValue(T v) { array[index] = v; }
    }

    private static final class AccessorByRef<T> extends ByRef<T> {
        private final Supplier<T> getter;
        private final Consumer<T> setter;
        AccessorByRef(Supplier<T> getter, Consumer<T> setter) {
            this.getter = getter; this.setter = setter;
        }
        @Override public T getValue() { return getter.get(); }
        @Override public void setValue(T v) { setter.accept(v); }
    }
}
```

- [ ] **Step 2: `RefStructByRef.java`（槽模式，Q2-A）**

```java
package io.github.ningpp.compat;

/**
 * Slot holder carrying ref-struct-mapped values (e.g. Span). Plain final class —
 * Java has no ref-struct lifetime concept (see Span.java note).
 */
public final class RefStructByRef<T> {
    private T slot;

    private RefStructByRef() { }
    private RefStructByRef(T initial) { this.slot = initial; }

    public static <T> RefStructByRef<T> create() { return new RefStructByRef<>(); }
    public static <T> RefStructByRef<T> create(T initial) { return new RefStructByRef<>(initial); }

    public T getValue() { return slot; }
    public void setValue(T v) { this.slot = v; }
}
```

- [ ] **Step 3: `ByRefTest.java`（JUnit 5，仿 HolderClassesTest）**

```java
package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.*;

import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;

class ByRefTest {
    @Test void slotDefaultNullAndSeeded() {
        ByRef<String> scratch = ByRef.create();
        assertNull(scratch.getValue());
        scratch.setValue("x");
        assertEquals("x", scratch.getValue());
        ByRef<Integer> seeded = ByRef.create(7);
        assertEquals(7, seeded.getValue());
    }

    @Test void arrayElementPassesThroughAndAliases() {
        Integer[] arr = { 1, 2, 3 };
        ByRef<Integer> a = ByRef.create(arr, 0);
        ByRef<Integer> b = ByRef.create(arr, 1);
        a.setValue(10);
        assertEquals(10, arr[0]);
        // alias swap through two proxies
        ByRef<Integer> tmp = ByRef.create(arr[0]);
        tmp.setValue(a.getValue());
        a.setValue(b.getValue());
        b.setValue(tmp.getValue());
        assertEquals(2, arr[0]);
        assertEquals(10, arr[1]);
    }

    @Test void accessorFieldWriteThrough() {
        AtomicInteger field = new AtomicInteger(5);
        ByRef<Integer> proxy = ByRef.create(field::get, field::set);
        proxy.setValue(9);
        assertEquals(9, field.get());
        field.set(11);
        assertEquals(11, proxy.getValue());
    }

    @Test void writeSurvivesThrowingSetter() {
        AtomicInteger field = new AtomicInteger(0);
        ByRef<Integer> proxy = ByRef.create(field::get, v -> {
            field.set(v);
            if (v > 3) throw new IllegalStateException("boom");
        });
        assertThrows(IllegalStateException.class, () -> proxy.setValue(4));
        assertEquals(4, field.get());   // 已发生的写入不丢失（D3 §3.2）
    }

    @Test void refStructByRefSlotModes() {
        RefStructByRef<Integer> seeded = RefStructByRef.create(3);
        assertEquals(3, seeded.getValue());
        seeded.setValue(4);
        assertEquals(4, seeded.getValue());
        RefStructByRef<Integer> scratch = RefStructByRef.create();
        assertNull(scratch.getValue());
    }
}
```

- [ ] **Step 4: 运行** Run: `cd java/csharptojava-compat && mvn -q test` → 全绿。
- [ ] **Step 5: Commit** `git commit -m "feat(c01): compat ByRef/RefStructByRef holder family + JUnit (M3)"`

---

### Task 14: M3——TypeMappings 注册 + 转换器 J4/J8

**Files:**
- Modify: `config/TypeMappings.json`
- Modify: `src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs`

- [ ] **Step 1: TypeMappings 新条目（`typeMappings` 里紧跟 `Cs2jPrelude.CsPrelude` 条目之后；**注意 arity 形式**，与 `System.Func\`1` 同风格）**

```json
{
    "csharp": "Cs2jByRef.ByRef`1",
    "java": "ByRef",
    "imports": [
        "io.github.ningpp.compat.ByRef"
    ]
},
{
    "csharp": "Cs2jByRef.RefStructByRef`1",
    "java": "RefStructByRef",
    "imports": [
        "io.github.ningpp.compat.RefStructByRef"
    ]
},
```

`methodMappings` 里加：

```json
{
    "type": "Cs2jByRef.ByRef`1",
    "method": "Create",
    "javaMethod": "create"
},
{
    "type": "Cs2jByRef.RefStructByRef`1",
    "method": "Create",
    "javaMethod": "create"
},
```

- [ ] **Step 2: 转换器 J4——`Create` 静态泛型调用去类型见证**

在 `ExpressionTransformerFacade` 处理 InvocationExpression 的路径（先定位现有静态调用/泛型 receiver 的转换分支；若泛型名 receiver 已正确产出 `ByRef.create(...)` 则只补测试锁死；否则加分支：receiver 为 `GenericNameSyntax` 且其解析类型为 `Cs2jByRef.ByRef\`1`/`Cs2jByRef.RefStructByRef\`1` 时，发射 `ByRef.create(<args>)`/`RefStructByRef.create(<args>)` 并加 import——**永不发射 Class token/类型见证**，spec §11 与 C02 的划界）。

- [ ] **Step 3: J8 fail-fast（CS2J3320）**

在类型引用解析处（定位 `Cs2jByRef` 命名空间类型的转换入口）：`Cs2jByRef.*` 类型若不是 `ByRef\`1`/`RefStructByRef\`1` → `throw new NotSupportedException("CS2J3320: unmapped Cs2jByRef form '<display>' — the ref/out/in preprocessor only emits ByRef<T>/RefStructByRef<T>; hand-written support types are not converted.")`。禁止 TODO/裸类名。

- [ ] **Step 4: 转换器测试（两段式 rewrite→convert）**

新文件 `tests/CSharpToJava.ConversionTests/Categories/RefOutInByRefPathTests.cs`：

```csharp
public class RefOutInByRefPathTests : ConversionTestBase
{
    private static string RewriteFirst(string src)
    {
        var (tree, model) = /* 与 ProjectTests 同款 ParseWithSemantics（复制 harness） */;
        return new CSharpToJava.Core.RefOutInPreprocessor.RefOutInRewriter().Rewrite(src, model).OutputCode;
    }

    [Fact]
    public void InternalRefCall_ProducesJavaByRefLambda()
    {
        var csharp = RewriteFirst(@"
public class Calc
{
    public void Bump(ref int v) { v = v + 1; }
    public void Use()
    {
        int x = 1;
        Bump(ref x);
    }
}");
        var java = ConvertSource(csharp);
        Assert.Contains("ByRef<Integer>", java);
        Assert.Contains("ByRef.create(", java);
        Assert.Contains("() -> x", java);
        Assert.Contains("v -> x = v", java);
        Assert.Contains("x.getValue()", java);
        Assert.DoesNotContain("IntHolder", java);
    }

    [Fact]
    public void OutSlot_ReadBackUsesGetValue()
    {
        var csharp = RewriteFirst(@"
public class Calc
{
    public bool TryGet(out int r) { r = 42; return true; }
    public int Use()
    {
        if (TryGet(out var v)) { return v; }
        return 0;
    }
}");
        var java = ConvertSource(csharp);
        Assert.Contains("ByRef.create()", java);
        Assert.Contains("v = __out", java);
        Assert.Contains(".getValue()", java);
    }

    [Fact]
    public void ValueWriteUsesSetValue()
    {
        var csharp = @"
public class Calc
{
    public void Direct(Cs2jByRef.ByRef<int> v) { v.Value = 3; }
}";
        var java = ConvertSource(csharp);
        Assert.Contains("setValue(3)", java);
    }
}
```

（`ConvertSource`/`ConversionTestBase` 的确切入口以现有 `RefOutInComprehensiveTests` 的调用方式为准——执行时先读该文件头部，套同一基类。）

- [ ] **Step 5: 运行 + Commit** `git commit -m "feat(c01): TypeMappings + converter recognition for ByRef forms (J1-J4, J8 CS2J3320)"`

---

### Task 15: M3——J6/J7 缝合回归测试

**Files:**
- Modify: `tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs`（新增而非迁移）

- [ ] **Step 1: J6——旧 holder 不被二次包装**（默认链产物断言：ByRef 形参转换后不再生成 IntHolder 包装——直连 pipeline 用例保持旧断言不变，加一条「rewrite→convert 后产物无 IntHolder」）。
- [ ] **Step 2: J7——H 槽转发外部 API**（fixture：内部方法持 ByRef 槽 → 调 `int.TryParse` 形态外部 API；断言 Java 产物同时含 `ByRef.create`（内部槽）与 `Integer.parseInt`/TryParse 旧路径形态，且编译语义正确——以现有 ConversionTests 的编译校验机制为准）。
- [ ] **Step 3: 运行 ConversionTests + Commit** `git commit -m "test(c01): J6/J7 stitching regressions"`

---

### Task 16: M4——旧断言迁移（§7.4）

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs`
- Modify: `tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs`（26 个用例二分迁移）
- Delete: `tests/CSharpToJava.Tests/RefOutSameVariableTests.cs`

- [ ] **Step 1: RefOutInProjectTests**——按 spec §7.4 拆二：①主用例改「先 ref-out-in 预处理再转换」断言 `ByRef`；②保留 1 个直连 pipeline 用例（注释标注「旧路径安全网」）保留 IntHolder 断言。
- [ ] **Step 2: ComprehensiveTests 26 用例二分**——逐用例判断：输入只含外部/锁定 ref/out（BCL 调用、`in` 按值、丢弃 scratch、P/Invoke）→ 留旧断言 + `[Trait("Category", "LegacyHolderPath")]`；含内部 ref/out 方法 → 改两段式断言 `ByRef` + `[Trait("Category", "ByRefPath")]`。**每个改动的用例在注释里写一行迁移依据。**
- [ ] **Step 3: 删除空壳 `RefOutSameVariableTests.cs`**（22 行、无测试方法——spec §7.4 允许；迁移理由注释写进 commit message）。
- [ ] **Step 4: 全量测试** Run: `dotnet test CSharpToJavaConverter.slnx -c Release --nologo` → 无新增失败。
- [ ] **Step 5: Commit** `git commit -m "test(c01): migrate legacy holder assertions to ByRef path per spec §7.4"`

---

### Task 17: M4——端到端 + 回归 + 文档收口

- [ ] **Step 1: 端到端** Run: `dotnet src/CSharpToJava.CLI/bin/Release/net10.0/CSharpToJava.CLI.dll convert-project -s tests/SampleRefOutInV2Solution/SampleRefOutInV2.sln -d /tmp/c01-e2e` → Java 工程 `cd /tmp/c01-e2e && mvn -q compile` 零错误（先看产物 pom/目录布局，用既有 e2e 模式）。
- [ ] **Step 2: 仓库全量回归** Run: `dotnet test CSharpToJavaConverter.slnx -c Release --nologo` → 基线比较无新增失败。
- [ ] **Step 3: 链契约不变确认** Run: `dotnet ... --list-passes` → id/stage/默认值/顺序与 spec §3 一致；`--no-ref-out-in` 回退可用（跑一次带开关的转换确认）。
- [ ] **Step 4: 文档**——`prompts/preprocessor-catalog.md` C01 条目状态改为「完成（C# 闭环 + Java 映射已接线）」、§4 风险 5 标注已解除；`ROADMAP.md` 相应更新。
- [ ] **Step 5: Commit** `git commit -m "docs(c01): close C01 — catalog status + roadmap (M4)"`

---

## 已知风险与回退

1. **Task 1 探针失败** → 回退 Q2 方案 B（强类型 per-type 生成），Task 6 重写；其余任务不受阻。
2. **Q4 lambda 经 JDK 函数式不通**（Task 14 测试红）→ 启用备选：compat 自定义 `Cs2jGetter/Setter` 函数式接口，ByRef.create 重载换签名。
3. **§8.3 SDK 基线**（`D:\csharp-sdk` 两 sln「新增失败为零」）耗时大，作为 Task 17 之后独立验证步骤执行（只读基线快照 → 修复后同口径比对）；若 sdk 构建在基线就红，记录既有失败集合作为对照。
4. **do-while 条件含可提升调用** → CS2J3303 拒绝（continue 语义），测试锁死该行为而非静默错译。
