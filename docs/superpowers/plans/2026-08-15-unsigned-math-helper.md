# UnsignedMath 静态工具类实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `unsigned-type` 预处理从扩展方法方案改为单一 `Cs2jUnsigned.UnsignedMath` 静态工具类方案：所有涉及 byte/ushort/uint/ulong 的运算/比较/移位/转换/混合运算改写为全限定静态调用，并保持中间 C# 可编译、语义等价。

**Architecture:** 生成物只有一个 `_Cs2jUnsignedMath.cs`（块式 namespace、`internal static class UnsignedMath`，方法名全局唯一）；重写器按语义模型的 ConvertedType 归类到 Int32/Int64/UInt32/UInt64 域，混合运算显式插入 `ToInt64UInt32`/`ToUInt64UInt32`/`unchecked((int)...)` 等归一化调用；预处理器不再复制本地副本，改为给每个编译项目追加 `Compile Include` + `Link` 引用根目录辅助文件。

**Tech Stack:** C# / Roslyn（Microsoft.CodeAnalysis.CSharp）/ xUnit / dotnet CLI。

**执行说明：** 在当前特性分支 `dev-3.x` 上直接执行（不新建 worktree：本特性全部提交已在 dev-3.x，用户评审流程基于该分支）。每个任务遵循 TDD：先写失败测试、确认失败、实现、确认通过、提交。

---

## Task 1: 提交规格修订

**Files:**
- Modify: `docs/superpowers/specs/2026-08-15-unsigned-math-helper-design.md`

- [ ] **Step 1: 提交当前规格**

```bash
git add docs/superpowers/specs/2026-08-15-unsigned-math-helper-design.md
git commit -m "docs: lock unsigned-math helper design after review"
```

Expected: commit 成功，无未提交改动（`git status --short` 只显示后续任务文件）。

---

## Task 2: 新增 UnsignedDomain 与共享语法构建器（含失败测试）

**Files:**
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedDomain.cs`
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedMathSyntax.cs`
- Modify: `tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs`（新增构建器断言）

- [ ] **Step 1: 写失败测试（构建器输出）**

在 `UnsignedTypeRewriterUnitTests.cs` 的 `ParseWithSemantics` 之后新增：

```csharp
[Fact]
public void StaticCallBuilder_EmitsFullyQualifiedInvocation()
{
    var call = UnsignedMathSyntax.StaticCall(
        "AddUInt32",
        SyntaxFactory.ParseExpression("a"),
        SyntaxFactory.ParseExpression("b"));
    Assert.Equal("Cs2jUnsigned.UnsignedMath.AddUInt32(a, b)", call.ToString());
}

[Fact]
public void UncheckedCastBuilder_EmitsUncheckedCast()
{
    var cast = UnsignedMathSyntax.UncheckedCast(
        SyntaxKind.IntKeyword,
        SyntaxFactory.ParseExpression("u"));
    Assert.Equal("unchecked((int)u)", cast.ToString());
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~StaticCallBuilder|FullyQualifiedName~UncheckedCastBuilder"
```

Expected: FAIL（`UnsignedMathSyntax` 不存在）。

- [ ] **Step 3: 创建 UnsignedDomain.cs**

```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// 无符号运算的目标域。方法名后缀与 Java 存储宽度一一对应。
/// </summary>
internal enum UnsignedDomain
{
    Int32,
    Int64,
    UInt32,
    UInt64,
    Native,
}
```

- [ ] **Step 4: 创建 UnsignedMathSyntax.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// 生成 Cs2jUnsigned.UnsignedMath 全限定静态调用所需的语法节点。
/// </summary>
internal static class UnsignedMathSyntax
{
    public static string Suffix(UnsignedDomain domain) => domain switch
    {
        UnsignedDomain.Int32 => "Int32",
        UnsignedDomain.Int64 => "Int64",
        UnsignedDomain.UInt32 => "UInt32",
        UnsignedDomain.UInt64 => "UInt64",
        _ => string.Empty,
    };

    public static InvocationExpressionSyntax StaticCall(
        string method, params ExpressionSyntax[] arguments)
    {
        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("Cs2jUnsigned"),
                SyntaxFactory.IdentifierName("UnsignedMath")),
            SyntaxFactory.IdentifierName(method));

        return SyntaxFactory.InvocationExpression(
            memberAccess,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(
                    arguments.Select(SyntaxFactory.Argument))));
    }

    public static CheckedExpressionSyntax UncheckedCast(
        SyntaxKind typeKeyword, ExpressionSyntax operand)
    {
        var cast = SyntaxFactory.CastExpression(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(typeKeyword)),
            operand);
        return SyntaxFactory.CheckedExpression(
            SyntaxKind.UncheckedExpression, cast);
    }

    /// <summary>
    /// 按目标域归一化操作数：uint→int/long、ulong→long 显式插入转换。
    /// byte/ushort→int 的提升两侧一致，直接传参。
    /// </summary>
    public static ExpressionSyntax Normalize(
        ExpressionSyntax operand,
        UnsignedDomain domain,
        SpecialType special)
    {
        return domain switch
        {
            UnsignedDomain.Int64 when special == SpecialType.System_UInt32 =>
                StaticCall("ToInt64UInt32", UncheckedCast(SyntaxKind.IntKeyword, operand)),
            UnsignedDomain.UInt32 when special == SpecialType.System_UInt32 =>
                UncheckedCast(SyntaxKind.IntKeyword, operand),
            UnsignedDomain.UInt64 when special == SpecialType.System_UInt64 =>
                UncheckedCast(SyntaxKind.LongKeyword, operand),
            UnsignedDomain.UInt64 when special == SpecialType.System_UInt32 =>
                StaticCall("ToInt64UInt32", UncheckedCast(SyntaxKind.IntKeyword, operand)),
            _ => operand,
        };
    }

    /// <summary>
    /// 复合赋值/自增自减写回目标类型所需的收尾转换。
    /// </summary>
    public static ExpressionSyntax WrapTarget(
        SpecialType target, ExpressionSyntax value) => target switch
    {
        SpecialType.System_Byte => StaticCall("ToByteInt32", value),
        SpecialType.System_UInt16 => StaticCall("ToUInt16Int32", value),
        SpecialType.System_UInt32 => UncheckedCast(SyntaxKind.UIntKeyword, value),
        SpecialType.System_UInt64 => UncheckedCast(SyntaxKind.ULongKeyword, value),
        _ => value,
    };
}
```

- [ ] **Step 5: 运行确认通过**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~StaticCallBuilder|FullyQualifiedName~UncheckedCastBuilder"
```

Expected: PASS（2 个）。

- [ ] **Step 6: 提交**

```bash
git add src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedDomain.cs src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedMathSyntax.cs tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs
git commit -m "feat: add unsigned math syntax builders and domain enum"
```

---

## Task 3: 替换辅助文件内容（RED → GREEN）

**Files:**
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedMathFileContent.cs`
- Delete: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedExtensionsFileContent.cs`
- Modify: `tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs`

- [ ] **Step 1: 改失败测试**

把 `ExtensionFile_TypeIsInternal` 改为：

```csharp
[Fact]
public void HelperFile_IsInternalUnsignedMathWithBlockNamespace()
{
    var content = UnsignedMathFileContent.Generate();
    Assert.Contains("namespace Cs2jUnsigned", content);
    Assert.Contains("internal static class UnsignedMath", content);
    Assert.Contains("public static int AddUInt32(int a, int b)", content);
    Assert.Contains("public static long ToInt64UInt32(int v)", content);
    Assert.DoesNotContain("public static class UnsignedExtensions", content);
}
```

并把文件顶部 `using CSharpToJava.Core.UnsignedTypeRewriter;` 保留（命名空间未变）。

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~HelperFile_IsInternalUnsignedMathWithBlockNamespace"
```

Expected: FAIL（`UnsignedMathFileContent` 不存在）。

- [ ] **Step 3: 创建 UnsignedMathFileContent.cs**

```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

public static class UnsignedMathFileContent
{
    public const string FileName = "_Cs2jUnsignedMath.cs";

