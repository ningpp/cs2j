# Null Coalesce Preprocessor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a C# → C# preprocessor that rewrites all `??` and `??=` operators into equivalent explicit ternary / if-assignment forms, eliminating the need for the main converter to handle these operators.

**Architecture:** Two-pass syntax rewriter using Roslyn `CSharpSyntaxRewriter`. Pass 1 (`NullCoalesceExpressionCollector`) rewrites `??`/`??=` expressions bottom-up and collects temp-variable declarations. Pass 2 (`NullCoalesceStatementLifter`) inserts the collected declarations before their anchor statements. A CLI wrapper (`ProjectNullCoalescePreprocessor`) mirrors `ProjectLocalFunctionLiftPreprocessor` for project-level batch processing with SemanticModel support.

**Tech Stack:** C# / ..NET 10, Microsoft.CodeAnalysis.CSharp 4.12.0, xUnit

---

## File Structure

### Core Library (`src/CSharpToJava.Core/NullCoalesceRewriter/`)

| File | Responsibility |
|------|----------------|
| `NullCoalesceOptions.cs` | Options record (Verbose, Strict) |
| `NullCoalesceStatistics.cs` | Statistics model with mutable counters |
| `NullCoalesceDiagnostic.cs` | Diagnostic enum + record |
| `NullCoalesceResult.cs` | Result record (OutputCode, Changed, Diagnostics, Statistics) |
| `NullCoalesceSyntaxRewriter.cs` | `CSharpSyntaxRewriter` subclass — the two-pass rewriter core |
| `NullCoalesceRewriter.cs` | Public entry point: `Preprocess(sourceCode, semanticModel, options) -> NullCoalesceResult` |

### CLI (`src/CSharpToJava.CLI/`)

| File | Responsibility |
|------|----------------|
| `ProjectNullCoalescePreprocessor.cs` | Project-level wrapper — mirrors `ProjectLocalFunctionLiftPreprocessor` |
| `Program.cs` (modify) | Add `null-coalesce` verb, wire into `convert-project` pipeline, add fingerprint token |

### Tests (`tests/CSharpToJava.Tests/`)

| File | Responsibility |
|------|----------------|
| `NullCoalesceRewriterUnitTests.cs` | Unit tests for `??` and `??=` rewriting |

---

## Task 1: Core Data Structures

**Files:**
- Create: `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceOptions.cs`
- Create: `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatistics.cs`
- Create: `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceDiagnostic.cs`
- Create: `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceResult.cs`

- [ ] **Step 1: Create the options record**

Create file `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceOptions.cs`:

```csharp
namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <param name="Verbose">If true, emit Info-level diagnostics.</param>
/// <param name="Strict">If true, residual ?? / ??= produces Error; otherwise Warning.</param>
public sealed record NullCoalesceOptions(
    bool Verbose = false,
    bool Strict = false);
```

- [ ] **Step 2: Create the statistics model**

Create file `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatistics.cs`:

```csharp
namespace CSharpToJava.Core.NullCoalesceRewriter;

public sealed class NullCoalesceStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int NullCoalescingRewritten;       // ?? rewrite count
    public int NullCoalescingAssignRewritten; // ??= rewrite count
    public int TempVariablesGenerated;        // temp variables generated
    public int NestedRewrites;                // nested rewrite count
    public int SkippedUnsupported;            // unsupported scenario skip count
    public int ResidualCount;                 // post-self-check residual count
}
```

- [ ] **Step 3: Create the diagnostic model**

Create file `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceDiagnostic.cs`:

```csharp
namespace CSharpToJava.Core.NullCoalesceRewriter;

public enum NullCoalesceSeverity { Warning, Error }

public sealed record NullCoalesceDiagnostic(
    NullCoalesceSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0);
```

- [ ] **Step 4: Create the result record**

Create file `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceResult.cs`:

