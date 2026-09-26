# Delegate Preprocessor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增预处理阶段 delegate，把 C# 委托组合/拆解运算符 `+`/`-`/`+=`/`-=` 降级为 `CsPrelude.Combine`/`CsPrelude.Remove` 普通方法调用，把匿名方法 `delegate(...){}` 降级为 lambda，使下游 C#→Java 转换器无需处理委托运算符。

**Architecture:** 单遍 `CSharpSyntaxRewriter` 遍历，基于 Roslyn `SemanticModel` 判定操作数是否为委托类型。引入受控 shim 类 `CsPrelude`（嵌入资源分发 + ProjectReference 注入，镜像 `Cs2jArray`/`Cs2jUnsignedMath` 机制）。改写产物不调用 `Formatter.Format`，用 `ToFullString()` 保留 trivia 以最小化差异。`Changed` 标志保证"脏但无需改写"的文件逐字节保真。

**Tech Stack:** C# / .NET 10、Roslyn `Microsoft.CodeAnalysis.CSharp`（`allowUnsafe: true`）、xUnit、CommandLineParser verbs；与 [NullCoalesceRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceRewriter.cs)、[ProjectNullCoalescePreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectNullCoalescePreprocessor.cs) 同构。

> 规范来源：[2026-09-26-delegate-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-26-delegate-preprocessor-design.md)。

---

## 0. 全局约定（执行每个任务前先读）

- 仓库根：`d:\code\cs2j`。所有路径相对仓库根。PowerShell 不支持 `&&`，串行命令用 `;`。
- 命名空间：Core 全部放 `CSharpToJava.Core.DelegatePreprocessor`；CLI 类放 `CSharpToJava.CLI`（与既有预处理器一致，internal）。
- C# 侧支撑库：namespace `Cs2jPrelude`，class `CsPrelude`，项目名 `Cs2jPrelude.csproj`。
- 入口必须是**实例成员**（`sealed class DelegatePreprocessor` + 实例方法 `Preprocess`），因为目录名与类名同为 `DelegatePreprocessor`，静态调用会触发 CS0234（与 C1 同类问题）。
- 调用点拼写固定为 `CsPrelude.Combine(...)` / `CsPrelude.Remove(...)`（全限定，无 using 依赖），与 `Cs2jArray.ArrayHelper.XXX(...)` 同构。
- 每个任务结束必须 `dotnet build` 全绿再提交；提交信息用 `feat(delegate): ...` / `test(delegate): ...`。
- TDD 节奏：先写失败测试 → 跑确认失败 → 最小实现 → 跑确认通过 → 提交。
- `CSharpToJava.CLI.csproj` 已对 `CSharpToJava.Tests` 配 `InternalsVisibleTo`，internal 类型可直接在 Tests 项目里测。

### 0.1 File Structure（本计划创建/修改的全部文件）

**Core（新建目录 `src/CSharpToJava.Core/DelegatePreprocessor/`）：**

| 文件 | 职责 |
|---:|---|
| `DelegatePreprocessorOptions.cs` | record（Verbose / Strict） |
| `DelegatePreprocessorStatistics.cs` | 统计模型 |
| `DelegatePreprocessorDiagnostic.cs` | Severity enum + record |
| `DelegatePreprocessorResult.cs` | 结果 record |
| `DelegatePreprocessor.cs` | 公共入口 `Preprocess(sourceCode, semanticModel, options)` → `DelegatePreprocessorResult`；脏检查、改写、后置自检 |
| `DelegateSyntaxRewriter.cs` | `CSharpSyntaxRewriter` 主体：二元 `+`/`-`、复合赋值 `+=`/`-=`、匿名方法→lambda |

**C# 侧支撑库（新建目录 `src/Cs2jPrelude/`）：**

| 文件 | 职责 |
|---:|---|
| `src/Cs2jPrelude/CsPrelude.cs` | `public static class CsPrelude`，namespace `Cs2jPrelude`；`Combine<T>` / `Remove<T>` 泛型方法 |
| `src/Cs2jPrelude/Cs2jPrelude.csproj` | net10.0 / LangVersion latest / Nullable disable / ImplicitUsings disable |

**CLI：**

| 文件 | 职责 |
|---:|---|
| `src/CSharpToJava.CLI/ProjectDelegatePreprocessor.cs` | 新建：项目模式预处理器（mirror `ProjectNullCoalescePreprocessor`）；`IntermediateDirectoryName = ".cs2j-delegate-src"` |
| `src/CSharpToJava.CLI/PreludeSupportFiles.cs` | 新建：嵌入资源读取/释放（mirror `ArrayHelperSupportFiles`） |
| `src/CSharpToJava.CLI/Cs2jPreludeProject.cs` | 新建：按 csproj 文件名精确识别 `Cs2jPrelude.csproj`（mirror `Cs2jArrayProject`） |
| `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj` | 改：嵌入 `Cs2jPrelude` 的两个文件 |
| `src/CSharpToJava.CLI/Program.cs` | 改：新增 `delegate-preprocess` verb + `DelegatePreprocessVerbOptions`、`ConvertProjectOptions` 开关、`ConvertProject` 执行块、`GetPreservedDestinationDirectories`、指纹行 |

**配置：**

| 文件 | 职责 |
|---:|---|
| `config/TypeMappings.json` | 改：新增 `Cs2jPrelude.CsPrelude` 类型映射 + `Combine`/`Remove` 方法映射 |
| `.gitignore` | 改：补齐 `.cs2j-delegate-src/` 及历史遗漏的中间目录 |

**Tests：**

| 文件 | 职责 |
|---:|---|
| `tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs` | 新建：改写器单元（二元/复合赋值/匿名方法/负向/保真/幂等） |
| `tests/CSharpToJava.Tests/ProjectDelegatePreprocessorTests.cs` | 新建：项目级 + 中间树 + build 验证 |

---

## Task 1: 创建支撑库 Cs2jPrelude（CsPrelude.cs + Cs2jPrelude.csproj）

**Files:**
- Create: `src/Cs2jPrelude/CsPrelude.cs`
- Create: `src/Cs2jPrelude/Cs2jPrelude.csproj`

- [ ] **Step 1: 创建 `src/Cs2jPrelude/Cs2jPrelude.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: 创建 `src/Cs2jPrelude/CsPrelude.cs`**

```csharp
// Cs2jPrelude.CsPrelude：委托组合/拆解辅助类（唯一真源）。
// 本文件既是本库项目的源码，又被 CLI 以 EmbeddedResource 嵌入分发：
// delegate 预处理把它释放到中间树 Cs2jPrelude/ 下，主转换器
// 跳过该项目的转换，调用点经 TypeMappings 映射到 compat 手写类
// io.github.ningpp.compat.DelegateHelper。
using System;

namespace Cs2jPrelude
{
    public static class CsPrelude
    {
        public static T Combine<T>(T a, T b) where T : Delegate
            => (T)Delegate.Combine(a, b);

        public static T Combine<T>(params T[] delegates) where T : Delegate
            => (T)Delegate.Combine(delegates);

        public static T Remove<T>(T a, T b) where T : Delegate
            => (T)Delegate.Remove(a, b);
    }
}
```

- [ ] **Step 3: 验证支撑库可独立编译**

Run:
```powershell
cd d:\code\cs2j\src\Cs2jPrelude; dotnet build -c Release
```
Expected: Build 成功，无 Error。

- [ ] **Step 4: 提交**

```powershell
cd d:\code\cs2j; git add src/Cs2jPrelude/CsPrelude.cs src/Cs2jPrelude/Cs2jPrelude.csproj; git commit -m "feat(delegate): add Cs2jPrelude support library with CsPrelude shim"
```

---

## Task 2: CLI 嵌入资源 + 支撑文件读取/释放

**Files:**
- Create: `src/CSharpToJava.CLI/PreludeSupportFiles.cs`
- Create: `src/CSharpToJava.CLI/Cs2jPreludeProject.cs`
- Modify: `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`

- [ ] **Step 1: 创建 `src/CSharpToJava.CLI/Cs2jPreludeProject.cs`**

```csharp
namespace CSharpToJava.CLI;