    public static string Generate() => """
        namespace Cs2jUnsigned
        {
            internal static class UnsignedMath
            {
                // ---- Int32 domain ----
                public static int AddInt32(int a, int b) => unchecked(a + b);
                public static int SubtractInt32(int a, int b) => unchecked(a - b);
                public static int MultiplyInt32(int a, int b) => unchecked(a * b);
                public static int DivideInt32(int a, int b) => a / b;
                public static int RemainderInt32(int a, int b) => a % b;
                public static int AndInt32(int a, int b) => a & b;
                public static int OrInt32(int a, int b) => a | b;
                public static int XorInt32(int a, int b) => a ^ b;
                public static int NotInt32(int a) => ~a;
                public static int LeftShiftInt32(int a, int n) => a << (n & 31);
                public static int RightShiftInt32(int a, int n) => a >> (n & 31);
                public static int CompareInt32(int a, int b) => a < b ? -1 : a > b ? 1 : 0;

                // ---- Int64 domain ----
                public static long AddInt64(long a, long b) => unchecked(a + b);
                public static long SubtractInt64(long a, long b) => unchecked(a - b);
                public static long MultiplyInt64(long a, long b) => unchecked(a * b);
                public static long DivideInt64(long a, long b) => a / b;
                public static long RemainderInt64(long a, long b) => a % b;
                public static long AndInt64(long a, long b) => a & b;
                public static long OrInt64(long a, long b) => a | b;
                public static long XorInt64(long a, long b) => a ^ b;
                public static int CompareInt64(long a, long b) => a < b ? -1 : a > b ? 1 : 0;

                // ---- UInt32 domain ----
                public static int AddUInt32(int a, int b) => unchecked(a + b);
                public static int SubtractUInt32(int a, int b) => unchecked(a - b);
                public static int MultiplyUInt32(int a, int b) => unchecked(a * b);
                public static int DivideUInt32(int a, int b) => unchecked((int)((a & 0xFFFFFFFFL) / (b & 0xFFFFFFFFL)));
                public static int RemainderUInt32(int a, int b) => unchecked((int)((a & 0xFFFFFFFFL) % (b & 0xFFFFFFFFL)));
                public static int AndUInt32(int a, int b) => a & b;
                public static int OrUInt32(int a, int b) => a | b;
                public static int XorUInt32(int a, int b) => a ^ b;
                public static int NotUInt32(int a) => ~a;
                public static int LeftShiftUInt32(int a, int n) => a << (n & 31);
                public static int RightShiftUInt32(int a, int n) => unchecked((int)((a & 0xFFFFFFFFL) >> (n & 31)));
                public static int CompareUInt32(int a, int b)
                {
                    var ua = a & 0xFFFFFFFFL;
                    var ub = b & 0xFFFFFFFFL;
                    return ua < ub ? -1 : ua > ub ? 1 : 0;
                }

                // ---- UInt64 domain ----
                public static long AddUInt64(long a, long b) => unchecked(a + b);
                public static long SubtractUInt64(long a, long b) => unchecked(a - b);
                public static long MultiplyUInt64(long a, long b) => unchecked(a * b);
                public static long DivideUInt64(long a, long b) => unchecked((long)((ulong)a / (ulong)b));
                public static long RemainderUInt64(long a, long b) => unchecked((long)((ulong)a % (ulong)b));
                public static long AndUInt64(long a, long b) => a & b;
                public static long OrUInt64(long a, long b) => a | b;
                public static long XorUInt64(long a, long b) => a ^ b;
                public static long NotUInt64(long a) => ~a;
                public static long LeftShiftUInt64(long a, int n) => a << (n & 63);
                public static long RightShiftUInt64(long a, int n) => unchecked((long)((ulong)a >> (n & 63)));
                public static int CompareUInt64(long a, long b)
                {
                    if (a == b) return 0;
                    if (a >= 0 && b < 0) return -1;
                    if (a < 0 && b >= 0) return 1;
                    return a < b ? -1 : 1;
                }

                // ---- Conversions ----
                public static System.Byte ToByteInt32(int v) => unchecked((byte)(v & 0xFF));
                public static System.Byte ToByteInt64(long v) => unchecked((byte)((int)v & 0xFF));
                public static System.UInt16 ToUInt16Int32(int v) => unchecked((ushort)(v & 0xFFFF));
                public static System.UInt16 ToUInt16Int64(long v) => unchecked((ushort)((int)v & 0xFFFF));
                public static long ToInt64UInt32(int v) => v & 0xFFFFFFFFL;
                public static System.UInt64 ToUInt64UInt32(int v) => unchecked((ulong)(v & 0xFFFFFFFFL));
                public static int ToInt32UInt64(long v) => unchecked((int)v);
                public static double ToDoubleUInt32(int v) => (double)(v & 0xFFFFFFFFL);
                public static double ToDoubleUInt64(long v) => (double)(ulong)v;
                public static float ToSingleUInt32(int v) => (float)(v & 0xFFFFFFFFL);
                public static float ToSingleUInt64(long v) => (float)(ulong)v;
                public static decimal ToDecimalUInt32(int v) => (decimal)(v & 0xFFFFFFFFL);
                public static decimal ToDecimalUInt64(long v) => (decimal)(ulong)v;
            }
        }
        """;
}
```

- [ ] **Step 4: 删除旧文件**

```bash
git rm src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedExtensionsFileContent.cs
```

- [ ] **Step 5: 运行确认通过**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~HelperFile_IsInternalUnsignedMathWithBlockNamespace"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "feat: replace extension helper content with static UnsignedMath class"
```

---

## Task 4: 重写 UnsignedTypeSyntaxRewriter（静态调用）

**Files:**
- Replace: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs`（整文件替换）
- Modify: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixAnnotations.cs`
- Replace: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedPostfixRewriter.cs`（整文件替换）
- Modify: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeRewriter.cs`（去掉 using 注入）

- [ ] **Step 1: 写失败测试（新调用形态 + 混合运算）**

把 `UnsignedTypeRewriterUnitTests.cs` 中以下方法替换/新增（完整文件在 Task 6 统一定稿，这里先落地关键断言）：

```csharp
[Fact]
public void BinaryOperator_ConvertedToStaticCall()
{
    var src = "public class C { public uint Add(uint a, uint b) { return a + b; } }";
    var (tree, model) = ParseWithSemantics(src);
    var result = new UnsignedTypeRewriter().Rewrite(src, model);
    Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), unchecked((int)b))", result.OutputCode);
    Assert.DoesNotContain("using Cs2jUnsigned;", result.OutputCode);
}

[Fact]
public void MixedUIntPlusInt_PromotesToInt64Domain()
{
    var src = "public class C { public long M(uint a, int b) { return a + b; } }";
    var (tree, model) = ParseWithSemantics(src);
    var result = new UnsignedTypeRewriter().Rewrite(src, model);
    Assert.Contains("Cs2jUnsigned.UnsignedMath.AddInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)a)), b)", result.OutputCode);
}

[Fact]
public void MixedUShortPlusInt_UsesInt32Domain()
{
    var src = "public class C { public int M(ushort s, int i) { return s + i; } }";
    var (tree, model) = ParseWithSemantics(src);
    var result = new UnsignedTypeRewriter().Rewrite(src, model);
    Assert.Contains("Cs2jUnsigned.UnsignedMath.AddInt32(s, i)", result.OutputCode);
}

[Fact]
public void ByteIncrement_WrapsThroughToByte()
{
    var src = "public class C { public void M(byte b) { b++; } }";
    var (tree, model) = ParseWithSemantics(src);
    var result = new UnsignedTypeRewriter().Rewrite(src, model);
    Assert.Contains("b = Cs2jUnsigned.UnsignedMath.ToByteInt32(Cs2jUnsigned.UnsignedMath.AddInt32(b, 1))", result.OutputCode);
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~BinaryOperator_ConvertedToStaticCall|FullyQualifiedName~MixedUIntPlusInt_PromotesToInt64Domain|FullyQualifiedName~MixedUShortPlusInt_UsesInt32Domain|FullyQualifiedName~ByteIncrement_WrapsThroughToByte"
```

Expected: FAIL（输出仍是扩展方法调用/注入 using）。

- [ ] **Step 3: 整文件替换 UnsignedTypeSyntaxRewriter.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