```csharp
namespace CSharpToJava.Core.NullCoalesceRewriter;

public sealed record NullCoalesceResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<NullCoalesceDiagnostic> Diagnostics,
    NullCoalesceStatistics Statistics);
```

- [ ] **Step 5: Build to verify no compilation errors**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS (0 errors, 0 warnings)

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceOptions.cs src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatistics.cs src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceDiagnostic.cs src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceResult.cs
git commit -m "feat: add NullCoalesce preprocessor data structures"
```

---

## Task 2: Core Syntax Rewriter — `??` Rewriting

**Files:**
- Create: `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs`

- [ ] **Step 1: Create the syntax rewriter with `??` support**

Create file `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// Two-pass CSharpSyntaxRewriter. Pass 1 (this class) rewrites ?? and ??=
/// bottom-up, collecting temp-variable declarations. Pass 2
/// (NullCoalesceStatementLifter) inserts the collected declarations
/// before their anchor statements.
/// </summary>
internal sealed class NullCoalesceSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly NullCoalesceStatistics _stats;
    private readonly List<NullCoalesceDiagnostic> _diagnostics;
    private readonly NullCoalesceOptions _options;

    // Pending temp-variable declarations collected during Pass 1.
    // Each entry: (declaration statement, anchor statement to insert before, order)
    internal List<PendingDeclaration> PendingDeclarations { get; } = new();

    private int _tempCounter;
    private int _nestedDepth;

    internal sealed record PendingDeclaration(
        StatementSyntax Declaration,
        StatementSyntax AnchorStatement,
        int Order);

    public NullCoalesceSyntaxRewriter(
        SemanticModel? semanticModel,
        NullCoalesceOptions options,
        NullCoalesceStatistics stats,
        List<NullCoalesceDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.CoalesceExpression))
            return base.VisitBinaryExpression(node);

        // Bottom-up: process children first (inner ?? before outer)
        var visitedLeft = (ExpressionSyntax?)Visit(node.Left);
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var left = visitedLeft ?? node.Left;
        var right = visitedRight ?? node.Right;

        _nestedDepth++;
        if (_nestedDepth > 1)
            _stats.NestedRewrites++;
        _nestedDepth--;

        // Skip if left operand is null literal (meaningless but legal C#)
        if (left is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression))
            return node;

        // Derive type from left operand
        var typeSymbol = _semanticModel?.GetTypeInfo(node.Left).Type;
        if (typeSymbol == null || typeSymbol is IErrorTypeSymbol)
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot resolve type of left operand in ?? expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        var typeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var tempName = GenerateTempName();

        // Build: var ___tN = <left>;
        var tempDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(tempName),
                        null,
                        SyntaxFactory.EqualsValueClause(left)))));

        // Build: (___tN != null) ? ___tN : <right>
        var tempRef = SyntaxFactory.IdentifierName(tempName);
        var rewritten = SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.ConditionalExpression(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.NotEqualsExpression,
                    tempRef,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                tempRef,
                right));

        // Find anchor statement (nearest enclosing statement)
        var anchor = FindAnchorStatement(node);

        if (anchor != null)
        {
            PendingDeclarations.Add(new PendingDeclaration(tempDecl, anchor, PendingDeclarations.Count));
        }
        else
        {
            // Cannot insert declaration (e.g. field initializer, lambda expression body)
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot insert temp variable declaration for ?? expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        _stats.NullCoalescingRewritten++;
        _stats.TempVariablesGenerated++;
        return rewritten;
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // ??= is handled in Task 3
        if (!node.IsKind(SyntaxKind.CoalesceAssignmentExpression))
            return base.VisitAssignmentExpression(node);

        // Placeholder — full implementation added in Task 3
        return base.VisitAssignmentExpression(node);
    }

    private string GenerateTempName()
    {
        _tempCounter++;
        return $"___t{_tempCounter}";
    }

    /// <summary>
    /// Walks up the parent chain to find the nearest enclosing StatementSyntax.
    /// Returns null if no statement ancestor exists (e.g. field initializer).
    /// </summary>
    private StatementSyntax? FindAnchorStatement(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is StatementSyntax stmt)
                return stmt;
            current = current.Parent;
        }
        return null;
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS (0 errors, 0 warnings)

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs
git commit -m "feat: add NullCoalesceSyntaxRewriter with ?? rewriting support"
```

---

## Task 3: Core Syntax Rewriter — `??=` Rewriting

**Files:**
- Modify: `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs`

- [ ] **Step 1: Replace the `VisitAssignmentExpression` placeholder with full `??=` implementation**

In `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs`, replace the placeholder `VisitAssignmentExpression` method:

```csharp
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.CoalesceAssignmentExpression))
            return base.VisitAssignmentExpression(node);

        // Process right side first (may contain nested ??)
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var right = visitedRight ?? node.Right;
        var left = node.Left;

        // Check if left is a simple identifier (local variable / parameter)
        if (left is IdentifierNameSyntax identifier)
        {
            // Simple identifier: no temp variable needed
            // Rewrite: a ??= expr  →  if (a == null) { a = expr; }
            var ifStmt = SyntaxFactory.IfStatement(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    identifier,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            identifier,
                            right))));

            _stats.NullCoalescingAssignRewritten++;
            return ifStmt;
        }

        // Member access / indexer: left may have side effects — use temp variable
        var typeSymbol = _semanticModel?.GetTypeInfo(left).Type;
        if (typeSymbol == null || typeSymbol is IErrorTypeSymbol)
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot resolve type of left operand in ??= expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        var tempName = GenerateTempName();

        // Build: var ___tN = <left>;
        var tempDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(tempName),
                        null,
                        SyntaxFactory.EqualsValueClause(left))));

        // Build: if (___tN == null) { <left> = <right>; }
        var ifStmt2 = SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                SyntaxFactory.IdentifierName(tempName),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        left,
                        right))));

        // The IfStatement replaces the ExpressionStatement containing the ??=.
        // The temp variable declaration needs to be inserted before the anchor.
        var anchor = FindAnchorStatement(node);
        if (anchor != null)
        {
            PendingDeclarations.Add(new PendingDeclaration(tempDecl, anchor, PendingDeclarations.Count));
        }
        else
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot insert temp variable declaration for ??= expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        _stats.NullCoalescingAssignRewritten++;
        _stats.TempVariablesGenerated++;
        return ifStmt2;
    }
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS (0 errors, 0 warnings)

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs
git commit -m "feat: add ??= rewriting support to NullCoalesceSyntaxRewriter"
```

---

## Task 4: Statement Lifter (Pass 2)

**Files:**
- Create: `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs`

- [ ] **Step 1: Create the statement lifter**

Create file `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// Pass 2: Inserts temp-variable declarations collected by Pass 1
/// before their anchor statements inside each Block.
/// </summary>
internal sealed class NullCoalesceStatementLifter : CSharpSyntaxRewriter
{
    private readonly List<NullCoalesceSyntaxRewriter.PendingDeclaration> _pending;