/// <summary>
/// 按 csproj 文件名精确识别 cs2j 内部支撑库 Cs2jPrelude
/// （与 Cs2jArrayProject 同款保留项目名契约）。
/// </summary>
internal static class Cs2jPreludeProject
{
    public const string ProjectFileName = "Cs2jPrelude.csproj";

    public static bool IsCs2jPreludeProject(string? projectFilePath)
        => projectFilePath != null
           && Path.GetFileName(projectFilePath).Equals(ProjectFileName, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: 创建 `src/CSharpToJava.CLI/PreludeSupportFiles.cs`**

```csharp
namespace CSharpToJava.CLI;

/// <summary>
/// Cs2jPrelude 支撑文件的嵌入资源读取/释放。真源是
/// src/Cs2jPrelude/ 下的两个文件（构建期嵌入 CLI 程序集），结构上
/// 不存在第二份副本可漂移。单文件模式（delegate-preprocess -i/-o）沿用
/// _Cs2j* 支撑文件前缀契约，文件名与项目模式的 CsPrelude.cs 不同名是有意的。
/// </summary>
internal static class PreludeSupportFiles
{
    public const string LibDirectoryName = "Cs2jPrelude";
    public const string SingleFileHelperFileName = "_Cs2jPrelude.cs";

    private const string SourceResourceName = "Cs2jPrelude.CsPrelude.cs";
    private const string ProjectResourceName = "Cs2jPrelude.Cs2jPrelude.csproj";

    public static string GetCsPreludeFileContent() => ReadResource(SourceResourceName);

    public static string GetProjectFileContent() => ReadResource(ProjectResourceName);

    /// <summary>把库项目（csproj + 源码）释放到目标目录（UTF-8 无 BOM）。</summary>
    public static async Task ReleaseAsync(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "CsPrelude.cs"),
            GetCsPreludeFileContent(),
            new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "Cs2jPrelude.csproj"),
            GetProjectFileContent(),
            new System.Text.UTF8Encoding(false));
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(PreludeSupportFiles).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "The CLI project must embed src/Cs2jPrelude/* with explicit LogicalName.");
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 3: 修改 `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`，在 `Cs2jArray` ItemGroup 之后追加嵌入资源**

在文件中找到 `</ItemGroup>` （Cs2jArray 块的结尾，第 33 行），在其后追加：

```xml
  <ItemGroup>
    <!-- Cs2jPrelude 支撑库（唯一真源在 src/Cs2jPrelude/，此处嵌入分发） -->
    <EmbeddedResource Include="..\Cs2jPrelude\CsPrelude.cs">
      <Link>support\Cs2jPrelude\CsPrelude.cs</Link>
      <LogicalName>Cs2jPrelude.CsPrelude.cs</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="..\Cs2jPrelude\Cs2jPrelude.csproj">
      <Link>support\Cs2jPrelude\Cs2jPrelude.csproj</Link>
      <LogicalName>Cs2jPrelude.Cs2jPrelude.csproj</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
```

- [ ] **Step 4: 验证 CLI 项目编译通过**

Run:
```powershell
cd d:\code\cs2j\src\CSharpToJava.CLI; dotnet build -c Release
```
Expected: Build 成功，无 Error。嵌入资源在编译产物中可用。

- [ ] **Step 5: 提交**

```powershell
cd d:\code\cs2j; git add src/CSharpToJava.CLI/PreludeSupportFiles.cs src/CSharpToJava.CLI/Cs2jPreludeProject.cs src/CSharpToJava.CLI/CSharpToJava.CLI.csproj; git commit -m "feat(delegate): add PreludeSupportFiles embedded resource and Cs2jPreludeProject identifier"
```

---

## Task 3: Core 数据模型（Options / Statistics / Diagnostic / Result）

**Files:**
- Create: `src/CSharpToJava.Core/DelegatePreprocessor/DelegatePreprocessorOptions.cs`
- Create: `src/CSharpToJava.Core/DelegatePreprocessor/DelegatePreprocessorStatistics.cs`
- Create: `src/CSharpToJava.Core/DelegatePreprocessor/DelegatePreprocessorDiagnostic.cs`
- Create: `src/CSharpToJava.Core/DelegatePreprocessor/DelegatePreprocessorResult.cs`

- [ ] **Step 1: 创建 `DelegatePreprocessorOptions.cs`**

```csharp
namespace CSharpToJava.Core.DelegatePreprocessor;

/// <param name="Verbose">If true, emit info-level diagnostics.</param>
/// <param name="Strict">If true, residual delegate operators produce Error instead of Warning.</param>
public sealed record DelegatePreprocessorOptions(
    bool Verbose = false,
    bool Strict = false);
```

- [ ] **Step 2: 创建 `DelegatePreprocessorStatistics.cs`**

```csharp
namespace CSharpToJava.Core.DelegatePreprocessor;

public sealed class DelegatePreprocessorStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int CombineRewritten;          // d1 + d2 → Combine(d1, d2)
    public int RemoveRewritten;           // d1 - d2 → Remove(d1, d2)
    public int CompoundAssignRewritten;   // d += h / d -= h
    public int AnonymousMethodRewritten;  // delegate(...) {...} → lambda
    public int SkippedUnsupported;        // unsupported scenario skip count
    public int ResidualCount;             // post-self-check residual count
}
```

- [ ] **Step 3: 创建 `DelegatePreprocessorDiagnostic.cs`**

```csharp
namespace CSharpToJava.Core.DelegatePreprocessor;

public enum DelegatePreprocessorSeverity { Warning, Error }

public sealed record DelegatePreprocessorDiagnostic(
    DelegatePreprocessorSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0);
```

- [ ] **Step 4: 创建 `DelegatePreprocessorResult.cs`**

```csharp
namespace CSharpToJava.Core.DelegatePreprocessor;

public sealed record DelegatePreprocessorResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<DelegatePreprocessorDiagnostic> Diagnostics,
    DelegatePreprocessorStatistics Statistics);