internal sealed class UnsignedTypeSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly UnsignedTypeRewriterOptions _options;
    private readonly UnsignedTypeRewriterStatistics _stats;
    private readonly List<UnsignedTypeRewriterDiagnostic> _diagnostics;

    public bool UsedExtensions { get; private set; }

    private static readonly Dictionary<SyntaxKind, string> KeywordToFullName = new()
    {
        { SyntaxKind.ByteKeyword,   "System.Byte"   },
        { SyntaxKind.UShortKeyword, "System.UInt16" },
        { SyntaxKind.UIntKeyword,   "System.UInt32" },
        { SyntaxKind.ULongKeyword,  "System.UInt64" },
    };

    private static readonly Dictionary<SyntaxKind, string> BinaryOpMethod = new()
    {
        { SyntaxKind.AddExpression,         "Add"       },
        { SyntaxKind.SubtractExpression,    "Subtract"  },
        { SyntaxKind.MultiplyExpression,    "Multiply"  },
        { SyntaxKind.DivideExpression,      "Divide"    },
        { SyntaxKind.ModuloExpression,      "Remainder" },
        { SyntaxKind.BitwiseAndExpression,  "And"       },
        { SyntaxKind.BitwiseOrExpression,   "Or"        },
        { SyntaxKind.ExclusiveOrExpression, "Xor"       },
        { SyntaxKind.LeftShiftExpression,   "LeftShift" },
        { SyntaxKind.RightShiftExpression,  "RightShift"},
    };

    private static readonly Dictionary<SyntaxKind, string> CompoundAssignmentMethod = new()
    {
        { SyntaxKind.AddAssignmentExpression,         "Add"       },
        { SyntaxKind.SubtractAssignmentExpression,    "Subtract"  },
        { SyntaxKind.MultiplyAssignmentExpression,    "Multiply"  },
        { SyntaxKind.DivideAssignmentExpression,      "Divide"    },
        { SyntaxKind.ModuloAssignmentExpression,      "Remainder" },
        { SyntaxKind.AndAssignmentExpression,         "And"       },
        { SyntaxKind.OrAssignmentExpression,          "Or"        },
        { SyntaxKind.ExclusiveOrAssignmentExpression, "Xor"       },
        { SyntaxKind.LeftShiftAssignmentExpression,   "LeftShift" },
        { SyntaxKind.RightShiftAssignmentExpression,  "RightShift"},
    };

    public UnsignedTypeSyntaxRewriter(
        SemanticModel? semanticModel,
        UnsignedTypeRewriterOptions options,
        UnsignedTypeRewriterStatistics stats,
        List<UnsignedTypeRewriterDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantTokens().Any(t => KeywordToFullName.ContainsKey(t.Kind()));

    internal static bool ContainsUnsignedKeyword(SyntaxNode node)
        => node.DescendantTokens().Any(t => KeywordToFullName.ContainsKey(t.Kind()));

    private static bool IsUnsignedType(ITypeSymbol? type)
        => type?.SpecialType is SpecialType.System_Byte
            or SpecialType.System_UInt16
            or SpecialType.System_UInt32
            or SpecialType.System_UInt64;

    private bool IsUnsignedExpression(ExpressionSyntax expression)
        => _semanticModel != null && IsUnsignedType(GetEffectiveType(expression));

    private ITypeSymbol? GetEffectiveType(ExpressionSyntax expression)
    {
        if (_semanticModel == null)
        {
            return null;
        }

        var info = _semanticModel.GetTypeInfo(expression);
        return info.ConvertedType ?? info.Type;
    }

    private SpecialType? GetNaturalSpecial(ExpressionSyntax expression)
        => _semanticModel?.GetTypeInfo(expression).Type?.SpecialType;

    private static SpecialType? GetSpecial(ExpressionSyntax expression, ITypeSymbol? type)
        => type?.SpecialType;

    private static bool IsInCheckedContext(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            if (current is CheckedExpressionSyntax checkedExpression)
            {
                return checkedExpression.IsKind(SyntaxKind.CheckedExpression);
            }

            if (current is CheckedStatementSyntax checkedStatement)
            {
                return checkedStatement.IsKind(SyntaxKind.CheckedStatement);
            }
        }

        return false;
    }

    private static bool IsInConstantExpressionContext(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case FieldDeclarationSyntax fieldDeclaration
                    when fieldDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword)
                         && fieldDeclaration.Declaration.Variables
                             .Any(variable => variable.Initializer?.Span.Contains(node.Span) == true):
                    return true;
                case LocalDeclarationStatementSyntax localDeclaration
                    when localDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword)
                         && localDeclaration.Declaration.Variables
                             .Any(variable => variable.Initializer?.Span.Contains(node.Span) == true):
                    return true;
                case EnumMemberDeclarationSyntax enumMember
                    when enumMember.EqualsValue?.Span.Contains(node.Span) == true:
                    return true;
                case AttributeArgumentSyntax attributeArgument
                    when attributeArgument.Expression.Span.Contains(node.Span):
                    return true;
                case ParameterSyntax parameter
                    when parameter.Default?.Span.Contains(node.Span) == true:
                    return true;
                case CaseSwitchLabelSyntax caseLabel
                    when caseLabel.Value.Span.Contains(node.Span):
                    return true;
                case PropertyDeclarationSyntax propertyDeclaration
                    when propertyDeclaration.Initializer?.Span.Contains(node.Span) == true:
                    return true;
            }
        }

        return false;
    }

    private static bool IsInRepeatedEvaluationContext(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case WhileStatementSyntax whileStatement when whileStatement.Condition.Span.Contains(node.Span):
                    return true;
                case DoStatementSyntax doStatement when doStatement.Condition.Span.Contains(node.Span):
                    return true;
                case ForStatementSyntax forStatement
                    when (forStatement.Condition?.Span.Contains(node.Span) == true
                          || forStatement.Incrementors.Any(i => i.Span.Contains(node.Span))):
                    return true;
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case QueryExpressionSyntax:
                    return true;
            }
        }

        return false;
    }

    private static bool IsSafeAssignmentTarget(ExpressionSyntax target)
    {
        return target switch
        {
            IdentifierNameSyntax => true,
            MemberAccessExpressionSyntax memberAccess => IsSideEffectFreeReceiver(memberAccess.Expression),
            _ => false,
        };
    }

    private static bool IsSideEffectFreeReceiver(ExpressionSyntax receiver) => receiver switch
    {
        ThisExpressionSyntax => true,
        BaseExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax memberAccess => IsSideEffectFreeReceiver(memberAccess.Expression),
        _ => false,
    };

    private static bool IsBinaryOperatorRewriteKind(SyntaxKind kind)
        => BinaryOpMethod.ContainsKey(kind)
           || kind is SyntaxKind.EqualsExpression
               or SyntaxKind.NotEqualsExpression
               or SyntaxKind.LessThanExpression
               or SyntaxKind.LessThanOrEqualExpression
               or SyntaxKind.GreaterThanExpression
               or SyntaxKind.GreaterThanOrEqualExpression;

    private static UnsignedDomain GetBinaryDomain(SpecialType? left, SpecialType? right)
    {
        if (left == null || right == null)
        {
            return UnsignedDomain.Native;
        }

        if (left == SpecialType.System_UInt64 || right == SpecialType.System_UInt64)
        {
            return UnsignedDomain.UInt64;
        }

        if (left == SpecialType.System_Int64 || right == SpecialType.System_Int64)
        {
            return UnsignedDomain.Int64;
        }

        if (left == SpecialType.System_UInt32 || right == SpecialType.System_UInt32)
        {
            if (left == SpecialType.System_Int32 || right == SpecialType.System_Int32
                || left == SpecialType.System_Int16 || right == SpecialType.System_Int16
                || left == SpecialType.System_SByte || right == SpecialType.System_SByte)
            {
                return UnsignedDomain.Int64;
            }

            return UnsignedDomain.UInt32;
        }

        return UnsignedDomain.Int32;
    }

    private static UnsignedDomain GetShiftDomain(SpecialType? left) => left switch
    {
        SpecialType.System_Byte or SpecialType.System_UInt16 => UnsignedDomain.Int32,
        SpecialType.System_UInt32 => UnsignedDomain.UInt32,
        SpecialType.System_UInt64 => UnsignedDomain.UInt64,
        _ => UnsignedDomain.Native,
    };

    private static UnsignedDomain GetUnaryDomain(SpecialType? operand) => operand switch
    {
        SpecialType.System_Byte or SpecialType.System_UInt16 => UnsignedDomain.Int32,
        SpecialType.System_UInt32 => UnsignedDomain.UInt32,
        SpecialType.System_UInt64 => UnsignedDomain.UInt64,
        _ => UnsignedDomain.Native,
    };

    private static ExpressionSyntax Clone(ExpressionSyntax expression)
        => SyntaxFactory.ParseExpression(expression.ToString());

    private static SyntaxKind ComparisonKind(SyntaxKind kind) => kind switch
    {
        SyntaxKind.LessThanExpression => SyntaxKind.LessThanExpression,
        SyntaxKind.LessThanOrEqualExpression => SyntaxKind.LessThanOrEqualExpression,
        SyntaxKind.GreaterThanExpression => SyntaxKind.GreaterThanExpression,
        _ => SyntaxKind.GreaterThanOrEqualExpression,
    };

    private static ExpressionSyntax LiteralZero()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));

    private static ExpressionSyntax LiteralOne()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));

    public override SyntaxNode? VisitPredefinedType(PredefinedTypeSyntax node)
    {
        if (!KeywordToFullName.TryGetValue(node.Keyword.Kind(), out var fullName))
        {
            return base.VisitPredefinedType(node);
        }

        _stats.TypesReplaced++;
        return SyntaxFactory.ParseTypeName(fullName).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (KeywordToFullName.TryGetValue(node.Identifier.Kind(), out var fullName))
        {
            _stats.TypesReplaced++;
            return SyntaxFactory.ParseTypeName(fullName).WithTriviaFrom(node);
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitArrayType(ArrayTypeSyntax node)
    {
        if (ContainsUnsignedKeyword(node.ElementType))
        {
            _stats.ArraysRewritten++;
        }

        return base.VisitArrayType(node);
    }

    public override SyntaxNode? VisitTypeArgumentList(TypeArgumentListSyntax node)
    {
        if (node.Arguments.Any(ContainsUnsignedKeyword))
        {
            _stats.GenericArgsReplaced++;
        }

        return base.VisitTypeArgumentList(node);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var isShift = node.IsKind(SyntaxKind.LeftShiftExpression)
                      || node.IsKind(SyntaxKind.RightShiftExpression);
        var domain = ShouldRewriteBinary(node, isShift);
        var leftEffectiveSpecial = GetSpecial(node.Left, GetEffectiveType(node.Left));
        var rightEffectiveSpecial = GetSpecial(node.Right, GetEffectiveType(node.Right));
        // 归一化只看操作数“自然类型”：字面量 5 的自然类型是 int，直接传参；
        // 0xFFFFFFFFu 的自然类型是 uint，才需要显式 unchecked((int)...)。
        var leftNaturalSpecial = GetNaturalSpecial(node.Left) ?? SpecialType.None;
        var rightNaturalSpecial = GetNaturalSpecial(node.Right) ?? SpecialType.None;

        var rewritten = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        if (domain == UnsignedDomain.Native)
        {
            return rewritten;
        }

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        if (isShift)
        {
            var method = BinaryOpMethod[rewritten.Kind()] + UnsignedMathSyntax.Suffix(domain);
            return UnsignedMathSyntax.StaticCall(
                method,
                UnsignedMathSyntax.Normalize(rewritten.Left, domain, leftNaturalSpecial),
                rewritten.Right);
        }

        if (rewritten.IsKind(SyntaxKind.EqualsExpression)
            || rewritten.IsKind(SyntaxKind.NotEqualsExpression)
            || rewritten.IsKind(SyntaxKind.LessThanExpression)
            || rewritten.IsKind(SyntaxKind.LessThanOrEqualExpression)
            || rewritten.IsKind(SyntaxKind.GreaterThanExpression)
            || rewritten.IsKind(SyntaxKind.GreaterThanOrEqualExpression))
        {
            var compare = UnsignedMathSyntax.StaticCall(
                "Compare" + UnsignedMathSyntax.Suffix(domain),
                UnsignedMathSyntax.Normalize(rewritten.Left, domain, leftNaturalSpecial),
                UnsignedMathSyntax.Normalize(rewritten.Right, domain, rightNaturalSpecial));
            return SyntaxFactory.BinaryExpression(
                ComparisonKind(rewritten.Kind()), compare, LiteralZero());
        }

        var binaryMethod = BinaryOpMethod[rewritten.Kind()] + UnsignedMathSyntax.Suffix(domain);
        return UnsignedMathSyntax.StaticCall(
            binaryMethod,
            UnsignedMathSyntax.Normalize(rewritten.Left, domain, leftNaturalSpecial),
            UnsignedMathSyntax.Normalize(rewritten.Right, domain, rightNaturalSpecial));
    }

    private UnsignedDomain ShouldRewriteBinary(BinaryExpressionSyntax node, bool isShift)
    {
        if (_semanticModel == null
            || !_options.EnableOperatorRewrite
            || !IsBinaryOperatorRewriteKind(node.Kind())
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node))
        {
            return UnsignedDomain.Native;
        }

        var leftSpecial = GetSpecial(node.Left, GetEffectiveType(node.Left));
        var rightSpecial = GetSpecial(node.Right, GetEffectiveType(node.Right));

        if (isShift)
        {
            return GetShiftDomain(leftSpecial);
        }

        if (!IsUnsignedType(GetEffectiveType(node.Left))
            && !IsUnsignedType(GetEffectiveType(node.Right)))
        {
            return UnsignedDomain.Native;
        }

        return GetBinaryDomain(leftSpecial, rightSpecial);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var rewritten = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;

        if (_semanticModel == null
            || !_options.EnableOperatorRewrite
            || !CompoundAssignmentMethod.ContainsKey(node.Kind())
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node)
            || !IsSafeAssignmentTarget(node.Left))
        {
            return rewritten;
        }

        var leftType = GetEffectiveType(node.Left);
        if (!IsUnsignedType(leftType))
        {
            return rewritten;
        }

        var isShift = node.IsKind(SyntaxKind.LeftShiftAssignmentExpression)
                      || node.IsKind(SyntaxKind.RightShiftAssignmentExpression);
        var leftSpecial = GetSpecial(node.Left, leftType);
        var rightSpecial = GetSpecial(node.Right, GetEffectiveType(node.Right));
        var leftNaturalSpecial = GetNaturalSpecial(node.Left) ?? SpecialType.None;
        var rightNaturalSpecial = GetNaturalSpecial(node.Right) ?? SpecialType.None;
        var domain = isShift
            ? GetShiftDomain(leftSpecial)
            : GetBinaryDomain(leftSpecial, rightSpecial);
        if (domain == UnsignedDomain.Native)
        {
            return rewritten;
        }

        var method = CompoundAssignmentMethod[node.Kind()] + UnsignedMathSyntax.Suffix(domain);
        var call = UnsignedMathSyntax.StaticCall(
            method,
            UnsignedMathSyntax.Normalize(rewritten.Left, domain, leftNaturalSpecial),
            UnsignedMathSyntax.Normalize(rewritten.Right, domain, rightNaturalSpecial));

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            rewritten.Left,
            UnsignedMathSyntax.WrapTarget(leftType!.SpecialType, call));
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        var method = node.Kind() switch
        {
            SyntaxKind.PostIncrementExpression => "Add",
            SyntaxKind.PostDecrementExpression => "Subtract",
            _ => null,
        };

        if (method != null
            && !IsInCheckedContext(node)
            && !IsInRepeatedEvaluationContext(node)
            && IsUnsignedExpression(node.Operand)
            && IsSafeAssignmentTarget(node.Operand))
        {
            var type = GetEffectiveType(node.Operand);
            return base.VisitPostfixUnaryExpression(
                node.WithAdditionalAnnotations(
                    UnsignedPostfixAnnotations.For(type!.SpecialType)));
        }

        return base.VisitPostfixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.BitwiseNotExpression))
        {
            var notRewritten = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
            if (_semanticModel == null
                || !_options.EnableOperatorRewrite
                || IsInCheckedContext(node)
                || IsInConstantExpressionContext(node))
            {
                return notRewritten;
            }

            var operandType = GetEffectiveType(node.Operand);
            var domain = GetUnaryDomain(operandType?.SpecialType);
            if (domain == UnsignedDomain.Native)
            {
                return notRewritten;
            }

            UsedExtensions = true;
            _stats.OperatorsRewritten++;
            return UnsignedMathSyntax.StaticCall(
                "Not" + UnsignedMathSyntax.Suffix(domain),
                UnsignedMathSyntax.Normalize(
                    notRewritten.Operand,
                    domain,
                    operandType?.SpecialType ?? SpecialType.None));
        }

        var method = node.Kind() switch
        {
            SyntaxKind.PreIncrementExpression => "Add",
            SyntaxKind.PreDecrementExpression => "Subtract",
            _ => null,
        };

        var rewritten = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
        if (method == null
            || _semanticModel == null
            || !_options.EnableOperatorRewrite
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node)
            || !IsUnsignedExpression(node.Operand)
            || !IsSafeAssignmentTarget(node.Operand))
        {
            return rewritten;
        }

        var type = GetEffectiveType(node.Operand);
        var prefixDomain = GetUnaryDomain(type?.SpecialType);
        if (prefixDomain == UnsignedDomain.Native)
        {
            return rewritten;
        }

        var call = UnsignedMathSyntax.StaticCall(
            method + UnsignedMathSyntax.Suffix(prefixDomain),
            UnsignedMathSyntax.Normalize(
                rewritten.Operand,
                prefixDomain,
                type?.SpecialType ?? SpecialType.None),
            LiteralOne());

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            rewritten.Operand,
            UnsignedMathSyntax.WrapTarget(type!.SpecialType, call));
    }

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var rewritten = (CastExpressionSyntax)base.VisitCastExpression(node)!;
        if (IsInConstantExpressionContext(node))
        {
            return FixQualifiedCastParens(rewritten);
        }

        if (_semanticModel == null || !_options.EnableCastRewrite || IsInCheckedContext(node))
        {
            return FixQualifiedCastParens(rewritten);
        }

        var sourceType = GetEffectiveType(node.Expression);
        var targetType = GetEffectiveType(node);
        var replacement = GetCastReplacement(targetType, sourceType, rewritten);
        if (replacement == null)
        {
            return FixQualifiedCastParens(rewritten);
        }

        UsedExtensions = true;
        _stats.CastsRewritten++;
        return replacement;
    }

    private static ExpressionSyntax FixQualifiedCastParens(CastExpressionSyntax rewritten)
    {
        if (rewritten.Type is not PredefinedTypeSyntax
            && rewritten.Expression is PrefixUnaryExpressionSyntax prefix
            && (prefix.IsKind(SyntaxKind.UnaryMinusExpression)
                || prefix.IsKind(SyntaxKind.UnaryPlusExpression)
                || prefix.IsKind(SyntaxKind.PointerIndirectionExpression)
                || prefix.IsKind(SyntaxKind.AddressOfExpression)))
        {
            return rewritten.WithExpression(
                SyntaxFactory.ParenthesizedExpression(rewritten.Expression));
        }

        return rewritten;
    }

    private ExpressionSyntax? GetCastReplacement(
        ITypeSymbol? targetType,
        ITypeSymbol? sourceType,
        CastExpressionSyntax rewritten)
    {
        if (targetType == null || sourceType == null)
        {
            return null;
        }

        var target = targetType.SpecialType;
        var source = sourceType.SpecialType;
        var operand = rewritten.Expression;

        if (target is SpecialType.System_Byte or SpecialType.System_UInt16)
        {
            if (source is SpecialType.System_Byte or SpecialType.System_UInt16)
            {
                return null;
            }

            var method = target == SpecialType.System_Byte
                ? (source == SpecialType.System_Int64 || source == SpecialType.System_UInt64
                    ? "ToByteInt64" : "ToByteInt32")
                : (source == SpecialType.System_Int64 || source == SpecialType.System_UInt64
                    ? "ToUInt16Int64" : "ToUInt16Int32");
            var normalized = source == SpecialType.System_UInt64
                ? UnsignedMathSyntax.UncheckedCast(SyntaxKind.LongKeyword, operand)
                : source == SpecialType.System_UInt32
                    ? UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand)
                    : operand;
            return UnsignedMathSyntax.StaticCall(method, normalized);
        }

        if (target == SpecialType.System_Int64 && source == SpecialType.System_UInt32)
        {
            return UnsignedMathSyntax.StaticCall(
                "ToInt64UInt32",
                UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand));
        }

        if (target == SpecialType.System_UInt64 && source == SpecialType.System_UInt32)
        {
            return UnsignedMathSyntax.StaticCall(
                "ToUInt64UInt32",
                UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand));
        }

        if (target == SpecialType.System_Int32 && source == SpecialType.System_UInt64)
        {
            return UnsignedMathSyntax.StaticCall(
                "ToInt32UInt64",
                UnsignedMathSyntax.UncheckedCast(SyntaxKind.LongKeyword, operand));
        }

        if (target is SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal)
        {
            if (source == SpecialType.System_UInt32)
            {
                var name = target switch
                {
                    SpecialType.System_Double => "ToDoubleUInt32",
                    SpecialType.System_Single => "ToSingleUInt32",
                    _ => "ToDecimalUInt32",
                };
                return UnsignedMathSyntax.StaticCall(
                    name,
                    UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand));
            }

            if (source == SpecialType.System_UInt64)
            {
                var name = target switch
                {
                    SpecialType.System_Double => "ToDoubleUInt64",
                    SpecialType.System_Single => "ToSingleUInt64",
                    _ => "ToDecimalUInt64",
                };
                return UnsignedMathSyntax.StaticCall(
                    name,
                    UnsignedMathSyntax.UncheckedCast(SyntaxKind.LongKeyword, operand));
            }
        }

        return null;
    }
}
```

- [ ] **Step 4: 更新 UnsignedPostfixAnnotations.cs**

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// Carried from the semantic pass to the syntax-only postfix pass so that only
/// postfix increments/decrements on verified unsigned, side-effect-free targets
/// are hoisted/rewritten. The data stores the target SpecialType name.
/// </summary>
internal static class UnsignedPostfixAnnotations
{
    private const string Kind = "Cs2jUnsignedPostfix";

    public static SyntaxAnnotation For(SpecialType target)
        => new(Kind, target.ToString());

    public static bool Has(SyntaxNode node)
        => node.GetAnnotations(Kind).Length > 0;

    public static SpecialType GetTarget(SyntaxNode node)
    {
        var annotation = node.GetAnnotations(Kind).FirstOrDefault();
        return annotation != null
            && Enum.TryParse<SpecialType>(annotation.Data, out var target)
            ? target
            : SpecialType.None;
    }
}
```