    public NullCoalesceStatementLifter(
        List<NullCoalesceSyntaxRewriter.PendingDeclaration> pending)
    {
        _pending = pending;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var newStatements = new List<StatementSyntax>();
        foreach (var stmt in node.Statements)
        {
            // Find pending declarations that anchor before this statement
            var pending = _pending
                .Where(d => d.AnchorStatement == stmt)
                .OrderBy(d => d.Order);
            foreach (var p in pending)
                newStatements.Add(p.Declaration);

            // Recursively process child blocks
            var visited = (StatementSyntax?)Visit(stmt) ?? stmt;
            newStatements.Add(visited);
        }
        return node.WithStatements(SyntaxFactory.List(newStatements));
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS (0 errors, 0 warnings)

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs
git commit -m "feat: add NullCoalesceStatementLifter for temp variable insertion"
```

---

## Task 5: Public Entry Point — `NullCoalesceRewriter.Preprocess`

**Files:**
- Create: `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceRewriter.cs`

- [ ] **Step 1: Create the public entry point**

Create file `src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceRewriter.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// C# null-coalesce preprocessor public entry point. Rewrites ?? and ??=
/// operators into equivalent explicit ternary / if-assignment forms.
/// Clean files are byte-level preserved. Rewritten output is valid C#.
/// </summary>
public sealed class NullCoalesceRewriter
{
    public NullCoalesceResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        NullCoalesceOptions? options = null)
    {
        options ??= new NullCoalesceOptions();
        var stats = new NullCoalesceStatistics { FilesScanned = 1 };
        var diagnostics = new List<NullCoalesceDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Dirty check: no ?? or ??= → byte-level copy
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new NullCoalesceResult(sourceCode, false, diagnostics, stats);
        }

        // Pass 1: rewrite expressions, collect temp declarations
        var rewriter = new NullCoalesceSyntaxRewriter(
            semanticModel, options, stats, diagnostics);
        var pass1Root = (CompilationUnitSyntax)rewriter.Visit(root)!;

        // Pass 2: lift temp declarations to before their anchor statements
        var lifter = new NullCoalesceStatementLifter(rewriter.PendingDeclarations);
        var pass2Root = (CompilationUnitSyntax)lifter.Visit(pass1Root)!;

        // Format
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(pass2Root, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        // Post-check: residual ?? / ??=
        if (ContainsResidualNullCoalesce(code))
        {
            stats.ResidualCount = CountResidual(code);
            var severity = options.Strict
                ? NullCoalesceSeverity.Error
                : NullCoalesceSeverity.Warning;
            diagnostics.Add(new NullCoalesceDiagnostic(
                severity,
                $"{stats.ResidualCount} residual ??/??= operator(s) remaining after preprocessing."));
        }

        return new NullCoalesceResult(code, true, diagnostics, stats);
    }

    /// <summary>Dirty check: syntax tree contains any CoalesceExpression or CoalesceAssignmentExpression.</summary>
    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
            || n is AssignmentExpressionSyntax a
                && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));

    /// <summary>Post-check: rewritten output still contains ?? or ??=.</summary>
    internal static bool ContainsResidualNullCoalesce(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return IsDirty(root);
    }

    private static int CountResidual(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Count(n =>
            n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
            || n is AssignmentExpressionSyntax a
                && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS (0 errors, 0 warnings)

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceRewriter.cs
git commit -m "feat: add NullCoalesceRewriter public entry point with dirty check and post-check"
```

---

## Task 6: Unit Tests — `??` Rewriting

**Files:**
- Create: `tests/CSharpToJava.Tests/NullCoalesceRewriterUnitTests.cs`

- [ ] **Step 1: Create the test file with `??` rewriting tests**

Create file `tests/CSharpToJava.Tests/NullCoalesceRewriterUnitTests.cs`:

```csharp
using CSharpToJava.Core.NullCoalesceRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

public class NullCoalesceRewriterUnitTests
{
    private static NullCoalesceResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("null-coalesce-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new NullCoalesceRewriter().Preprocess(source, model);
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
    public void Simple_Coalesce_Variable_Declaration_Generates_Temp()
    {
        var src = """
        class C {
            void M(string? x, string y) {
                var a = x ?? y;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("___t1 != null", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Method_Call_Left_Operand_Generates_Temp()
    {
        var src = """
        class C {
            string? Get() => null;
            void M(string y) {
                var a = Get() ?? y;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("Get()", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Multiple_Coalesce_In_Same_Statement_Generates_Independent_Temps()
    {
        var src = """
        class C {
            void M(string? a, string? b, string? c, string? d) {
                var z = (a ?? b) + (c ?? d);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("___t2", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Nested_Coalesce_Unfolded_From_Inside_Out()
    {
        var src = """
        class C {
            void M(string? a, string? b, string? c, string? d) {
                var r = a ?? b ?? c ?? d;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("___t2", result.OutputCode);
        Assert.Contains("___t3", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Coalesce_In_Return_Statement()
    {
        var src = """
        class C {
            string? Get() => null;
            string M(string fallback) {
                return Get() ?? fallback;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Coalesce_In_Method_Argument()
    {
        var src = """
        class C {
            string? Get() => null;
            void Foo(string s) {}
            void M(string fallback) {
                Foo(Get() ?? fallback);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Non_Coalesce_BinaryExpression_Not_Rewritten()
    {
        var src = """
        class C {
            void M(int x, int y) {
                var a = x + y;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Regular_Assignment_Not_Rewritten()
    {
        var src = """
        class C {
            void M() {
                var a = "x";
                a = "y";
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Output_Has_No_Residual_Coalesce_Syntax()
    {
        var src = """
        class C {
            void M(string? x, string y) {
                var a = x ?? y;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    [Fact]
    public void Statistics_Count_Coalesce_Rewrites()
    {
        var src = """
        class C {
            void M(string? a, string? b, string? c) {
                var x = a ?? b;
                var y = c ?? "default";
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(3, result.Statistics.NullCoalescingRewritten);
        Assert.Equal(3, result.Statistics.TempVariablesGenerated);
    }
}
```

- [ ] **Step 2: Run the tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~NullCoalesceRewriterUnitTests" --logger "console;verbosity=normal"`
Expected: ALL PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/NullCoalesceRewriterUnitTests.cs
git commit -m "test: add NullCoalesceRewriter unit tests for ?? rewriting"
```

---

## Task 7: Unit Tests — `??=` Rewriting

**Files:**
- Modify: `tests/CSharpToJava.Tests/NullCoalesceRewriterUnitTests.cs`

- [ ] **Step 1: Append `??=` tests to the existing test file**

Add the following tests to `tests/CSharpToJava.Tests/NullCoalesceRewriterUnitTests.cs`, before the closing `}` of the class:

```csharp
    [Fact]
    public void CoalesceAssign_Simple_Identifier_No_Temp_Variable()
    {
        var src = """
        class C {
            void M(string? a, string b) {
                a ??= b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Contains("if (a == null)", result.OutputCode);
        Assert.DoesNotContain("___t", result.OutputCode);
    }

    [Fact]
    public void CoalesceAssign_Member_Access_Generates_Temp_Variable()
    {
        var src = """
        class C {
            public string? Timeout;
            void M(C config, int val) {
                config.Timeout ??= val.ToString();
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("if (___t1 == null)", result.OutputCode);
        Assert.Contains("config.Timeout =", result.OutputCode);
    }

    [Fact]
    public void CoalesceAssign_Indexer_Generates_Temp_Variable()
    {
        var src = """
        using System.Collections.Generic;
        class C {
            void M(Dictionary<string, string> cache, string key, string val) {
                cache[key] ??= val;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("if (___t1 == null)", result.OutputCode);
        Assert.Contains("cache[key] =", result.OutputCode);
    }

    [Fact]
    public void CoalesceAssign_Statistics_Counted()
    {
        var src = """
        class C {
            void M(string? a, string b, string? c, string d) {
                a ??= b;
                c ??= d;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.NullCoalescingAssignRewritten);
    }

    [Fact]
    public void CoalesceAssign_Simple_No_Residual()
    {
        var src = """
        class C {
            void M(string? a, string b) {
                a ??= b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }
```

- [ ] **Step 2: Run all NullCoalesce tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~NullCoalesceRewriterUnitTests" --logger "console;verbosity=normal"`
Expected: ALL PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/NullCoalesceRewriterUnitTests.cs
git commit -m "test: add NullCoalesceRewriter unit tests for ??= rewriting"
```

---

## Task 8: Project-Level CLI Wrapper

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectNullCoalescePreprocessor.cs`

- [ ] **Step 1: Create the project-level preprocessor wrapper**

Create file `src/CSharpToJava.CLI/ProjectNullCoalescePreprocessor.cs`:

```csharp
using CSharpToJava.Core.NullCoalesceRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI;

internal sealed class ProjectNullCoalesceRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public NullCoalesceOptions Options { get; init; } = new();
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

internal static class ProjectNullCoalescePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-null-coalesce-src";

    public static async Task<ProjectNullCoalesceResult> PreprocessAsync(
        ProjectNullCoalesceRequest request)
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
                    $"Null-coalesce source directory already exists: {intermediateRoot}. " +
                    "Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<NullCoalesceDiagnostic>();
        var statistics = new NullCoalesceStatistics();
        var rewriter = new NullCoalesceRewriter();
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
            sourceRoot, intermediateRoot, "[null-coalesce]", request.Verbose);

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
            "null-coalesce-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

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
            }

            if (request.Verbose)
            {
                Console.WriteLine(
                    $"[null-coalesce] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        // Residual detection
        var residualCount = CountResidualNullCoalesce(intermediateRoot);
        statistics.ResidualCount = residualCount;

        if (residualCount > 0)
        {
            var severity = request.Strict
                ? NullCoalesceSeverity.Error
                : NullCoalesceSeverity.Warning;
            diagnostics.Add(new NullCoalesceDiagnostic(
                severity,
                $"{residualCount} residual ??/??= operator(s) remaining after preprocessing."));
        }

        // Restore + verify build/tests
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[null-coalesce]");
            if (request.Verbose)
                Console.WriteLine($"Restore exit code: {restoreExitCode}");

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new NullCoalesceDiagnostic(
                    NullCoalesceSeverity.Warning,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
            }

            await VerifyBuildAndTestsAsync(restoreTarget, projectGraph, exclusionInfo,
                request, diagnostics);
            DeleteBuildOutputDirectories(intermediateRoot);
        }

        return new ProjectNullCoalesceResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == NullCoalesceSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static int CountResidualNullCoalesce(string intermediateRoot)
    {
        var count = 0;
        foreach (var csFile in Directory.EnumerateFiles(
            intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csFile);
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(content).GetRoot();
            count += root.DescendantNodes().Count(n =>
                n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
                || n is AssignmentExpressionSyntax a
                    && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));
        }
        return count;
    }

    private static async Task VerifyBuildAndTestsAsync(
        string restoreTarget,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        ProjectNullCoalesceRequest request,
        List<NullCoalesceDiagnostic> diagnostics)
    {
        if (!request.VerifyBuild && !request.VerifyTests)
            return;

        Console.WriteLine($"[null-coalesce] Verifying build: dotnet build \"{restoreTarget}\" -c Release");
        var buildExit = await RunDotNetCommandAsync("build", restoreTarget, request.Verbose, "Release");
        if (buildExit != 0)
        {
            diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Error,
                $"dotnet build failed with exit code {buildExit} after null-coalesce preprocessing."));
            return;
        }

        if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
        {
            Console.WriteLine($"[null-coalesce] Verifying tests: dotnet test \"{restoreTarget}\" -c Release");
            var testExit = await RunDotNetCommandAsync("test", restoreTarget, request.Verbose, "Release");
            if (testExit != 0)
            {
                diagnostics.Add(new NullCoalesceDiagnostic(
                    NullCoalesceSeverity.Error,
                    $"dotnet test failed with exit code {testExit} after null-coalesce preprocessing."));
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
        NullCoalesceStatistics target,
        NullCoalesceStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.NullCoalescingRewritten += source.NullCoalescingRewritten;
        target.NullCoalescingAssignRewritten += source.NullCoalescingAssignRewritten;
        target.TempVariablesGenerated += source.TempVariablesGenerated;
        target.NestedRewrites += source.NestedRewrites;
        target.SkippedUnsupported += source.SkippedUnsupported;
        target.ResidualCount += source.ResidualCount;
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: PASS (0 errors, 0 warnings)

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.CLI/ProjectNullCoalescePreprocessor.cs
git commit -m "feat: add ProjectNullCoalescePreprocessor CLI wrapper"
```

---

## Task 9: CLI Verb — `null-coalesce`

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: Add `using` directive for the new namespace**

In `src/CSharpToJava.CLI/Program.cs`, after line 10 (`using CoreLocalFunctionLiftOptions = ...`), add:

```csharp
using CSharpToJava.Core.NullCoalesceRewriter;
```

- [ ] **Step 2: Add `NullCoalesceVerbOptions` to the `ParseArguments` call and `MapResult` dispatch**

In `src/CSharpToJava.CLI/Program.cs`, modify the `ParseArguments` generic list on line 30 to add `NullCoalesceVerbOptions`:

```csharp
        return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions, ForeachDeconstructionVerbOptions, NullCoalesceVerbOptions>(args)
            .MapResult(
                (ConvertOptions opts) => ConvertFile(opts),
                (ConvertProjectOptions opts) => ConvertProject(opts),
                (AnalyzeOptions opts) => AnalyzeProject(opts),
                (EliminateGotoOptions opts) => EliminateGoto(opts),
                (MakeReadOnlyOptions opts) => MakeReadonly(opts),
                (UnsignedTypeOptions opts) => UnsignedType(opts),
                (GenericPreprocessOptions opts) => GenericPreprocess(opts),
                (StripDefaultOptions opts) => StripDefault(opts),
                (LocalFunctionLiftOptions opts) => LocalFunctionLiftAsync(opts),
                (TupleToStructVerbOptions opts) => TupleToStructVerbAsync(opts),
                (StripConditionalOptions opts) => StripConditionalCommand.RunAsync(opts),
                (RecordReadOnlyVerbOptions opts) => RecordReadOnlyVerbAsync(opts),
                (ArrayMethodOptions opts) => ArrayMethod(opts),
                (ForeachDeconstructionVerbOptions opts) => ForeachDeconstructionAsync(opts),
                (NullCoalesceVerbOptions opts) => NullCoalesceAsync(opts),
                errs => Task.FromResult(1)
            );
```

- [ ] **Step 3: Add `NullCoalesce` property and `--no-null-coalesce` option to `ConvertProjectOptions`**

In the `ConvertProjectOptions` class (after the `NoForeachDeconstruction` option around line 5071), add:

```csharp
    [Option("no-null-coalesce", Default = false,
        HelpText = "Disable the default null-coalesce (?? / ??=) preprocessing step")]
    public bool NoNullCoalesce { get; set; }
```

And after `public bool ForeachDeconstruction => !NoForeachDeconstruction;` (line 5124), add:

```csharp
    public bool NullCoalesce => !NoNullCoalesce;
```

- [ ] **Step 4: Add the pipeline stage in `ConvertProject`**

In `src/CSharpToJava.CLI/Program.cs`, after the `LocalFunctionLift` block (after line 231, before the `TupleToStruct` block), insert:

```csharp
            if (opts.NullCoalesce)
            {
                var nullCoalesceResult = await ProjectNullCoalescePreprocessor.PreprocessAsync(
                    new ProjectNullCoalesceRequest
                    {
                        SourcePath = opts.Source,
                        DestinationRoot = opts.Destination,
                        Force = opts.Force,
                        Verbose = opts.Verbose,
                        FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                        CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
                        VerifyBuild = opts.VerifyBuild,
                        VerifyTests = opts.VerifyTests,
                        Strict = opts.Strict,
                    });

                Console.WriteLine(
                    $"null-coalesce: {nullCoalesceResult.Statistics.FilesTransformed} transformed, " +
                    $"{nullCoalesceResult.Statistics.FilesSkippedClean} clean, " +
                    $"{nullCoalesceResult.Statistics.NullCoalescingRewritten} ?? rewritten, " +
                    $"{nullCoalesceResult.Statistics.NullCoalescingAssignRewritten} ??= rewritten, " +
                    $"{nullCoalesceResult.Statistics.TempVariablesGenerated} temp vars, " +
                    $"{nullCoalesceResult.Statistics.ResidualCount} residual");

                foreach (var diagnostic in nullCoalesceResult.Diagnostics)
                {
                    var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
                    Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
                }

                if (!nullCoalesceResult.Success)
                    return 1;

                opts.Source = nullCoalesceResult.PreprocessedSourcePath;
                opts.FingerprintSourcePath ??= nullCoalesceResult.OriginalSourcePath;
                opts.FingerprintInputRoot ??= nullCoalesceResult.SourceRoot;
            }
```

- [ ] **Step 5: Add `NullCoalesce` to preserved destination directories**

In `GetPreservedDestinationDirectories` method (after the `LocalFunctionLift` block around line 2066), add:

```csharp
        if (opts.NullCoalesce)
        {
            preserved.Add(ProjectNullCoalescePreprocessor.IntermediateDirectoryName);
        }
```

- [ ] **Step 6: Add fingerprint token**

In `GetProjectConversionOptionTokens` method (after `"local-function-lift={opts.LocalFunctionLift}"` around line 2934), add:

```csharp
            $"null-coalesce={opts.NullCoalesce}",
```

- [ ] **Step 7: Add the `NullCoalesceAsync` handler method and `NullCoalesceVerbOptions` class**

At the end of the `Program` class (before the `ConvertProjectOptions` class definition, around line 4863), add:

```csharp
    private static async Task<int> NullCoalesceAsync(NullCoalesceVerbOptions opts)
    {
        var coreOptions = new NullCoalesceOptions(Verbose: opts.Verbose, Strict: opts.Strict);
        var rewriter = new NullCoalesceRewriter();

        // Single-file mode
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = CSharpCompilation.Create(
                "null-coalesce-standalone",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
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

            return result.Diagnostics.Any(d => d.Severity == NullCoalesceSeverity.Error) ? 1 : 0;
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

        var preprocessResult = await ProjectNullCoalescePreprocessor.PreprocessAsync(
            new ProjectNullCoalesceRequest
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
            $"null-coalesce: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.NullCoalescingRewritten} ?? rewritten, " +
            $"{preprocessResult.Statistics.NullCoalescingAssignRewritten} ??= rewritten, " +
            $"{preprocessResult.Statistics.TempVariablesGenerated} temp vars, " +
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

At the end of the file (after the last verb options class), add:

```csharp
[Verb("null-coalesce", HelpText = "Rewrite ?? and ??= operators to explicit ternary / if-assignment (single-file or project mode)")]
class NullCoalesceVerbOptions
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
    [Option("strict", Default = false, HelpText = "Treat residual ??/??= as errors instead of warnings")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after null-coalesce preprocessing")]
    public bool NoVerifyBuild { get; set; }
    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after null-coalesce preprocessing")]
    public bool NoVerifyTests { get; set; }
}
```

- [ ] **Step 8: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: PASS (0 errors, 0 warnings)

- [ ] **Step 9: Commit**

```bash
git add src/CSharpToJava.CLI/Program.cs
git commit -m "feat: wire null-coalesce verb into CLI and convert-project pipeline"
```

---

## Task 10: Integration Build Verification

**Files:**
- No new files; verifies the full solution builds

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build`
Expected: PASS (0 errors)

- [ ] **Step 2: Run all NullCoalesce unit tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~NullCoalesce" --logger "console;verbosity=normal"`
Expected: ALL PASS

- [ ] **Step 3: Commit (if any fixups were needed)**

```bash
git add -A
git commit -m "chore: integration build verification for null-coalesce preprocessor"
```