```

- [ ] **Step 5: 验证 Core 项目编译通过**

Run:
```powershell
cd d:\code\cs2j\src\CSharpToJava.Core; dotnet build -c Release
```
Expected: Build 成功。

- [ ] **Step 6: 提交**

```powershell
cd d:\code\cs2j; git add src/CSharpToJava.Core/DelegatePreprocessor/; git commit -m "feat(delegate): add core data models (Options/Statistics/Diagnostic/Result)"
```

---

## Task 4: DelegateSyntaxRewriter — 二元 + / - 改写

**Files:**
- Create: `src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs`
- Test: `tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs`

- [ ] **Step 1: 创建测试文件并写第一个失败测试**

Create `tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs`:

```csharp
using CSharpToJava.Core.DelegatePreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class DelegatePreprocessorUnitTests
{
    private static DelegatePreprocessorResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("delegate-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Delegate).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Action).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new DelegatePreprocessor().Preprocess(source, model);
    }

    [Fact]
    public void Clean_File_Is_Byte_Level_Preserved()
    {
        var src = """
        class C {
            void M() {
                var a = "hello";
                System.Console.WriteLine(a);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Delegate_Combine_Two_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2) {
                Action d = d1 + d2;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine(d1, d2)", result.OutputCode);
        Assert.DoesNotContain("d1 + d2", result.OutputCode);
    }

    [Fact]
    public void Delegate_Remove_Two_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2) {
                Action d = d1 - d2;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Remove(d1, d2)", result.OutputCode);
        Assert.DoesNotContain("d1 - d2", result.OutputCode);
    }

    [Fact]
    public void Numeric_Add_Not_Rewritten()
    {
        var src = """
        class C {
            void M() {
                int x = 1 + 2;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "DelegatePreprocessorUnitTests" -v n
```
Expected: FAIL — `DelegatePreprocessor` 类型不存在。

- [ ] **Step 3: 创建 `DelegateSyntaxRewriter.cs` — 二元 + / - 改写**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DelegatePreprocessor;

/// <summary>
/// Single-pass CSharpSyntaxRewriter. Rewrites delegate + / - to
/// CsPrelude.Combine / CsPrelude.Remove, and anonymous methods to lambdas.
/// Only queries SemanticModel on original (pre-rewrite) nodes.
/// </summary>
internal sealed class DelegateSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly DelegatePreprocessorStatistics _stats;
    private readonly List<DelegatePreprocessorDiagnostic> _diagnostics;
    private readonly DelegatePreprocessorOptions _options;

    public bool Changed { get; private set; }

    public DelegateSyntaxRewriter(
        SemanticModel? semanticModel,
        DelegatePreprocessorOptions options,
        DelegatePreprocessorStatistics stats,
        List<DelegatePreprocessorDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        // Visit children first (bottom-up). CSharpSyntaxRewriter default
        // visits children first, but we call base to be explicit.
        var visited = base.VisitBinaryExpression(node);
        if (visited is not BinaryExpressionSyntax binary)
            return visited;

        // Only handle + and -
        if (!binary.IsKind(SyntaxKind.AddExpression) && !binary.IsKind(SyntaxKind.SubtractExpression))
            return binary;

        // Need semantic model to determine if operands are delegate types
        if (_semanticModel == null)
            return binary;

        // Check if either operand is a delegate type (use ORIGINAL node for semantic query)
        var leftType = _semanticModel.GetTypeInfo(node.Left).Type;
        var rightType = _semanticModel.GetTypeInfo(node.Right).Type;

        bool leftIsDelegate = IsDelegateType(leftType);
        bool rightIsDelegate = IsDelegateType(rightType);

        if (!leftIsDelegate && !rightIsDelegate)
            return binary; // numeric + / -, not our concern

        // Flatten left-nested AddExpression chains for Combine
        if (binary.IsKind(SyntaxKind.AddExpression))
        {
            var operands = new List<ExpressionSyntax>();
            CollectAddOperands(node, operands);

            if (operands.Count >= 3)
            {
                // Use params overload: Combine(a, b, c)
                _stats.CombineRewritten++;
                Changed = true;
                return CreateCombineCall(operands)
                    .WithTriviaFrom(node);
            }
        }

        // Two-operand case
        var left = (ExpressionSyntax)binary.Left;
        var right = (ExpressionSyntax)binary.Right;

        if (binary.IsKind(SyntaxKind.AddExpression))
        {
            _stats.CombineRewritten++;
            Changed = true;
            return CreateCombineCall(left, right)
                .WithTriviaFrom(node);
        }
        else
        {
            _stats.RemoveRewritten++;
            Changed = true;
            return CreateRemoveCall(left, right)
                .WithTriviaFrom(node);
        }
    }

    private static bool IsDelegateType(ITypeSymbol? type)
    {
        if (type == null) return false;
        return type.TypeKind == TypeKind.Delegate;
    }

    private void CollectAddOperands(BinaryExpressionSyntax node, List<ExpressionSyntax> operands)
    {
        if (node.Left is BinaryExpressionSyntax leftBinary
            && leftBinary.IsKind(SyntaxKind.AddExpression))
        {
            CollectAddOperands(leftBinary, operands);
        }
        else
        {
            operands.Add(node.Left);
        }
        operands.Add(node.Right);
    }

    private static ExpressionSyntax CreateCombineCall(params ExpressionSyntax[] args)
    {
        var argList = SyntaxFactory.SeparatedList(
            args.Select(a => SyntaxFactory.Argument(a)));
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("CsPrelude"),
                SyntaxFactory.IdentifierName("Combine")),
            SyntaxFactory.ArgumentList(argList));
    }

    private static ExpressionSyntax CreateRemoveCall(ExpressionSyntax a, ExpressionSyntax b)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("CsPrelude"),
                SyntaxFactory.IdentifierName("Remove")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(a),
                    SyntaxFactory.Argument(b)
                })));
    }

    // Placeholder for assignment/anonymous method handling — added in later tasks
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
        => base.VisitAssignmentExpression(node);

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        => base.VisitAnonymousMethodExpression(node);
}
```

- [ ] **Step 4: 创建 `DelegatePreprocessor.cs` 入口（最小实现，只接二元改写）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DelegatePreprocessor;

/// <summary>
/// C# delegate preprocessor public entry point. Rewrites delegate + / - / += / -=
/// to CsPrelude.Combine / CsPrelude.Remove, and anonymous methods to lambdas.
/// Clean files are byte-level preserved. Rewritten output is valid C#.
/// </summary>
public sealed class DelegatePreprocessor
{
    public DelegatePreprocessorResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        DelegatePreprocessorOptions? options = null)
    {
        options ??= new DelegatePreprocessorOptions();
        var stats = new DelegatePreprocessorStatistics { FilesScanned = 1 };
        var diagnostics = new List<DelegatePreprocessorDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Dirty check: no + / - / += / -= / delegate anonymous → byte-level copy
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new DelegatePreprocessorResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new DelegateSyntaxRewriter(
            semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            // Dirty but nothing to rewrite (e.g. numeric arithmetic only)
            stats.FilesSkippedClean = 1;
            return new DelegatePreprocessorResult(sourceCode, false, diagnostics, stats);
        }

        // No Formatter.Format — use ToFullString() to minimize trivia disruption
        var code = newRoot.ToFullString();
        stats.FilesTransformed = 1;

        // Post-check: residual anonymous methods
        var checkRoot = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        if (checkRoot.DescendantNodes().Any(n => n is AnonymousMethodExpressionSyntax))
        {
            stats.ResidualCount = checkRoot.DescendantNodes()
                .Count(n => n is AnonymousMethodExpressionSyntax);
            var severity = options.Strict
                ? DelegatePreprocessorSeverity.Error
                : DelegatePreprocessorSeverity.Warning;
            diagnostics.Add(new DelegatePreprocessorDiagnostic(
                severity,
                $"{stats.ResidualCount} residual anonymous method(s) remaining after preprocessing."));
        }

        return new DelegatePreprocessorResult(code, true, diagnostics, stats);
    }

    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            n is AnonymousMethodExpressionSyntax
            || (n is BinaryExpressionSyntax b &&
                (b.IsKind(SyntaxKind.AddExpression) || b.IsKind(SyntaxKind.SubtractExpression)))
            || (n is AssignmentExpressionSyntax a &&
                (a.IsKind(SyntaxKind.AddAssignmentExpression)
                 || a.IsKind(SyntaxKind.SubtractAssignmentExpression))));
}
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "DelegatePreprocessorUnitTests" -v n
```
Expected: PASS — 全部 4 个测试通过。

- [ ] **Step 6: 提交**

```powershell
cd d:\code\cs2j; git add src/CSharpToJava.Core/DelegatePreprocessor/ tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs; git commit -m "feat(delegate): add DelegateSyntaxRewriter with binary +/- rewrite and entry point"
```

---

## Task 5: 复合赋值 += / -= 改写

**Files:**
- Modify: `src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs`
- Modify: `tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs`

- [ ] **Step 1: 追加失败测试**

在 `DelegatePreprocessorUnitTests.cs` 中追加：

```csharp
    [Fact]
    public void Delegate_CompoundAdd_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d, Action h) {
                d += h;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("d = CsPrelude.Combine(d, h)", result.OutputCode);
        Assert.DoesNotContain("d += h", result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundSubtract_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d, Action h) {
                d -= h;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("d = CsPrelude.Remove(d, h)", result.OutputCode);
        Assert.DoesNotContain("d -= h", result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundAdd_Field_Rewritten()
    {
        var src = """
        using System;
        class C {
            Action _field;
            void M(Action h) {
                _field += h;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("_field = CsPrelude.Combine(_field, h)", result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundAdd_Property_Rewritten()
    {
        var src = """
        using System;
        class C {
            Action Prop { get; set; }
            void M(Action h) {
                Prop += h;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("Prop = CsPrelude.Combine(Prop, h)", result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundAdd_UnstableLeft_Skipped()
    {
        var src = """
        using System;
        class C {
            void M(Action[] arr, int i, Action h) {
                arr[i++] += h;
            }
        }
        """;
        var result = Preprocess(src);
        // Should not rewrite — arr[i++] is not a stable reference
        Assert.Contains("arr[i++] += h", result.OutputCode);
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "DelegatePreprocessorUnitTests" -v n
```
Expected: FAIL — 复合赋值测试失败（未被改写）。

- [ ] **Step 3: 修改 `DelegateSyntaxRewriter.cs`，替换 `VisitAssignmentExpression` 实现**

将 `DelegateSyntaxRewriter.cs` 中的 `VisitAssignmentExpression` 占位实现替换为：

```csharp
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visited = base.VisitAssignmentExpression(node);
        if (visited is not AssignmentExpressionSyntax assignment)
            return visited;

        if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)
            && !assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
            return assignment;

        if (_semanticModel == null)
            return assignment;

        // Check if left operand is a delegate type (query original node)
        var leftType = _semanticModel.GetTypeInfo(node.Left).Type;
        if (!IsDelegateType(leftType))
            return assignment; // numeric += / -=, not our concern

        // Check if left is an event — skip events
        var leftSymbol = _semanticModel.GetSymbolInfo(node.Left).Symbol;
        if (leftSymbol is IEventSymbol)
            return assignment;

        // Stable reference check: only rewrite if left is a simple identifier
        // or a member access without side effects in the receiver
        if (!IsStableLeftValue(node.Left))
        {
            _stats.SkippedUnsupported++;
            _diagnostics.Add(new DelegatePreprocessorDiagnostic(
                DelegatePreprocessorSeverity.Warning,
                $"Compound assignment on non-simple left value is not supported; skipping rewrite."));
            return assignment;
        }

        var left = (ExpressionSyntax)assignment.Left;
        var right = (ExpressionSyntax)assignment.Right;

        ExpressionSyntax newRight;
        if (assignment.IsKind(SyntaxKind.AddAssignmentExpression))
        {
            _stats.CompoundAssignRewritten++;
            newRight = CreateCombineCall(left, right);
        }
        else
        {
            _stats.CompoundAssignRewritten++;
            newRight = CreateRemoveCall(left, right);
        }

        Changed = true;
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            left,
            SyntaxFactory.Token(SyntaxKind.EqualsToken)
                .WithTriviaFrom(assignment.OperatorToken),
            newRight)
            .WithTriviaFrom(node);
    }

    /// <summary>
    /// A left value is "stable" (safe to duplicate) if it is an identifier
    /// or a member access whose receiver contains no invocations or
    /// prefix/postfix unary operators.
    /// </summary>
    private static bool IsStableLeftValue(ExpressionSyntax left)
    {
        // Strip parentheses
        while (left is ParenthesizedExpressionSyntax p)
            left = p.Expression;

        if (left is IdentifierNameSyntax)
            return true;

        if (left is MemberAccessExpressionSyntax member)
        {
            // Check receiver for side effects
            var receiver = member.Expression;
            while (receiver is MemberAccessExpressionSyntax ma)
                receiver = ma.Expression;
            // Receiver must not contain invocations or ++/--
            return !receiver.DescendantNodesAndSelf()
                .Any(n => n is InvocationExpressionSyntax
                       || n is PrefixUnaryExpressionSyntax
                       || n is PostfixUnaryExpressionSyntax);
        }

        return false;
    }
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "DelegatePreprocessorUnitTests" -v n
```
Expected: PASS — 全部测试通过。

- [ ] **Step 5: 提交**

```powershell
cd d:\code\cs2j; git add src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs; git commit -m "feat(delegate): add compound assignment += / -= rewrite with stable left value check"
```

---

## Task 6: 嵌套 + 多组合扁平化

**Files:**
- Modify: `tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs`

- [ ] **Step 1: 追加失败测试**

在 `DelegatePreprocessorUnitTests.cs` 中追加：

```csharp
    [Fact]
    public void Delegate_Multi_Combine_Three_Uses_Params()
    {
        var src = """
        using System;
        class C {
            void M(Action a, Action b, Action c) {
                Action d = a + b + c;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine(a, b, c)", result.OutputCode);
    }

    [Fact]
    public void Delegate_Nested_Combine_Remove()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2, Action d3) {
                Action d = (d1 + d2) - d3;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Remove(CsPrelude.Combine(d1, d2), d3)", result.OutputCode);
    }

    [Fact]
    public void Delegate_Right_Nested_Not_Flattened()
    {
        var src = """
        using System;
        class C {
            void M(Action a, Action b, Action c) {
                Action d = a + (b + c);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Right-nested: do not flatten, produce Combine(a, Combine(b, c))
        Assert.Contains("CsPrelude.Combine(a, CsPrelude.Combine(b, c))", result.OutputCode);
    }

    [Fact]
    public void Delegate_Remove_Not_Flattened()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2, Action d3) {
                Action d = d1 - d2 - d3;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Remove(CsPrelude.Remove(d1, d2), d3)", result.OutputCode);
    }
```

- [ ] **Step 2: 运行测试确认通过（已有实现应覆盖）**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "DelegatePreprocessorUnitTests" -v n
```
Expected: PASS — `CollectAddOperands` 只处理左嵌套，右嵌套不扁平化。多组合参数重载已实现。如果测试失败，检查 `CollectAddOperands` 是否正确识别左嵌套链。

- [ ] **Step 3: 提交**

```powershell
cd d:\code\cs2j; git add tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs; git commit -m "test(delegate): add nested and multi-combine flattening tests"
```

---

## Task 7: 匿名方法 → lambda（显式参数列表）

**Files:**
- Modify: `src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs`
- Modify: `tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs`

- [ ] **Step 1: 追加失败测试**

在 `DelegatePreprocessorUnitTests.cs` 中追加：

```csharp
    [Fact]
    public void AnonymousMethod_ExplicitParams_To_Lambda()
    {
        var src = """
        using System;
        class C {
            void M() {
                Action<int> f = delegate(int x) { return; };
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(int x) =>", result.OutputCode);
        Assert.DoesNotContain("delegate", result.OutputCode);
    }

    [Fact]
    public void AnonymousMethod_NoParam_To_Empty_Lambda()
    {
        var src = """
        using System;
        class C {
            void M() {
                Action f = delegate { };
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("() =>", result.OutputCode);
        Assert.DoesNotContain("delegate", result.OutputCode);
    }

    [Fact]
    public void AnonymousMethod_Async_To_Async_Lambda()
    {
        var src = """
        using System;
        using System.Threading.Tasks;
        class C {
            async Task M() {
                Func<Task> f = async delegate { await Task.Delay(1); };
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("async () =>", result.OutputCode);
        Assert.DoesNotContain("delegate", result.OutputCode);
    }

    [Fact]
    public void Delegate_Declaration_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            delegate void D(int x);
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void AnonymousMethod_As_MethodArg_To_Lambda()
    {
        var src = """
        using System;
        class C {
            void Run(Action<int> f) { }
            void M() {
                Run(delegate(int x) { });
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(int x) =>", result.OutputCode);
        Assert.DoesNotContain("delegate", result.OutputCode);
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "DelegatePreprocessorUnitTests" -v n
```
Expected: FAIL — 匿名方法未被改写。

- [ ] **Step 3: 修改 `DelegateSyntaxRewriter.cs`，替换 `VisitAnonymousMethodExpression` 实现**

将 `DelegateSyntaxRewriter.cs` 中的 `VisitAnonymousMethodExpression` 占位实现替换为：

```csharp
    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        // Visit children first (block body)
        var visited = base.VisitAnonymousMethodExpression(node);
        if (visited is not AnonymousMethodExpressionSyntax anonymousMethod)
            return visited;

        // Do NOT rewrite delegate declarations (delegate void D(int x);)
        // — those are DelegateDeclarationSyntax, not AnonymousMethodExpressionSyntax,
        // so they never reach this method. But be safe.

        // If the anonymous method has an explicit parameter list, convert directly
        if (anonymousMethod.ParameterList != null)
        {
            _stats.AnonymousMethodRewritten++;
            Changed = true;
            return SyntaxFactory.ParenthesizedLambdaExpression(
                anonymousMethod.ParameterList,
                SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken),
                anonymousMethod.Block)
                .WithTriviaFrom(node);
        }

        // No explicit parameter list — need to synthesize from target delegate type
        if (_semanticModel == null)
        {
            _stats.SkippedUnsupported++;
            _diagnostics.Add(new DelegatePreprocessorDiagnostic(
                DelegatePreprocessorSeverity.Warning,
                "Cannot resolve target delegate type for parameter-less anonymous method without SemanticModel; skipping."));
            return anonymousMethod;
        }

        var convertedType = _semanticModel.GetTypeInfo(node).ConvertedType;
        if (convertedType is not INamedTypeSymbol namedType
            || namedType.TypeKind != TypeKind.Delegate
            || namedType.DelegateInvokeMethod == null)
        {
            _stats.SkippedUnsupported++;
            _diagnostics.Add(new DelegatePreprocessorDiagnostic(
                DelegatePreprocessorSeverity.Warning,
                "Cannot resolve target delegate type for parameter-less anonymous method; skipping."));
            return anonymousMethod;
        }

        var invokeMethod = namedType.DelegateInvokeMethod;
        var parameters = invokeMethod.Parameters;

        if (parameters.Length == 0)
        {
            // No params → () => { ... }
            _stats.AnonymousMethodRewritten++;
            Changed = true;
            return SyntaxFactory.ParenthesizedLambdaExpression(
                SyntaxFactory.ParameterList(),
                SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken),
                anonymousMethod.Block)
                .WithTriviaFrom(node);
        }

        // Synthesize parameter names, avoiding identifiers already used in the block
        var blockedNames = new HashSet<string>(
            anonymousMethod.Block.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Select(id => id.Identifier.ValueText),
            StringComparer.Ordinal);

        var paramList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            parameters.Select((p, i) =>
            {
                string name = GenerateUniqueName("p" + i, blockedNames);
                blockedNames.Add(name);
                return SyntaxFactory.Parameter(SyntaxFactory.Identifier(name));
            })));

        _stats.AnonymousMethodRewritten++;
        Changed = true;
        return SyntaxFactory.ParenthesizedLambdaExpression(
            paramList,
            SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken),
            anonymousMethod.Block)
            .WithTriviaFrom(node);
    }

    private static string GenerateUniqueName(string baseName, HashSet<string> blocked)
    {
        if (!blocked.Contains(baseName))
            return baseName;
        for (int i = 0; ; i++)
        {
            var candidate = baseName + "_" + i;
            if (!blocked.Contains(candidate))
                return candidate;
        }
    }
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "DelegatePreprocessorUnitTests" -v n
```
Expected: PASS — 全部测试通过。

- [ ] **Step 5: 提交**

```powershell
cd d:\code\cs2j; git add src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs; git commit -m "feat(delegate): add anonymous method to lambda rewrite with parameter synthesis"
```

---

## Task 8: 负向测试 + 事件跳过

**Files:**
- Modify: `tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs`

- [ ] **Step 1: 追加负向测试**

在 `DelegatePreprocessorUnitTests.cs` 中追加：

```csharp
    [Fact]
    public void Event_CompoundAdd_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            event Action E;
            void M(Action h) {
                E += h;
            }
        }
        """;
        var result = Preprocess(src);
        // Events are skipped — left to Event preprocessor
        Assert.Contains("E += h", result.OutputCode);
    }

    [Fact]
    public void Explicit_DelegateCombine_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2) {
                Action d = (Action)Delegate.Combine(d1, d2);
            }
        }
        """;
        var result = Preprocess(src);
        // Explicit Delegate.Combine calls are preserved
        Assert.Contains("Delegate.Combine(d1, d2)", result.OutputCode);
    }

    [Fact]
    public void MethodGroup_Conversion_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M() { }
            void M2() {
                Action d = M;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Lambda_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M() {
                Action f = () => { };
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Delegate_Combine_With_MethodGroup()
    {
        var src = """
        using System;
        class C {
            void M() { }
            void M2(Action d) {
                Action r = d + M;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine(d, M)", result.OutputCode);
    }

    [Fact]
    public void Delegate_Combine_With_Lambda()
    {
        var src = """
        using System;
        class C {
            void M(Action d) {
                Action r = d + (() => { });
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine(d, () => { })", result.OutputCode);
    }

    [Fact]
    public void Generic_Delegate_Combine()
    {
        var src = """
        using System;
        class C {
            void M(Func<int, int> f1, Func<int, int> f2) {
                Func<int, int> r = f1 + f2;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine(f1, f2)", result.OutputCode);
    }

    [Fact]
    public void Idempotent_Second_Run_Produces_Same_Output()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2, Action h) {
                Action d = d1 + d2;
                d += h;
            }
        }
        """;
        var first = Preprocess(src);
        Assert.True(first.Changed);
        var second = Preprocess(first.OutputCode);
        // Second run: dirty (has + from arithmetic-like syntax tree) but no rewrites
        // Output should be byte-identical
        Assert.Equal(first.OutputCode, second.OutputCode);
    }
```

- [ ] **Step 2: 运行测试确认通过**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "DelegatePreprocessorUnitTests" -v n
```
Expected: PASS — 全部测试通过。如果 `Event_CompoundAdd_Not_Rewritten` 失败，检查 `VisitAssignmentExpression` 中 `IEventSymbol` 判定是否正确。

- [ ] **Step 3: 提交**

```powershell
cd d:\code\cs2j; git add tests/CSharpToJava.Tests/DelegatePreprocessorUnitTests.cs; git commit -m "test(delegate): add negative tests for events, explicit Combine, method groups, lambdas, and idempotency"
```

---

## Task 9: CLI Verb + ProjectDelegatePreprocessor

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectDelegatePreprocessor.cs`
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: 创建 `src/CSharpToJava.CLI/ProjectDelegatePreprocessor.cs`**

```csharp
using CSharpToJava.Core.DelegatePreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Xml.Linq;

namespace CSharpToJava.CLI;

internal sealed class ProjectDelegatePreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public DelegatePreprocessorOptions Options { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectDelegatePreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required DelegatePreprocessorStatistics Statistics { get; init; }
    public required IReadOnlyList<DelegatePreprocessorDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectDelegatePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-delegate-src";

    public static async Task<ProjectDelegatePreprocessResult> PreprocessAsync(
        ProjectDelegatePreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(
            Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Delegate source directory already exists: {intermediateRoot}. " +
                    "Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<DelegatePreprocessorDiagnostic>();
        var statistics = new DelegatePreprocessorStatistics();
        var rewriter = new DelegatePreprocessor();
        var sourceRoot = layout.SourceRoot;
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph, request.Verbose,
                request.CompatNamespacePrefixes, layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot, intermediateRoot, "[delegate]", request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot,
                Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Pre-parse all .cs files for SemanticModel
        var parseOptions = new CSharpParseOptions(
            preprocessorSymbols: new[] { "NETCOREAPP" });
        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions, path: csFile);
            syntaxTrees[csFile] = tree;
        }

        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "delegate-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        var rewrittenSourceFiles = new List<string>();

        // Process each .cs file
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            if (!syntaxTrees.TryGetValue(sourceFile, out var tree))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var semanticModel = compilation.GetSemanticModel(tree);
            var result = rewriter.Preprocess(sourceCode, semanticModel, request.Options);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with { FilePath = relativePath });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode,
                    new System.Text.UTF8Encoding(false));
                rewrittenSourceFiles.Add(destinationFile);
            }

            if (request.Verbose)
            {
                Console.WriteLine(
                    $"[delegate] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        // Release support files and inject ProjectReferences
        await PreludeSupportFiles.ReleaseAsync(
            Path.Combine(intermediateRoot, PreludeSupportFiles.LibDirectoryName));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            layout, exclusionInfo, rewrittenSourceFiles, projectGraph);
        EnsureExtensionProjectReferences(
            sourceRoot, intermediateRoot, layout, exclusionInfo, hostProjectDirectories, diagnostics);

        // Residual detection (syntax-level: anonymous methods only)
        var residualCount = CountResidualAnonymousMethods(intermediateRoot);
        statistics.ResidualCount = residualCount;

        if (residualCount > 0)
        {
            var severity = request.Strict
                ? DelegatePreprocessorSeverity.Error
                : DelegatePreprocessorSeverity.Warning;
            diagnostics.Add(new DelegatePreprocessorDiagnostic(
                severity,
                $"{residualCount} residual anonymous method(s) remaining after preprocessing."));
        }

        // Restore + verify build/tests
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[delegate]");
            if (request.Verbose)
                Console.WriteLine($"Restore exit code: {restoreExitCode}");

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new DelegatePreprocessorDiagnostic(
                    DelegatePreprocessorSeverity.Warning,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
            }

            await VerifyBuildAndTestsAsync(restoreTarget, projectGraph, exclusionInfo,
                request, diagnostics);
            DeleteBuildOutputDirectories(intermediateRoot);
        }

        return new ProjectDelegatePreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == DelegatePreprocessorSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static int CountResidualAnonymousMethods(string intermediateRoot)
    {
        var count = 0;
        foreach (var csFile in Directory.EnumerateFiles(
            intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csFile);
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(content).GetRoot();
            count += root.DescendantNodes().Count(n => n is AnonymousMethodExpressionSyntax);
        }
        return count;
    }

    private static async Task VerifyBuildAndTestsAsync(
        string restoreTarget,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        ProjectDelegatePreprocessRequest request,
        List<DelegatePreprocessorDiagnostic> diagnostics)
    {
        if (!request.VerifyBuild && !request.VerifyTests)
            return;

        Console.WriteLine($"[delegate] Verifying build: dotnet build \"{restoreTarget}\" -c Release");
        var buildExit = await RunDotNetCommandAsync("build", restoreTarget, request.Verbose, "Release");
        if (buildExit != 0)
        {
            diagnostics.Add(new DelegatePreprocessorDiagnostic(
                DelegatePreprocessorSeverity.Error,
                $"dotnet build failed with exit code {buildExit} after delegate preprocessing."));
            return;
        }

        if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
        {
            Console.WriteLine($"[delegate] Verifying tests: dotnet test \"{restoreTarget}\" -c Release");
            var testExit = await RunDotNetCommandAsync("test", restoreTarget, request.Verbose, "Release");
            if (testExit != 0)
            {
                diagnostics.Add(new DelegatePreprocessorDiagnostic(
                    DelegatePreprocessorSeverity.Error,
                    $"dotnet test failed with exit code {testExit} after delegate preprocessing."));
            }
        }
    }

    private static bool HasSupportedTestProjects(
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        if (projectGraph == null)
            return false;
        return projectGraph.ProjectsInTopologicalOrder.Any(project =>
            project.Kind == ProjectKind.Test &&
            !exclusionInfo.ExcludedProjectPaths.Contains(Path.GetFullPath(project.ProjectFilePath)));
    }

    private static async Task ForwardOutputAsync(System.IO.StreamReader source, TextWriter destination)
    {
        string? line;
        while ((line = await source.ReadLineAsync()) != null)
            await destination.WriteLineAsync(line);
    }

    private static async Task<int> RunDotNetCommandAsync(
        string verb, string targetPath, bool verbose, string? configuration = null)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"{verb} \"{targetPath}\" --nologo" +
                            (configuration is null ? "" : $" -c {configuration}") +
                            " -p:GenerateResourceUsePreserializedResources=true",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine($"Failed to start dotnet {verb} process.");
                return -1;
            }
            var outputTask = ForwardOutputAsync(process.StandardOutput, Console.Out);
            var errorTask = ForwardOutputAsync(process.StandardError, Console.Error);
            await process.WaitForExitAsync();
            await outputTask;
            await errorTask;
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet {verb} failed: {ex.Message}");
            return -1;
        }
    }

    private static void DeleteBuildOutputDirectories(string root)
    {
        foreach (var binDirectory in Directory
                     .EnumerateDirectories(root, "bin", SearchOption.AllDirectories)
                     .ToList())
        {
            try { Directory.Delete(binDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void MergeStatistics(
        DelegatePreprocessorStatistics target,
        DelegatePreprocessorStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.CombineRewritten += source.CombineRewritten;
        target.RemoveRewritten += source.RemoveRewritten;
        target.CompoundAssignRewritten += source.CompoundAssignRewritten;
        target.AnonymousMethodRewritten += source.AnonymousMethodRewritten;
        target.SkippedUnsupported += source.SkippedUnsupported;
        target.ResidualCount += source.ResidualCount;
    }

    // ---- EnsureExtensionProjectReferences (mirrors ProjectArrayMethodPreprocessor) ----

    private static IReadOnlySet<string> ComputeAllExtensionHostProjectDirectories(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        var ownReleaseDirectories = FindOwnReleaseLibDirectories(layout.SourceRoot);

        var hostDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Direct: rewritten files' project directories
        foreach (var file in rewrittenFiles)
        {
            if (IsUnderOwnReleaseLib(file, ownReleaseDirectories))
                continue;
            // Find the .csproj that owns this file
            var dir = Path.GetDirectoryName(file)!;
            while (dir != null)
            {
                var csproj = Directory.GetFiles(dir, "*.csproj")
                    .FirstOrDefault();
                if (csproj != null)
                {
                    hostDirs.Add(dir);
                    break;
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }

        // Also add from project graph
        if (projectGraph != null)
        {
            foreach (var project in projectGraph.ProjectsInTopologicalOrder)
            {
                if (SupportedProjectFiltering.IsUnderExcludedProject(
                        project.ProjectFilePath, exclusionInfo))
                    continue;
                hostDirs.Add(Path.GetDirectoryName(project.ProjectFilePath)!);
            }
        }

        return hostDirs;
    }

    private static List<string> FindOwnReleaseLibDirectories(string sourceRoot)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory
            .EnumerateDirectories(sourceRoot, PreludeSupportFiles.LibDirectoryName, options)
            .Where(dir => IsOwnReleasedProjectFile(
                Path.Combine(dir, Cs2jPreludeProject.ProjectFileName)))
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static bool IsOwnReleasedProjectFile(string projectFilePath)
    {
        try
        {
            if (!File.Exists(projectFilePath)) return false;
            var content = File.ReadAllText(projectFilePath);
            return string.Equals(
                NormalizeLineEndings(content),
                NormalizeLineEndings(PreludeSupportFiles.GetProjectFileContent()),
                StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsUnderOwnReleaseLib(string filePath, IReadOnlyList<string> ownReleaseDirectories)
    {
        var fullPath = Path.GetFullPath(filePath);
        return ownReleaseDirectories.Any(dir =>
            fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureExtensionProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        List<DelegatePreprocessorDiagnostic> diagnostics)
    {
        var libProjectPath = Path.Combine(
            intermediateRoot,
            PreludeSupportFiles.LibDirectoryName,
            Cs2jPreludeProject.ProjectFileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
                continue;

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
                continue;

            var destinationProjectFile = Path.Combine(
                intermediateRoot,
                Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionProjectReference(
                destinationProjectFile, libProjectPath, intermediateRoot, diagnostics);
        }
    }

    private static void EnsureExtensionProjectReference(
        string projectFilePath,
        string libProjectPath,
        string intermediateRoot,
        List<DelegatePreprocessorDiagnostic> diagnostics)
    {
        if (!File.Exists(projectFilePath))
            return;

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
            return;

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDirectory, intermediateRoot))
        {
            var removePattern = Path.Combine(PreludeSupportFiles.LibDirectoryName, "**");
            if (!root.Descendants().Any(e =>
                    e.Name.LocalName == "Compile"
                    && string.Equals((string?)e.Attribute("Remove"), removePattern, StringComparison.OrdinalIgnoreCase)))
            {
                root.Add(
                    new XText(Environment.NewLine + "  "),
                    new XElement("ItemGroup",
                        new XElement("Compile", new XAttribute("Remove", removePattern))),
                    new XText(Environment.NewLine));
            }
        }

        foreach (var referenceElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference")
                     .ToList())
        {
            var include = (string?)referenceElement.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
                continue;

            var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
            if (PathsEqual(resolvedPath, libProjectPath))
            {
                referenceElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string? GetPropertyValue(XElement root, string name)
    {
        var prop = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "PropertyGroup"
                && e.Elements().Any(p => p.Name.LocalName == name));
        return prop?.Element(name)?.Value;
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");
}
```

- [ ] **Step 2: 修改 `Program.cs` — 追加 verb options 类**

在 `NullCoalesceVerbOptions` 类之后追加：

```csharp
[Verb("delegate-preprocess", HelpText = "Rewrite delegate +/- / += / -= and anonymous methods")]
class DelegatePreprocessVerbOptions
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
    [Option("strict", Default = false, HelpText = "Treat residual delegate operators as errors instead of warnings")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after delegate preprocessing")]
    public bool NoVerifyBuild { get; set; }
    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after delegate preprocessing")]
    public bool NoVerifyTests { get; set; }
}
```

- [ ] **Step 3: 修改 `Program.cs` — 在 `ParseArguments` 类型列表追加 `DelegatePreprocessVerbOptions`**

将 `Parser.ParseArguments<...>` 行追加 `DelegatePreprocessVerbOptions`：

找到：
```csharp
        return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions, ForeachDeconstructionVerbOptions, NullCoalesceVerbOptions>(args)
```
改为：
```csharp
        return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions, ForeachDeconstructionVerbOptions, NullCoalesceVerbOptions, DelegatePreprocessVerbOptions>(args)
```

- [ ] **Step 4: 修改 `Program.cs` — 在 `MapResult` 中追加 dispatch**

在 `(NullCoalesceVerbOptions opts) => NullCoalesceAsync(opts),` 之后追加：
```csharp
                (DelegatePreprocessVerbOptions opts) => DelegatePreprocessAsync(opts),
```

- [ ] **Step 5: 修改 `Program.cs` — 追加 `DelegatePreprocessAsync` 方法**

在 `NullCoalesceAsync` 方法之后追加：

```csharp
    private static async Task<int> DelegatePreprocessAsync(DelegatePreprocessVerbOptions opts)
    {
        var coreOptions = new DelegatePreprocessorOptions(Verbose: opts.Verbose, Strict: opts.Strict);
        var rewriter = new DelegatePreprocessor();

        // Single-file mode
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "delegate-preprocess-standalone",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var model = compilation.GetSemanticModel(tree);
            var result = rewriter.Preprocess(sourceCode, model, coreOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose)
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.Message}");

            return result.Diagnostics.Any(d => d.Severity == DelegatePreprocessorSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectDelegatePreprocessor.PreprocessAsync(
            new ProjectDelegatePreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                Options = coreOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
                VerifyBuild = !opts.NoVerifyBuild,
                VerifyTests = !opts.NoVerifyTests,
                Strict = opts.Strict,
            });

        Console.WriteLine(
            $"delegate: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.CombineRewritten} combine, " +
            $"{preprocessResult.Statistics.RemoveRewritten} remove, " +
            $"{preprocessResult.Statistics.CompoundAssignRewritten} compound, " +
            $"{preprocessResult.Statistics.AnonymousMethodRewritten} anonymous method, " +
            $"{preprocessResult.Statistics.SkippedUnsupported} skipped, " +
            $"{preprocessResult.Statistics.ResidualCount} residual");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
            Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        return preprocessResult.Success ? 0 : 1;
    }
```

- [ ] **Step 6: 在 `Program.cs` 顶部追加 using**

在 `using CSharpToJava.Core.NullCoalesceRewriter;` 之后追加：
```csharp
using CSharpToJava.Core.DelegatePreprocessor;
```

- [ ] **Step 7: 验证编译通过**

Run:
```powershell
cd d:\code\cs2j\src\CSharpToJava.CLI; dotnet build -c Release
```
Expected: Build 成功。

- [ ] **Step 8: 提交**

```powershell
cd d:\code\cs2j; git add src/CSharpToJava.CLI/ProjectDelegatePreprocessor.cs src/CSharpToJava.CLI/Program.cs; git commit -m "feat(delegate): add ProjectDelegatePreprocessor and CLI verb delegate-preprocess"
```

---

## Task 10: convert-project 流水线接入

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: 在 `ConvertProjectOptions` 中追加 `NoDelegatePreprocess` 开关**

在 `NoNullCoalesce` 属性之后追加：

```csharp
    [Option("no-delegate-preprocess", Default = false,
        HelpText = "Disable the default delegate (+/- / += / -= / anonymous method) preprocessing step")]
    public bool NoDelegatePreprocess { get; set; }
```

在 `NullCoalesce` 便捷属性之后追加：

```csharp
    public bool DelegatePreprocess => !NoDelegatePreprocess;
```

- [ ] **Step 2: 在 `ConvertProject` 的 `NullCoalesce` 块之后插入 delegate 块**

找到 `if (opts.NullCoalesce) { ... }` 块的结尾（`opts.FingerprintInputRoot ??= nullCoalesceResult.SourceRoot;` 后的 `}`），在其后、`if (opts.TupleToStruct)` 之前插入：

```csharp
            if (opts.DelegatePreprocess)
            {
                var delegateResult = await ProjectDelegatePreprocessor.PreprocessAsync(
                    new ProjectDelegatePreprocessRequest
                    {
                        SourcePath = opts.Source,
                        DestinationRoot = opts.Destination,
                        Force = opts.Force,
                        Verbose = opts.Verbose,
                        FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                        CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
                        VerifyBuild = opts.VerifyBuild,
                        VerifyTests = opts.VerifyTests,
                        Strict = opts.LocalFunctionStrict,
                    });

                Console.WriteLine(
                    $"delegate: {delegateResult.Statistics.FilesTransformed} transformed, " +
                    $"{delegateResult.Statistics.FilesSkippedClean} clean, " +
                    $"{delegateResult.Statistics.CombineRewritten} combine, " +
                    $"{delegateResult.Statistics.RemoveRewritten} remove, " +
                    $"{delegateResult.Statistics.CompoundAssignRewritten} compound, " +
                    $"{delegateResult.Statistics.AnonymousMethodRewritten} anonymous method, " +
                    $"{delegateResult.Statistics.SkippedUnsupported} skipped, " +
                    $"{delegateResult.Statistics.ResidualCount} residual");

                foreach (var diagnostic in delegateResult.Diagnostics)
                {
                    var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
                    Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
                }

                if (!delegateResult.Success)
                    return 1;

                opts.Source = delegateResult.PreprocessedSourcePath;
                opts.FingerprintSourcePath ??= delegateResult.OriginalSourcePath;
                opts.FingerprintInputRoot ??= delegateResult.SourceRoot;
            }
```

- [ ] **Step 3: 在 `GetPreservedDestinationDirectories` 中追加 delegate**

在 `if (opts.NullCoalesce) { preserved.Add(ProjectNullCoalescePreprocessor.IntermediateDirectoryName); }` 之后追加：

```csharp
        if (opts.DelegatePreprocess)
        {
            preserved.Add(ProjectDelegatePreprocessor.IntermediateDirectoryName);
        }
```

- [ ] **Step 4: 在 `GetProjectConversionOptionTokens` 中追加指纹 token**

在 `$"null-coalesce={opts.NullCoalesce}",` 之后追加：

```csharp
            $"delegate-preprocess={opts.DelegatePreprocess}",
```

- [ ] **Step 5: 验证编译通过**

Run:
```powershell
cd d:\code\cs2j\src\CSharpToJava.CLI; dotnet build -c Release
```
Expected: Build 成功。

- [ ] **Step 6: 提交**

```powershell
cd d:\code\cs2j; git add src/CSharpToJava.CLI/Program.cs; git commit -m "feat(delegate): wire delegate preprocess into convert-project pipeline"
```

---

## Task 11: TypeMappings.json 映射 + .gitignore

**Files:**
- Modify: `config/TypeMappings.json`
- Modify: `.gitignore`

- [ ] **Step 1: 在 `config/TypeMappings.json` 的 `typeMappings` 数组中追加类型映射**

在 `Cs2jArray.ArrayHelper` 条目之后追加：

```json
        {
            "csharp": "Cs2jPrelude.CsPrelude",
            "java": "DelegateHelper",
            "imports": [
                "io.github.ningpp.compat.DelegateHelper"
            ]
        },
```

- [ ] **Step 2: 在 `config/TypeMappings.json` 的 `methodMappings` 数组中追加方法映射**

在 `methodMappings` 数组开头追加：

```json
        {
            "type": "Cs2jPrelude.CsPrelude",
            "method": "Combine",
            "javaMethod": "combine"
        },
        {
            "type": "Cs2jPrelude.CsPrelude",
            "method": "Remove",
            "javaMethod": "remove"
        },
```

- [ ] **Step 3: 修改 `.gitignore`，补齐中间目录条目**

在 `.cs2j-no-goto-src/` 等条目所在区域，追加：

```
.cs2j-delegate-src/
.cs2j-enum-src/
.cs2j-record-readonly-src/
.cs2j-array-src/
.cs2j-foreach-deconstruction-src/
.cs2j-null-coalesce-src/
```

- [ ] **Step 4: 验证 JSON 格式正确**

Run:
```powershell
cd d:\code\cs2j; dotnet build -c Release
```
Expected: Build 成功（TypeMappings.json 是运行时配置，不影响编译，但确认无语法错误）。

- [ ] **Step 5: 提交**

```powershell
cd d:\code\cs2j; git add config/TypeMappings.json .gitignore; git commit -m "feat(delegate): add TypeMappings for CsPrelude and update .gitignore"
```

---

## Task 12: 项目级集成测试

**Files:**
- Create: `tests/CSharpToJava.Tests/ProjectDelegatePreprocessorTests.cs`

- [ ] **Step 1: 创建测试文件**

```csharp
using CSharpToJava.CLI;
using Xunit;

namespace CSharpToJava.Tests;

public class ProjectDelegatePreprocessorTests
{
    [Fact]
    public async Task PreprocessAsync_SingleProject_Builds_Successfully()
    {
        if (!IsDotNetAvailable())
            return;

        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceRoot);

            var projectPath = Path.Combine(sourceRoot, "Lib.csproj");
            await File.WriteAllTextAsync(projectPath,
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

            var csPath = Path.Combine(sourceRoot, "Lib.cs");
            await File.WriteAllTextAsync(csPath, """
                using System;
                class Lib {
                    void M(Action d1, Action d2, Action h) {
                        Action d = d1 + d2;
                        d += h;
                        d -= h;
                    }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            Directory.CreateDirectory(destinationRoot);

            var result = await ProjectDelegatePreprocessor.PreprocessAsync(
                new ProjectDelegatePreprocessRequest
                {
                    SourcePath = projectPath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = false,
                    VerifyBuild = true,
                    VerifyTests = false,
                    Strict = false,
                });

            Assert.True(result.Success,
                string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            Assert.True(result.Statistics.FilesTransformed > 0);

            // Verify Cs2jPrelude support files were released
            var preludeDir = Path.Combine(
                result.IntermediateRoot,
                PreludeSupportFiles.LibDirectoryName);
            Assert.True(File.Exists(Path.Combine(preludeDir, "CsPrelude.cs")));
            Assert.True(File.Exists(Path.Combine(preludeDir, "Cs2jPrelude.csproj")));

            // Verify output contains Combine / Remove calls
            var outputCs = Path.Combine(result.IntermediateRoot, "Lib.cs");
            var output = await File.ReadAllTextAsync(outputCs);
            Assert.Contains("CsPrelude.Combine", output);
            Assert.Contains("CsPrelude.Remove", output);
            Assert.DoesNotContain("d1 + d2", output);
            Assert.DoesNotContain("d += h", output);
            Assert.DoesNotContain("d -= h", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PreprocessAsync_CleanFile_ByteLevelPreserved()
    {
        if (!IsDotNetAvailable())
            return;

        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceRoot);

            var projectPath = Path.Combine(sourceRoot, "Lib.csproj");
            await File.WriteAllTextAsync(projectPath,
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

            var csPath = Path.Combine(sourceRoot, "Lib.cs");
            var content = """
                class Lib {
                    int Add(int a, int b) => a + b;
                }
                """;
            await File.WriteAllTextAsync(csPath, content);

            var destinationRoot = Path.Combine(root, "out");
            Directory.CreateDirectory(destinationRoot);

            var result = await ProjectDelegatePreprocessor.PreprocessAsync(
                new ProjectDelegatePreprocessRequest
                {
                    SourcePath = projectPath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = false,
                    VerifyBuild = false,
                    VerifyTests = false,
                });

            Assert.True(result.Success);
            Assert.True(result.Statistics.FilesSkippedClean > 0);

            var outputCs = Path.Combine(result.IntermediateRoot, "Lib.cs");
            var output = await File.ReadAllTextAsync(outputCs);
            Assert.Equal(content, output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static bool IsDotNetAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-delegate-test-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
```

- [ ] **Step 2: 运行测试确认通过**

Run:
```powershell
cd d:\code\cs2j\tests\CSharpToJava.Tests; dotnet test --filter "ProjectDelegatePreprocessorTests" -v n
```
Expected: PASS — 项目级测试通过（需要 dotnet 可用）。

- [ ] **Step 3: 提交**

```powershell
cd d:\code\cs2j; git add tests/CSharpToJava.Tests/ProjectDelegatePreprocessorTests.cs; git commit -m "test(delegate): add project-level integration tests"
```

---

## Task 13: 全量构建 + 全量测试

- [ ] **Step 1: 全量构建**

Run:
```powershell
cd d:\code\cs2j; dotnet build -c Release
```
Expected: 全绿。

- [ ] **Step 2: 全量测试**

Run:
```powershell
cd d:\code\cs2j; dotnet test -c Release --no-build
```
Expected: 全绿。

- [ ] **Step 3: 最终提交（如有遗漏）**

```powershell
cd d:\code\cs2j; git status; git add -A; git commit -m "feat(delegate): final build verification"
```