- [ ] **Step 5: 整文件替换 UnsignedPostfixRewriter.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// Syntax-only second pass. The semantic pass annotates safe unsigned postfix
/// increments/decrements; this pass hoists value-used ones into a temp and
/// rewrites them as static UnsignedMath calls.
/// </summary>
internal sealed class UnsignedPostfixRewriter : CSharpSyntaxRewriter
{
    private readonly UnsignedTypeRewriterStatistics _stats;
    private int _tempCounter;

    public bool UsedExtensions { get; private set; }

    public UnsignedPostfixRewriter(UnsignedTypeRewriterStatistics stats)
    {
        _stats = stats;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var newStatements = new List<StatementSyntax>();
        var changed = false;

        foreach (var statement in node.Statements)
        {
            var postfixes = statement.DescendantNodes()
                .OfType<PostfixUnaryExpressionSyntax>()
                .Where(UnsignedPostfixAnnotations.Has)
                .Where(p => !IsInsideNestedBlock(p, node) && !IsInsideLoopBody(p))
                .OrderBy(p => p.SpanStart)
                .ToList();

            if (postfixes.Count == 0)
            {
                newStatements.Add(statement);
                continue;
            }

            changed = true;
            var current = statement;
            var replacements = new Dictionary<PostfixUnaryExpressionSyntax, ExpressionSyntax>();
            foreach (var postfix in postfixes)
            {
                var target = UnsignedPostfixAnnotations.GetTarget(postfix);
                var domain = target switch
                {
                    SpecialType.System_Byte or SpecialType.System_UInt16 => UnsignedDomain.Int32,
                    SpecialType.System_UInt32 => UnsignedDomain.UInt32,
                    SpecialType.System_UInt64 => UnsignedDomain.UInt64,
                    _ => UnsignedDomain.Native,
                };
                if (domain == UnsignedDomain.Native)
                {
                    continue;
                }

                var method = postfix.IsKind(SyntaxKind.PostIncrementExpression) ? "Add" : "Subtract";
                var operand = postfix.Operand;
                var call = UnsignedMathSyntax.StaticCall(
                    method + UnsignedMathSyntax.Suffix(domain),
                    UnsignedMathSyntax.Normalize(operand, domain, target),
                    LiteralOne());
                var assignment = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    operand.WithoutTrivia(),
                    UnsignedMathSyntax.WrapTarget(target, call));

                if (IsStandaloneExpressionStatement(postfix))
                {
                    replacements[postfix] = assignment;
                }
                else
                {
                    var tempName = $"__cs2j_unsigned_{_tempCounter++}";
                    var tempIdentifier = SyntaxFactory.IdentifierName(tempName);
                    replacements[postfix] = tempIdentifier.WithTriviaFrom(postfix);

                    var tempDeclaration = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(tempName)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(operand.WithoutTrivia())))));

                    newStatements.Add(tempDeclaration);
                    newStatements.Add(SyntaxFactory.ExpressionStatement(assignment));
                }

                UsedExtensions = true;
                _stats.OperatorsRewritten++;
            }

            current = current.ReplaceNodes(
                replacements.Keys,
                (original, _) => replacements[original]);
            newStatements.Add(current);
        }

        return changed
            ? node.WithStatements(SyntaxFactory.List(newStatements))
            : node;
    }

    private static bool IsStandaloneExpressionStatement(PostfixUnaryExpressionSyntax node)
    {
        var parent = node.Parent;
        return parent is ExpressionStatementSyntax expressionStatement
               && expressionStatement.Expression == node;
    }

    private static bool IsInsideNestedBlock(SyntaxNode node, BlockSyntax block)
    {
        for (SyntaxNode? current = node.Parent; current != null && current != block; current = current.Parent)
        {
            if (current is BlockSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideLoopBody(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            if (current is WhileStatementSyntax whileStatement
                && whileStatement.Statement.Span.Contains(node.Span))
            {
                return true;
            }

            if (current is DoStatementSyntax doStatement
                && doStatement.Statement.Span.Contains(node.Span))
            {
                return true;
            }

            if (current is ForStatementSyntax forStatement
                && forStatement.Statement.Span.Contains(node.Span))
            {
                return true;
            }

            if (current is ForEachStatementSyntax forEachStatement
                && forEachStatement.Statement.Span.Contains(node.Span))
            {
                return true;
            }
        }

        return false;
    }

    private static ExpressionSyntax LiteralOne()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
}
```

- [ ] **Step 6: 简化 UnsignedTypeRewriter.cs（去掉 using 注入）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// C# 无符号类型预处理器公共入口。将 byte/ushort/uint/ulong 关键字替换为
/// System.* 全名，并把无符号类型上的运算符/cast 重写为
/// Cs2jUnsigned.UnsignedMath 静态调用。clean 文件字节级保真。
/// </summary>
public sealed class UnsignedTypeRewriter
{
    public UnsignedTypeRewriterResult Rewrite(
        string sourceCode,
        SemanticModel? semanticModel = null,
        UnsignedTypeRewriterOptions? options = null)
    {
        options ??= new UnsignedTypeRewriterOptions();
        var stats = new UnsignedTypeRewriterStatistics { FilesScanned = 1 };
        var diagnostics = new List<UnsignedTypeRewriterDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!UnsignedTypeSyntaxRewriter.IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new UnsignedTypeRewriterResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new UnsignedTypeSyntaxRewriter(semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        var postfixRewriter = new UnsignedPostfixRewriter(stats);
        newRoot = (CompilationUnitSyntax)postfixRewriter.Visit(newRoot)!;

        using var workspace = new AdhocWorkspace();
        newRoot = (CompilationUnitSyntax)Formatter.Format(newRoot, workspace);

        stats.FilesTransformed = 1;
        return new UnsignedTypeRewriterResult(newRoot.ToFullString(), true, diagnostics, stats);
    }
}
```

- [ ] **Step 7: 运行新测试确认通过**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~BinaryOperator_ConvertedToStaticCall|FullyQualifiedName~MixedUIntPlusInt_PromotesToInt64Domain|FullyQualifiedName~MixedUShortPlusInt_UsesInt32Domain|FullyQualifiedName~ByteIncrement_WrapsThroughToByte"
```

Expected: PASS（4 个）。若失败，检查 Formatter 输出的空格差异，用 `Assert.Contains` 宽松匹配。

- [ ] **Step 8: 提交**

```bash
git add src/CSharpToJava.Core/UnsignedTypeRewriter
git commit -m "feat: rewrite unsigned operators/casts to static UnsignedMath calls"
```

---

## Task 5: 更新单元测试全集（RED → GREEN）

**Files:**
- Replace: `tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs`

- [ ] **Step 1: 整文件替换为静态调用断言版**

```csharp
using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

public class UnsignedTypeRewriterUnitTests
{
    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("unsigned-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    [Fact]
    public void CleanFile_NoUnsignedKeywords_ReturnedUnchanged()
    {
        var src = "public class C { public int Value; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void HelperFile_IsInternalUnsignedMathWithBlockNamespace()
    {
        var content = UnsignedMathFileContent.Generate();
        Assert.Contains("namespace Cs2jUnsigned", content);
        Assert.Contains("internal static class UnsignedMath", content);
        Assert.Contains("public static int AddUInt32(int a, int b)", content);
        Assert.Contains("public static long ToInt64UInt32(int v)", content);
        Assert.DoesNotContain("public static class UnsignedExtensions", content);
    }

    [Fact]
    public void FieldDeclaration_KeywordReplacedWithFullName()
    {
        var src = "public class C { public uint Value; public byte B; public ushort S; public ulong L; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("System.UInt32 Value", result.OutputCode);
        Assert.Contains("System.Byte B", result.OutputCode);
        Assert.Contains("System.UInt16 S", result.OutputCode);
        Assert.Contains("System.UInt64 L", result.OutputCode);
    }

    [Fact]
    public void UIntAddition_UsesUInt32Domain()
    {
        var src = "public class C { public uint Add(uint a, uint b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), unchecked((int)b))", result.OutputCode);
        Assert.DoesNotContain("using Cs2jUnsigned;", result.OutputCode);
    }

    [Fact]
    public void ByteAddition_PromotesToInt32Domain()
    {
        var src = "public class C { public int Add(byte a, byte b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddInt32(a, b)", result.OutputCode);
    }

    [Fact]
    public void MixedUShortPlusInt_UsesInt32Domain()
    {
        var src = "public class C { public int M(ushort s, int i) { return s + i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddInt32(s, i)", result.OutputCode);
    }

    [Fact]
    public void MixedUIntPlusInt_PromotesToInt64Domain()
    {
        var src = "public class C { public long M(uint a, int b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.AddInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)a)), b)",
            result.OutputCode);
    }

    [Fact]
    public void MixedBytePlusLong_UsesInt64Domain()
    {
        var src = "public class C { public long M(byte b, long l) { return b + l; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddInt64(b, l)", result.OutputCode);
    }

    [Fact]
    public void MixedBytePlusUInt_UsesUInt32Domain()
    {
        var src = "public class C { public uint M(byte b, uint u) { return b + u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32(b, unchecked((int)u))", result.OutputCode);
    }

    [Fact]
    public void MixedUIntPlusULong_UsesUInt64Domain()
    {
        var src = "public class C { public ulong M(uint u, ulong ul) { return u + ul; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.AddUInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)u)), unchecked((long)ul))",
            result.OutputCode);
    }

    [Fact]
    public void ConstantOperand_UIntPlusFive_StaysUInt32Domain()
    {
        var src = "public class C { public uint M(uint u) { return u + 5; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)u), 5)", result.OutputCode);
    }

    [Fact]
    public void ConstantOperand_UIntPlusLongLiteral_UsesInt64Domain()
    {
        var src = "public class C { public long M(uint u) { return u + 5L; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.AddInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)u)), 5L)",
            result.OutputCode);
    }

    [Fact]
    public void ComparisonOperator_ConvertedToCompareInt32()
    {
        var src = "public class C { public bool Less(uint a, uint b) { return a < b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.CompareUInt32(unchecked((int)a), unchecked((int)b)) < 0",
            result.OutputCode);
    }

    [Fact]
    public void EqualityOperator_ConvertedToCompareUInt32()
    {
        var src = "public class C { public bool Eq(uint a, uint b) { return a == b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.CompareUInt32(unchecked((int)a), unchecked((int)b)) == 0",
            result.OutputCode);
    }

    [Fact]
    public void CompoundAssignment_ConvertedToStaticCall()
    {
        var src = "public class C { public void Inc(ref uint a) { a += 5; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "a = unchecked((uint)Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), 5))",
            result.OutputCode);
    }

    [Fact]
    public void ByteCompoundAssignment_WrapsThroughToByte()
    {
        var src = "public class C { public void M(ref byte b, int i) { b += i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "b = Cs2jUnsigned.UnsignedMath.ToByteInt32(Cs2jUnsigned.UnsignedMath.AddInt32(b, i))",
            result.OutputCode);
    }

    [Fact]
    public void UIntCompoundWithInt_UsesInt64Domain()
    {
        var src = "public class C { public void M(ref uint a, int i) { a += i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "a = unchecked((uint)Cs2jUnsigned.UnsignedMath.AddInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)a)), i))",
            result.OutputCode);
    }

    [Fact]
    public void PostIncrement_ConvertedToStaticCall()
    {
        var src = "public class C { public void Inc(uint a) { a++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "a = unchecked((uint)Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), 1))",
            result.OutputCode);
    }

    [Fact]
    public void ByteIncrement_WrapsThroughToByte()
    {
        var src = "public class C { public void M(byte b) { b++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "b = Cs2jUnsigned.UnsignedMath.ToByteInt32(Cs2jUnsigned.UnsignedMath.AddInt32(b, 1))",
            result.OutputCode);
    }

    [Fact]
    public void ULongShift_UsesLogicalRightShift()
    {
        var src = "public class C { public ulong M(ulong a, int n) { return a >> n; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.RightShiftUInt64(unchecked((long)a), n)",
            result.OutputCode);
    }

    [Fact]
    public void BitwiseNotByte_UsesNotInt32()
    {
        var src = "public class C { public int M(byte b) { return ~b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.NotInt32(b)", result.OutputCode);
    }

    [Fact]
    public void Cast_UIntToLong_UsesToInt64UInt32()
    {
        var src = "public class C { public long M(uint u) { return (long)u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)u))",
            result.OutputCode);
    }

    [Fact]
    public void Cast_ULongToInt_UsesToInt32UInt64()
    {
        var src = "public class C { public int M(ulong ul) { return (int)ul; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.ToInt32UInt64(unchecked((long)ul))",
            result.OutputCode);
    }

    [Fact]
    public void Cast_IntToByte_UsesToByteInt32()
    {
        var src = "public class C { public byte M(int i) { return (byte)i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToByteInt32(i)", result.OutputCode);
    }

    [Fact]
    public void Cast_UIntToDouble_UsesToDoubleUInt32()
    {
        var src = "public class C { public double M(uint u) { return (double)u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.ToDoubleUInt32(unchecked((int)u))",
            result.OutputCode);
    }

    [Fact]
    public void Cast_UIntToChar_StaysNative()
    {
        var src = "public class C { public char M(uint u) { return (char)u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("(char)u", result.OutputCode);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
    }

    [Fact]
    public void ConstFieldInitializer_OperatorsNotRewritten()
    {
        var src = "public class C { private const ulong Mask = (0x07ul | 0x06ul << 8 | 0x05ul << 16 | 0x04ul << 24 | 0x03ul << 32 | 0x02ul << 40 | 0x01ul << 48) + 1; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("const System.UInt64 Mask", result.OutputCode);
        Assert.DoesNotContain(".Or(", result.OutputCode);
        Assert.DoesNotContain(".LeftShift(", result.OutputCode);
        Assert.DoesNotContain(".Add(1)", result.OutputCode);
    }

    [Fact]
    public void ConstFieldInitializer_CastNotRewritten()
    {
        var src = "public class C { private const uint U = 0xFFFFFFFFu; private const int I = (int)U; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("const System.UInt32 U", result.OutputCode);
        Assert.DoesNotContain("U.ToInt32()", result.OutputCode);
        Assert.Contains("(int)U", result.OutputCode);
    }

    [Fact]
    public void CheckedIncrement_NotRewritten()
    {
        var src = "public class C { public void M(uint a) { checked { a++; } } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
        Assert.Contains("a++", result.OutputCode);
    }

    [Fact]
    public void Cast_NegativeLiteral_OperandParenthesized()
    {
        var src = "public class C { private const byte Invalid = unchecked((byte)-1); }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("unchecked((System.Byte)(-1))", result.OutputCode);
        Assert.DoesNotContain("(System.Byte)-1", result.OutputCode);
    }

    [Fact]
    public void Cast_PointerDereference_OperandParenthesized()
    {
        var src = "public unsafe class C { public void M(byte* pDst, char* pS) { while ((*pDst++ = (byte)*pS++) != 0) ; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("(System.Byte)(*pS++)", result.OutputCode);
        Assert.DoesNotContain("(System.Byte)*pS++", result.OutputCode);
    }

    [Fact]
    public void PropertyCompoundAssignment_NotRewritten()
    {
        var src = "public class C { public uint P { get; set; } public void M() { P += 1; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
        Assert.Contains("P += 1", result.OutputCode);
    }

    [Fact]
    public void EnumUnderlyingType_Replaced()
    {
        var src = "public enum Flag : uint { None = 0, All = 0xFFFFFFFFu }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("enum Flag : System.UInt32", result.OutputCode);
    }

    [Fact]
    public void StaticMemberAccess_Replaced()
    {
        var src = "public class C { public ulong Max() { return uint.MaxValue; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32.MaxValue", result.OutputCode);
    }

    [Fact]
    public void NullableType_Replaced()
    {
        var src = "public class C { public uint? maybe; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32? maybe", result.OutputCode);
    }
}
```

- [ ] **Step 2: 运行全套单测**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedTypeRewriterUnitTests"
```

Expected: 全部 PASS。若个别断言因 Formatter 空格/括号差异失败，以实际输出为准调整断言字符串（语义不变）。

- [ ] **Step 3: 提交**

```bash
git add tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs
git commit -m "test: update unsigned rewriter unit tests to static call shape"
```

---

## Task 6: 预处理器改为单文件 + Compile Include/Link

**Files:**
- Modify: `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs`
- Modify: `src/CSharpToJava.CLI/Program.cs`（单文件模式的辅助文件引用）
- Modify: `tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs`

- [ ] **Step 1: 写失败测试**

把 `LibProject_AllUnsignedTypes_RewrittenCorrectly` 的辅助文件断言改为：

```csharp
Assert.True(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedMath.cs")),
    "Unsigned math helper file not generated");
Assert.False(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedExtensions.cs")),
    "Old extension file must not be generated");
Assert.DoesNotContain("using Cs2jUnsigned;", opsContent);
Assert.Contains("Cs2jUnsigned.UnsignedMath", opsContent);
```

`ExplicitCompileLib_ExtensionFileAddedToProjectCompileItems` 改为：

```csharp
Assert.Contains("_Cs2jUnsignedMath.cs", csprojContent);
Assert.Contains("Link=\"_Cs2jUnsignedMath.cs\"", csprojContent);
```

`CrossDirectoryCompile_ExtensionFileAddedToCompilingProjects` 改为：

```csharp
Assert.Contains("_Cs2jUnsignedMath.cs", srcContent);
Assert.Contains("_Cs2jUnsignedMath.cs", testsContent);
Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
    "Shared helper file not generated at intermediate root");
Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "Tests", "_Cs2jUnsignedMath.cs")),
    "Helper file must not be copied into project directories");
Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "Src", "_Cs2jUnsignedMath.cs")),
    "Helper file must not be copied into project directories");
```

并新增：

```csharp
[Fact]
public async Task Rerun_DoesNotLeaveOldExtensionArtifacts()
{
    var libDir = Path.Combine(FixtureDir, "SampleUnsignedLib");
    var outputDir = Path.Combine(libDir, "..", ".test-output-rerun");

    try
    {
        for (var i = 0; i < 2; i++)
        {
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.False(Directory
                .EnumerateFiles(result.IntermediateRoot, "_Cs2jUnsignedExtensions.cs", SearchOption.AllDirectories)
                .Any(), "stale extension file after rerun");
        }
    }
    finally
    {
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~LibProject_AllUnsignedTypes_RewrittenCorrectly|FullyQualifiedName~ExplicitCompileLib|FullyQualifiedName~CrossDirectoryCompile|FullyQualifiedName~Rerun_DoesNotLeaveOldExtensionArtifacts"
```

Expected: FAIL。

- [ ] **Step 3: 修改 WriteExtensionFilesAsync 与 EnsureExtensionCompileItems**

在 `ProjectUnsignedTypePreprocessor.cs` 中把 `WriteExtensionFilesAsync` 整体替换为：

```csharp
    /// <summary>
    /// 扩展/数学辅助文件只写一份到中间根目录；每个实际编译被重写源文件的
    /// 项目通过 Compile Include + Link 引用同一物理文件。不复制本地副本。
    /// </summary>
    private static async Task WriteExtensionFilesAsync(
        string intermediateRoot,
        string sourceRoot,
        UnsignedTypeSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        var content = UnsignedMathFileContent.Generate();
        var rootPath = Path.Combine(intermediateRoot, UnsignedMathFileContent.FileName);
        Directory.CreateDirectory(intermediateRoot);
        await File.WriteAllTextAsync(rootPath, content, new System.Text.UTF8Encoding(false));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            layout, exclusionInfo, rewrittenFiles, projectGraph);
        EnsureExtensionCompileItems(
            sourceRoot, intermediateRoot, layout, exclusionInfo, hostProjectDirectories);
    }
```

并把 `EnsureExtensionCompileItems` 及其调用链替换为：

```csharp
    private static void EnsureExtensionCompileItems(
        string sourceRoot,
        string intermediateRoot,
        UnsignedTypeSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories)
    {
        var sharedFilePath = Path.Combine(intermediateRoot, UnsignedMathFileContent.FileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
            {
                continue;
            }

            var destinationProjectFile = Path.Combine(
                intermediateRoot,
                Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionCompileItem(
                destinationProjectFile,
                UnsignedMathFileContent.FileName,
                projectDirectory,
                sharedFilePath);
        }
    }

    private static void EnsureExtensionCompileItem(
        string projectFilePath,
        string fileName,
        string projectDirectory,
        string sharedFilePath)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFullPath(projectDirectory).TrimEnd('\\', '/'),
                Path.GetFullPath(Path.GetDirectoryName(sharedFilePath)!).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            // 项目根目录就是辅助文件所在根目录且使用 SDK 默认 glob：
            // 文件已被默认编译项包含，追加显式条目会造成重复编译。
            return;
        }

        var normalizedFileName = fileName.Replace('\\', '/');
        foreach (var compileElement in root.Descendants().Where(e => e.Name.LocalName == "Compile").ToList())
        {
            var include = (string?)compileElement.Attribute("Include");
            var link = (string?)compileElement.Attribute("Link");
            if (MatchesHelperFile(include, normalizedFileName)
                || MatchesHelperFile(link, normalizedFileName))
            {
                compileElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, sharedFilePath);
        var compileItem = new XElement(
            "Compile",
            new XAttribute("Include", relativeInclude),
            new XAttribute("Link", fileName));
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup", compileItem),
            new XText(Environment.NewLine));

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool MatchesHelperFile(string? value, string normalizedFileName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value!.Replace('\\', '/');
        return normalized.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("/" + normalizedFileName, StringComparison.OrdinalIgnoreCase);
    }
```

把文件内所有 `UnsignedExtensionsFileContent` 替换为 `UnsignedMathFileContent`（含 `Program.cs` 单文件模式）。

- [ ] **Step 4: 运行确认通过**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~LibProject_AllUnsignedTypes_RewrittenCorrectly|FullyQualifiedName~ExplicitCompileLib|FullyQualifiedName~CrossDirectoryCompile|FullyQualifiedName~Rerun_DoesNotLeaveOldExtensionArtifacts"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs src/CSharpToJava.CLI/Program.cs tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs
git commit -m "feat: preprocessor emits single shared unsigned math helper with Compile Include links"
```

---

## Task 7: 语义等价测试与 fixture

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/UnsignedTypeSemanticsTests.cs`
- Modify: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedSemantics.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedMixedBoundaries.cs`

- [ ] **Step 1: 新增 fixture（混合运算与边界）**

```csharp
using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedMixedBoundaries
{
    public static string Run()
    {
        var sb = new StringBuilder();

        ushort s = 65535;
        int i = 1;
        sb.AppendLine((s + i).ToString());

        uint u = 0xFFFFFFFFu;
        sb.AppendLine((u + i).ToString());
        sb.AppendLine((u + 5L).ToString());
        sb.AppendLine((u + 5u).ToString());

        byte b = 255;
        sb.AppendLine((b + i).ToString());
        sb.AppendLine((b + u).ToString());
        sb.AppendLine((b + 1L).ToString());

        ulong ul = 0xFFFFFFFFFFFFFFFFul;
        sb.AppendLine((ul + 5ul).ToString());
        sb.AppendLine((ul + u).ToString());

        sb.AppendLine((u == -1) ? "eq" : "ne");
        sb.AppendLine((u < 0x80000000L) ? "lt" : "ge");
        sb.AppendLine(((int)ul).ToString("X"));
        sb.AppendLine(((long)u).ToString());
        sb.AppendLine(((ulong)u).ToString("X"));
        sb.AppendLine(((byte)u).ToString("X"));
        sb.AppendLine(((double)u).ToString("R"));

        b++;
        s--;
        u += i;
        ul += i;
        sb.AppendLine(b.ToString());
        sb.AppendLine(s.ToString());
        sb.AppendLine(u.ToString("X"));
        sb.AppendLine(ul.ToString("X"));

        return sb.ToString();
    }
}
```

并更新 `UnsignedSemantics.cs` 的 `RunAll`，在 `mixed` 之后追加：

```csharp
AppendSection(sb, "mixedBoundaries", UnsignedMixedBoundaries.Run());
```

- [ ] **Step 2: 更新语义测试的辅助文件引用**

`UnsignedTypeSemanticsTests.cs` 中 `rewrittenSources.Add(UnsignedExtensionsFileContent.Generate());`
改为 `rewrittenSources.Add(UnsignedMathFileContent.Generate());`。

- [ ] **Step 3: 运行语义测试**

```bash
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedTypeSemanticsTests"
```

Expected: PASS（原始与改写后运行结果一致）。

- [ ] **Step 4: 提交**

```bash
git add tests/CSharpToJava.ProjectTests/UnsignedTypeSemanticsTests.cs tests/SampleUnsignedSolution/SampleUnsignedLib
git commit -m "test: add mixed unsigned boundary semantics fixture"
```

---

## Task 8: 仓库全量测试

**Files:** 无

- [ ] **Step 1: 运行仓库测试**

```bash
dotnet test -c Release D:\code\cs2j\CSharpToJavaConverter.slnx *> .scratch-a3\repo-test-release.log; $code = $LASTEXITCODE; Write-Output "EXIT_CODE=$code"; exit $code
```

Expected: EXIT_CODE=0。失败则回到对应任务修复（重点看 `UnsignedType*` 相关失败）。

- [ ] **Step 2: 提交剩余修复**

```bash
git add -A
git commit -m "fix: resolve remaining unsigned-type regressions"
```

（无改动则跳过本步。）

---

## Task 9: 端到端验收

**Files:** 无（产物在仓库外）

- [ ] **Step 1: 重新转换目标 SDK 项目**

```bash
dotnet run -c Release --project src\CSharpToJava.CLI -- unsigned-type -s D:\csharp-sdk -d D:\unsigned-81519
```

Expected: 退出码 0；统计行 `unsigned-type: ... transformed ... types replaced ... operators rewritten`；产物位于 `D:\unsigned-81519\.cs2j-unsigned-src`，根目录只有一份 `_Cs2jUnsignedMath.cs`。

- [ ] **Step 2: 检查产物形态**

```bash
Get-ChildItem D:\unsigned-81519\.cs2j-unsigned-src -Recurse -Filter _Cs2jUnsignedMath.cs | Select-Object FullName
Get-ChildItem D:\unsigned-81519\.cs2j-unsigned-src -Recurse -Filter _Cs2jUnsignedExtensions.cs | Select-Object FullName
```

Expected: 只有一个 `_Cs2jUnsignedMath.cs`（中间根目录）；`_Cs2jUnsignedExtensions.cs` 不存在。

- [ ] **Step 3: 中间解决方案 Release 测试**

```bash
dotnet test -c Release D:\unsigned-81519\.cs2j-unsigned-src\csharp-sdk.sln *> D:\unsigned-81519\dotnet-test-release.log; $code = $LASTEXITCODE; Write-Output "EXIT_CODE=$code"; exit $code
```

Expected: EXIT_CODE=0；日志中 7 个测试项目全部 `已通过!`，无 `error CS`。

- [ ] **Step 4: 仓库自身 Release 测试**

```bash
dotnet test -c Release D:\code\cs2j\CSharpToJavaConverter.slnx
```

Expected: 退出码 0。

- [ ] **Step 5: 汇总验收证据**

记录：转换统计、中间测试各项目通过数、仓库测试通过数。若失败，按 systematic-debugging 定位修复后重跑 Task 9。

---

## 自审

- 规格 §5 方法目录 ↔ Task 3 辅助文件：全部方法名一一对应（含 `ToUInt64UInt32`）。
- 规格 §6 规则 ↔ Task 4 重写器：域归类、混合归一化、常量操作数、checked/unchecked、常量上下文、原生转换均覆盖。
- 规格 §7 管线 ↔ Task 6：单文件、Compile Include/Link、去重、根目录默认 glob、重复运行重建。
- 规格 §8 验证 ↔ Task 5/7/8/9：单测、项目测试、语义测试、仓库测试、端到端验收。
- 规格 §9 TypeMappings 契约：本阶段只保证方法名唯一（不在配置中实现）。
- 规格 §10 checked：辅助类内部 unchecked、插入转换 unchecked。
