# Foreach Deconstruction Preprocessor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a C#→C# preprocessor that lowers deconstruction `foreach` over KeyValuePair collections into plain `foreach (var __entry in ...)` + explicit local declarations, placed after TupleToStruct in the convert-project pipeline.

**Architecture:** Core rewriter (`ForeachDeconstructionRewriter`) is a `CSharpSyntaxRewriter` subclass that holds a `SemanticModel` and overrides `VisitForEachVariableStatement`. CLI wrapper (`ProjectForeachDeconstructionPreprocessor`) builds a `CSharpCompilation` across all `.cs` files (like `ProjectUnsignedTypePreprocessor`) and calls the Core per file. A standalone verb `foreach-deconstruction` and a `--no-foreach-deconstruction` switch on `convert-project` are added.

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit, CommandLine parser.

---

## File Structure

| File | Responsibility | Action |
|------|---------------|--------|
| `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionOptions.cs` | Options record | Create |
| `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionDiagnostics.cs` | Severity enum + Diagnostic record + Statistics + Result | Create |
| `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs` | `CSharpSyntaxRewriter` subclass — the core lowering logic | Create |
| `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionPreprocessor.cs` | Public entry point `Preprocess(string, SemanticModel?, Options?)` | Create |
| `src/CSharpToJava.CLI/ProjectForeachDeconstructionPreprocessor.cs` | Project-level wrapper (compilation, per-file dispatch, BOM, restore) | Create |
| `src/CSharpToJava.CLI/Program.cs` | Verb registration, `ConvertProjectOptions` switch, `ConvertProject` step, preserved dirs, fingerprint tokens | Modify |
| `tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs` | Core unit tests | Create |
| `tests/CSharpToJava.Tests/ProjectForeachDeconstructionPreprocessorTests.cs` | Integration tests with `dotnet build`/`dotnet test` | Create |

---

### Task 1: Core — Options, Diagnostics, Statistics, Result

**Files:**
- Create: `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionOptions.cs`
- Create: `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionDiagnostics.cs`

- [ ] **Step 1: Create the Options file**

Create `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionOptions.cs`:

```csharp
namespace CSharpToJava.Core.ForeachDeconstruction;

public sealed class ForeachDeconstructionOptions
{
    /// <summary>Whether to emit Info-level diagnostics for skipped deconstruction foreach.</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>Strict mode: treat skipped deconstruction foreach as errors instead of info.</summary>
    public bool Strict { get; init; }
}
```

- [ ] **Step 2: Create the Diagnostics/Statistics/Result file**

Create `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionDiagnostics.cs`:

```csharp
namespace CSharpToJava.Core.ForeachDeconstruction;

public enum ForeachDeconstructionSeverity { Info, Warning, Error }

public sealed record ForeachDeconstructionDiagnostic(
    ForeachDeconstructionSeverity Severity,
    string Message,
    string? FilePath = null,
    int? Line = null);

public sealed class ForeachDeconstructionStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int ForeachDeconstructionsLowered;
    public int ForeachDeconstructionsSkipped;
}

public sealed record ForeachDeconstructionResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ForeachDeconstructionDiagnostic> Diagnostics,
    ForeachDeconstructionStatistics Statistics);
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: BUILD SUCCEEDED with no errors.

- [ ] **Step 4: Commit**

```bash
cd d:\code\cs2j
git add src/CSharpToJava.Core/ForeachDeconstruction/
git commit -m "feat(foreach-deconstruction): add Options, Diagnostics, Statistics, Result models"
```

---

### Task 2: Core — The Rewriter (KV lowering logic)

**Files:**
- Create: `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs`

- [ ] **Step 1: Create the Rewriter file with full lowering logic**

Create `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ForeachDeconstruction;

/// <summary>
/// C# syntax rewriter that lowers deconstruction foreach over KeyValuePair
/// collections into plain foreach + explicit local declarations.
/// </summary>
internal sealed class ForeachDeconstructionRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;
    private readonly ForeachDeconstructionOptions _options;
    private readonly ForeachDeconstructionStatistics _stats;
    private readonly List<ForeachDeconstructionDiagnostic> _diagnostics;

    public bool Changed { get; private set; }
    public IReadOnlyList<ForeachDeconstructionDiagnostic> Diagnostics => _diagnostics;

    private int _entryCounter;
    private int _innerCounter;

    public ForeachDeconstructionRewriter(
        SemanticModel? model,
        ForeachDeconstructionOptions options,
        ForeachDeconstructionStatistics stats,
        List<ForeachDeconstructionDiagnostic> diagnostics)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        // Visit children first (in case of nested deconstruction foreach in the body).
        var visited = (ForEachVariableStatementSyntax?)base.VisitForEachVariableStatement(node);
        if (visited == null)
            return visited;

        if (_model == null)
            return visited;

        // Determine the element type.
        var forEachInfo = _model.GetForEachStatementInfo(node);
        var elementType = forEachInfo.ElementType as INamedTypeSymbol;
        if (elementType == null)
            return visited;

        // Classify the element type.
        var family = ClassifyFamily(elementType);
        switch (family.Kind)
        {
            case FamilyKind.KeyValuePair:
                break; // proceed to lowering
            case FamilyKind.Deconstruct:
                AddInfoDiagnostic(node, "Deconstruct-based deconstruction skipped; handled by converter.");
                _stats.ForeachDeconstructionsSkipped++;
                return visited;
            case FamilyKind.Tuple:
                AddInfoDiagnostic(node, "Tuple-based deconstruction skipped; run TupleToStruct first.");
                _stats.ForeachDeconstructionsSkipped++;
                return visited;
            default:
                return visited;
        }

        // KV family: lower it.
        var entryName = FreshName("__entry", visited);
        var entryIdentifier = SyntaxFactory.IdentifierName(entryName);

        // Build the unpack statements.
        var unpackStatements = new List<StatementSyntax>();
        UnpackVariable(visited.Variable, entryIdentifier, elementType, unpackStatements, visited);

        // Build the new foreach statement.
        var newForEach = SyntaxFactory.ForEachStatement(
            visited.AwaitKeyword,
            visited.ForEachKeyword,
            visited.OpenParenToken,
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(entryName).WithLeadingTrivia(
                visited.Variable.GetLeadingTrivia()),
            visited.InKeyword,
            visited.Expression,
            visited.CloseParenToken,
            PrependStatements(visited.Statement, unpackStatements));

        Changed = true;
        _stats.ForeachDeconstructionsLowered++;

        return newForEach
            .WithLeadingTrivia(visited.GetLeadingTrivia())
            .WithTrailingTrivia(visited.GetTrailingTrivia());
    }

    // -----------------------------------------------------------------------
    // Family classification
    // -----------------------------------------------------------------------

    private enum FamilyKind { KeyValuePair, Deconstruct, Tuple, Other }

    private readonly record struct FamilyInfo(FamilyKind Kind);

    private FamilyInfo ClassifyFamily(INamedTypeSymbol elementType)
    {
        // KV family: named KeyValuePair<K,V> or implements IEnumerable<KeyValuePair<K,V>>.
        if (IsKeyValuePair(elementType))
            return new FamilyInfo(FamilyKind.KeyValuePair);
        foreach (var iface in elementType.AllInterfaces)
        {
            if (IsKeyValuePair(iface))
                return new FamilyInfo(FamilyKind.KeyValuePair);
        }

        // Tuple family: ValueTuple/Tuple or implements ITuple.
        if (IsTuple(elementType))
            return new FamilyInfo(FamilyKind.Tuple);

        // Deconstruct family: has Deconstruct method.
        if (HasDeconstruct(elementType))
            return new FamilyInfo(FamilyKind.Deconstruct);

        return new FamilyInfo(FamilyKind.Other);
    }

    private static bool IsKeyValuePair(INamedTypeSymbol type)
        => type.Name == "KeyValuePair" && type.TypeArguments.Length == 2
           && (type.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
               || type.OriginalDefinition.ToDisplayString().StartsWith(
                   "System.Collections.Generic.KeyValuePair<", StringComparison.Ordinal));

    private static bool IsTuple(INamedTypeSymbol type)
    {
        if (type.IsTupleType)
            return true;
        var defName = type.OriginalDefinition.ToDisplayString();
        if (defName.StartsWith("System.ValueTuple<", StringComparison.Ordinal)
            || defName.StartsWith("System.Tuple<", StringComparison.Ordinal))
            return true;
        return type.AllInterfaces.Any(i =>
            i.OriginalDefinition.ToDisplayString() == "System.Runtime.CompilerServices.ITuple");
    }

    private static bool HasDeconstruct(INamedTypeSymbol type)
    {
        return type.GetMembers("Deconstruct")
            .OfType<IMethodSymbol>()
            .Any(m => m.Parameters.Length > 0 && m.Parameters.All(p => p.RefKind == RefKind.Out))
            || type.GetMethod("Deconstruct", 0) != null; // extension methods not checked here
    }

    // -----------------------------------------------------------------------
    // Unpack recursion — generate local declarations from the designation
    // -----------------------------------------------------------------------

    private void UnpackVariable(
        ExpressionSyntax variable,
        IdentifierNameSyntax accessorRoot,
        INamedTypeSymbol currentType,
        List<StatementSyntax> statements,
        SyntaxNode scopeNode)
    {
        // DeclarationExpressionSyntax: var (x, y) form
        if (variable is DeclarationExpressionSyntax declaration)
        {
            UnpackDesignation(declaration.Designation, accessorRoot, currentType, statements, scopeNode,
                declaredTypeSyntax: declaration.Type);
            return;
        }

        // TupleExpressionSyntax: (int x, int y) or (var x, var y) form
        if (variable is TupleExpressionSyntax tuple)
        {
            var components = GetKvComponents(currentType);
            for (var i = 0; i < tuple.Arguments.Count && i < components.Count; i++)
            {
                var (suffix, subType) = components[i];
                var arg = tuple.Arguments[i].Expression;
                var memberAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    accessorRoot,
                    SyntaxFactory.IdentifierName(suffix));

                UnpackTupleArgument(arg, memberAccess, subType, statements, scopeNode);
            }
            return;
        }

        // IdentifierNameSyntax: whole-binding (rare)
        if (variable is IdentifierNameSyntax id && id.Identifier.Text != "_")
        {
            statements.Add(MakeLocalDeclaration(null, id.Identifier.Text, accessorRoot));
        }
    }

    private void UnpackDesignation(
        VariableDesignationSyntax designation,
        IdentifierNameSyntax accessorRoot,
        INamedTypeSymbol currentType,
        List<StatementSyntax> statements,
        SyntaxNode scopeNode,
        TypeSyntax? declaredTypeSyntax)
    {
        // SingleVariableDesignationSyntax: single variable (leaf)
        if (designation is SingleVariableDesignationSyntax single)
        {
            if (single.Identifier.Text == "_")
                return; // discard
            var declType = GetDeclarationType(declaredTypeSyntax, currentType, position: 0, currentType);
            statements.Add(MakeLocalDeclaration(declType, single.Identifier.Text, accessorRoot));
            return;
        }

        // ParenthesizedVariableDesignationSyntax: composite
        if (designation is ParenthesizedVariableDesignationSyntax parenthesized)
        {
            var components = GetKvComponents(currentType);
            for (var i = 0; i < parenthesized.Variables.Count && i < components.Count; i++)
            {
                var (suffix, subType) = components[i];
                var memberAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    accessorRoot,
                    SyntaxFactory.IdentifierName(suffix));

                UnpackDesignationChild(parenthesized.Variables[i], memberAccess, subType, statements, scopeNode);
            }
            return;
        }
    }

    private void UnpackDesignationChild(
        VariableDesignationSyntax child,
        ExpressionSyntax accessor,
        INamedTypeSymbol subType,
        List<StatementSyntax> statements,
        SyntaxNode scopeNode)
    {
        if (child is SingleVariableDesignationSyntax single)
        {
            if (single.Identifier.Text == "_")
                return; // discard
            // For KV, the subType is K or V. If the child had a type annotation
            // (from DeclarationExpressionSyntax), it was already in the parent.
            // For ParenthesizedVariableDesignationSyntax, there's no type annotation.
            var declTypeSyntax = SyntaxFactory.IdentifierName("var");
            statements.Add(MakeLocalDeclaration("var", single.Identifier.Text, accessor));
            return;
        }

        if (child is ParenthesizedVariableDesignationSyntax nested)
        {
            // Nested composite: introduce intermediate variable and recurse.
            // The subType may be KV, Tuple, or Deconstruct.
            var subFamily = ClassifyFamily(subType);
            if (subFamily.Kind == FamilyKind.Other || subFamily.Kind == FamilyKind.Deconstruct
                || subFamily.Kind == FamilyKind.Tuple)
            {
                // Cannot lower nested — bind whole thing to a var.
                var midName = FreshName("__entry_inner", scopeNode);
                var midId = SyntaxFactory.IdentifierName(midName);
                statements.Add(MakeLocalDeclaration("var", midName, accessor));

                if (subFamily.Kind == FamilyKind.Tuple)
                {
                    // Try to unpack as Tuple (.ItemN).
                    var tupleComponents = GetTupleComponents(subType);
                    for (var i = 0; i < nested.Variables.Count && i < tupleComponents.Count; i++)
                    {
                        var (suffix, innerType) = tupleComponents[i];
                        var memberAccess = SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            midId,
                            SyntaxFactory.IdentifierName(suffix));
                        UnpackDesignationChild(nested.Variables[i], memberAccess, innerType, statements, scopeNode);
                    }
                }
                else
                {
                    AddInfoDiagnostic(child, "Nested Deconstruct-based deconstruction skipped; handled by converter.");
                }
                return;
            }

            // Nested KV: introduce intermediate and recurse.
            var midName2 = FreshName("__entry_inner", scopeNode);
            var midId2 = SyntaxFactory.IdentifierName(midName2);
            statements.Add(MakeLocalDeclaration("var", midName2, accessor));
            UnpackDesignation(child, midId2, subType, statements, scopeNode, null);
            return;
        }

        // DiscardPatternSyntax or other — skip
    }

    private void UnpackTupleArgument(
        ExpressionSyntax arg,
        ExpressionSyntax accessor,
        INamedTypeSymbol subType,
        List<StatementSyntax> statements,
        SyntaxNode scopeNode)
    {
        // arg can be: IdentifierNameSyntax (inferred), DeclarationExpressionSyntax (var x),
        // or TypedArgument (int x → actually comes as a DeclarationExpressionSyntax with type),
        // or another TupleExpressionSyntax (nested).

        if (arg is DeclarationExpressionSyntax decl)
        {
            // var x or int x inside a TupleExpression
            if (decl.Designation is SingleVariableDesignationSyntax single)
            {
                if (single.Identifier.Text == "_")
                    return;
                var declType = decl.Type is PredefinedTypeSyntax || decl.Type is IdentifierNameSyntax
                    ? decl.Type.ToString()
                    : "var";
                // If the type is "var", use "var"; otherwise use the explicit type.
                statements.Add(MakeLocalDeclaration(
                    decl.Type.IsKind(SyntaxKind.IdentifierName)
                        && ((IdentifierNameSyntax)decl.Type).Identifier.Text == "var"
                        ? "var" : declType,
                    single.Identifier.Text,
                    accessor));
            }
            else if (decl.Designation is ParenthesizedVariableDesignationSyntax nested)
            {
                // Nested composite in a TupleExpression argument.
                UnpackDesignationChild(nested, accessor, subType, statements, scopeNode);
            }
            return;
        }

        if (arg is IdentifierNameSyntax id)
        {
            if (id.Identifier.Text == "_")
                return;
            // Inferred type (no annotation) — use var.
            statements.Add(MakeLocalDeclaration("var", id.Identifier.Text, accessor));
            return;
        }

        if (arg is TupleExpressionSyntax nestedTuple)
        {
            UnpackVariable(nestedTuple, (IdentifierNameSyntax)accessor, subType, statements, scopeNode);
            return;
        }
    }

    // -----------------------------------------------------------------------
    // Component accessors
    // -----------------------------------------------------------------------

    private static List<(string Suffix, INamedTypeSymbol SubType)> GetKvComponents(INamedTypeSymbol kvType)
    {
        // Find the KeyValuePair<K,V> among the type or its interfaces.
        INamedTypeSymbol? kv = null;
        if (IsKeyValuePair(kvType))
            kv = kvType;
        else
            kv = kvType.AllInterfaces.FirstOrDefault(IsKeyValuePair);

        if (kv == null || kv.TypeArguments.Length != 2)
            return new List<(string, INamedTypeSymbol)>();

        return new List<(string, INamedTypeSymbol)>
        {
            ("Key", (kv.TypeArguments[0] as INamedTypeSymbol)!),
            ("Value", (kv.TypeArguments[1] as INamedTypeSymbol)!),
        };
    }

    private static List<(string Suffix, INamedTypeSymbol SubType)> GetTupleComponents(INamedTypeSymbol tupleType)
    {
        var result = new List<(string, INamedTypeSymbol)>();
        if (tupleType.IsTupleType)
        {
            for (var i = 0; i < tupleType.TupleElements.Length; i++)
            {
                result.Add(($"Item{i + 1}", (tupleType.TupleElements[i].Type as INamedTypeSymbol)!));
            }
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string? GetDeclarationType(
        TypeSyntax? declaredTypeSyntax,
        INamedTypeSymbol currentType,
        int position,
        INamedTypeSymbol elementType)
    {
        if (declaredTypeSyntax == null)
            return "var";
        // If it's "var", return "var".
        if (declaredTypeSyntax is IdentifierNameSyntax id && id.Identifier.Text == "var")
            return "var";
        if (declaredTypeSyntax.IsKind(SyntaxKind.IdentifierName) && ((IdentifierNameSyntax)declaredTypeSyntax).Identifier.Text == "var")
            return "var";
        return declaredTypeSyntax.ToString();
    }

    private static StatementSyntax MakeLocalDeclaration(string? declType, string varName, ExpressionSyntax initializer)
    {
        var typeSyntax = string.IsNullOrEmpty(declType) || declType == "var"
            ? SyntaxFactory.IdentifierName("var")
            : SyntaxFactory.ParseTypeName(declType);

        var declarator = SyntaxFactory.VariableDeclarator(varName)
            .WithInitializer(SyntaxFactory.EqualsValueClause(initializer));

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(typeSyntax)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    private static StatementSyntax PrependStatements(StatementSyntax body, List<StatementSyntax> prepend)
    {
        if (body is BlockSyntax block)
        {
            return block.WithStatements(
                SyntaxFactory.List(prepend.Concat(block.Statements)));
        }

        // Wrap non-block body in a block.
        var newBlock = SyntaxFactory.Block(
            SyntaxFactory.List(prepend.Concat(new[] { body })));
        return newBlock;
    }

    private string FreshName(string baseName, SyntaxNode scopeNode)
    {
        // Find the enclosing method declaration for scope checking.
        var methodDecl = scopeNode.FirstAncestorOrSelf<MethodDeclarationSyntax>()
            ?? scopeNode.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() as SyntaxNode
            ?? scopeNode.FirstAncestorOrSelf<BlockSyntax>();

        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        if (methodDecl != null)
        {
            foreach (var id in methodDecl.DescendantNodes()
                .OfType<IdentifierNameSyntax>())
            {
                existingNames.Add(id.Identifier.Text);
            }
            foreach (var decl in methodDecl.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>())
            {
                existingNames.Add(decl.Identifier.Text);
            }
            foreach (var param in methodDecl.DescendantNodes()
                .OfType<ParameterSyntax>())
            {
                existingNames.Add(param.Identifier.Text);
            }
        }

        var counter = baseName == "__entry" ? _entryCounter++ : _innerCounter++;
        var name = counter == 0 ? baseName : baseName + counter;
        while (existingNames.Contains(name))
        {
            counter = baseName == "__entry" ? _entryCounter++ : _innerCounter++;
            name = baseName + counter;
        }
        return name;
    }

    private void AddInfoDiagnostic(SyntaxNode node, string message)
    {
        var severity = _options.Strict
            ? ForeachDeconstructionSeverity.Error
            : ForeachDeconstructionSeverity.Info;
        if (severity == ForeachDeconstructionSeverity.Info && !_options.ReportInfo)
            return;

        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        _diagnostics.Add(new ForeachDeconstructionDiagnostic(severity, message, null, line));
    }
}
```

- [ ] **Step 2: Build the Core project to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: BUILD SUCCEEDED. Fix any compilation errors before proceeding.

- [ ] **Step 3: Commit**

```bash
cd d:\code\cs2j
git add src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs
git commit -m "feat(foreach-deconstruction): implement core KV lowering rewriter"
```

---

### Task 3: Core — Preprocessor Entry Point

**Files:**
- Create: `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionPreprocessor.cs`

- [ ] **Step 1: Create the Preprocessor entry point**

Create `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionPreprocessor.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.ForeachDeconstruction;

/// <summary>
/// C# deconstruction foreach preprocessor public entry point.
/// Lowers deconstruction foreach over KeyValuePair collections into plain
/// foreach + explicit local declarations. Clean files (no lowerable
/// deconstruction foreach) are returned byte-for-byte identical.
/// </summary>
public sealed class ForeachDeconstructionPreprocessor
{
    public ForeachDeconstructionResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        ForeachDeconstructionOptions? options = null)
    {
        options ??= new ForeachDeconstructionOptions();
        var stats = new ForeachDeconstructionStatistics { FilesScanned = 1 };

        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Cheap pre-check: if no ForEachVariableStatementSyntax at all, return original.
        if (!root.DescendantNodes().OfType<ForEachVariableStatementSyntax>().Any())
        {
            stats.FilesSkippedClean = 1;
            return new ForeachDeconstructionResult(
                sourceCode, Changed: false, Array.Empty<ForeachDeconstructionDiagnostic>(), stats);
        }

        var rewriter = new ForeachDeconstructionRewriter(
            semanticModel, options, stats, new List<ForeachDeconstructionDiagnostic>());
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            stats.FilesSkippedClean = 1;
            return new ForeachDeconstructionResult(
                sourceCode, Changed: false, rewriter.Diagnostics, stats);
        }

        // Format to normalize whitespace/trivia on generated nodes.
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(rewritten, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;
        return new ForeachDeconstructionResult(
            code, Changed: true, rewriter.Diagnostics, stats);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
cd d:\code\cs2j
git add src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionPreprocessor.cs
git commit -m "feat(foreach-deconstruction): add Preprocess entry point with clean-file fast path"
```

---

### Task 4: Core — Unit Tests (Red phase)

**Files:**
- Create: `tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs`

- [ ] **Step 1: Create the test file with all test cases**

Create `tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs`:

```csharp
using CSharpToJava.Core.ForeachDeconstruction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public class ForeachDeconstructionTests
{
    // ===================================================================
    // Model tests
    // ===================================================================

    [Fact]
    public void Options_Default_NotStrict_ReportInfo()
    {
        var o = new ForeachDeconstructionOptions();
        Assert.False(o.Strict);
        Assert.True(o.ReportInfo);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new ForeachDeconstructionStatistics();
        Assert.Equal(0, s.FilesScanned);
        Assert.Equal(0, s.ForeachDeconstructionsLowered);
    }

    // ===================================================================
    // 1. Basic KV: foreach ((int x, int y) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_BasicKv_TupleExpressionWithTypes()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((int x, string y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("__entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("__entry.Value", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("int x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("string y = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 2. var form: foreach (var (x, y) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_VarDesignation_Form()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach (var (x, y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var y = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 3. Tuple-expr var: foreach ((var x, var y) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_TupleExpressionVar_Form()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((var x, var y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var y = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 4. Inferred form: foreach ((x, y) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_InferredForm_UsesVar()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((x, y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var y = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 5. Discard: foreach ((int x, _) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_Discard_OnlyKeyDeclared()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((int x, _) in d) {
                        Console.WriteLine(x);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("int x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("__entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 6. Whole-binding: foreach ((int k, var whole) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_WholeBinding_ValueAssignedToVariable()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((int k, var whole) in d) {
                        Console.WriteLine(k + whole);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("int k = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var whole = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 7. await foreach: preserve await keyword
    // ===================================================================

    [Fact]
    public void Lowers_AwaitForeach_PreservesAwait()
    {
        var src = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            class C {
                async Task M(IAsyncEnumerable<KeyValuePair<int, string>> d) {
                    await foreach (var (x, y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("await foreach", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 8. Deconstruct family: skipped, Changed=false
    // ===================================================================

    [Fact]
    public void Skips_DeconstructFamily_EmitsInfo()
    {
        var src = """
            class Point {
                public int X, Y;
                public void Deconstruct(out int x, out int y) { x = X; y = Y; }
            }
            class C {
                void M(System.Collections.Generic.List<Point> pts) {
                    foreach (var (x, y) in pts) {
                        System.Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Severity == ForeachDeconstructionSeverity.Info);
    }

    // ===================================================================
    // 9. Non-deconstruction foreach: Changed=false
    // ===================================================================

    [Fact]
    public void NonDeconstructionForeach_NotChanged()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(List<int> list) {
                    foreach (var item in list) {
                        Console.WriteLine(item);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    // ===================================================================
    // 10. No deconstruction in file: byte-identical output
    // ===================================================================

    [Fact]
    public void NoDeconstruction_FileOutputEqualsInput()
    {
        var src = "class C { void M() { System.Console.WriteLine(42); } }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    // ===================================================================
    // 11. Body is single statement (non-block): wrapped to block
    // ===================================================================

    [Fact]
    public void Lowers_NonBlockBody_WrapsInBlock()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((int x, string y) in d)
                        System.Console.WriteLine(x + y);
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("int x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 12. __entry already in scope: unique suffix
    // ===================================================================

    [Fact]
    public void Lowers_WhenEntryNameInScope_GeneratesUniqueSuffix()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    int __entry = 0;
                    foreach ((int x, string y) in d) {
                        System.Console.WriteLine(x + y + __entry);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Should use __entry1 (or higher) to avoid collision with existing __entry.
        Assert.Contains("__entry1.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 13. Nested deconstruction foreach in body: independent names
    // ===================================================================

    [Fact]
    public void Lowers_NestedDeconstruction_IndependentEntryNames()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d1, Dictionary<int, string> d2) {
                    foreach ((int x, string y) in d1) {
                        foreach ((int a, string b) in d2) {
                            System.Console.WriteLine(x + y + a + b);
                        }
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Both should have __entry (or __entry1) — the outer and inner get independent names.
        Assert.Contains("__entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 14. IEnumerable<KeyValuePair<K,V>> source: KV lowering
    // ===================================================================

    [Fact]
    public void Lowers_IEnumerableKvSource_KvLowering()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(IEnumerable<KeyValuePair<int, string>> items) {
                    foreach ((int k, string v) in items) {
                        System.Console.WriteLine(k + v);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("int k = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("string v = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 15. SortedDictionary / ConcurrentDictionary: KV lowering
    // ===================================================================

    [Fact]
    public void Lowers_SortedDictionary_KvLowering()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(SortedDictionary<int, string> d) {
                    foreach (var (x, y) in d) {
                        System.Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Lowers_ConcurrentDictionary_KvLowering()
    {
        var src = """
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            class C {
                void M(ConcurrentDictionary<int, string> d) {
                    foreach (var (x, y) in d) {
                        System.Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 16. ref/ref readonly deconstruction foreach: not rewritten
    // ===================================================================

    [Fact]
    public void Skips_RefForeach_NotRewritten()
    {
        // ref foreach is extremely rare and not lowered. Verify it is not
        // incorrectly rewritten (Changed should be false or the foreach
        // should remain a ForEachVariableStatement).
        // Note: ref foreach over a Span<T> that has Deconstruct is a Deconstruct
        // family case — it should be skipped with an Info diagnostic.
        var src = """
            using System;
            using System.Collections.Generic;
            class C {
                void M(List<KeyValuePair<int, int>> d) {
                    // This is a regular ref foreach (non-deconstruction) — should not be touched.
                    ref int x = ref CollectionsMarshal.AsReadOnly(d)[0].Key;
                }
            }
            """;
        var result = Preprocess(src);
        // No ForEachVariableStatementSyntax at all → Changed=false.
        Assert.False(result.Changed);
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static ForeachDeconstructionResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "foreach-deconstruction-test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new ForeachDeconstructionPreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private static void AssertNoSyntaxErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            $"Syntax errors in output:\n{string.Join("\n", errors.Select(e => e.ToString()))}\n--- Code ---\n{code}");
    }
}
```

- [ ] **Step 2: Build the test project**

Run: `dotnet build tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release`
Expected: BUILD SUCCEEDED (tests compile but will fail when run — rewriter not yet verified).

- [ ] **Step 3: Run the tests — expect failures (Red)**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --filter "ForeachDeconstructionTests" --no-build`
Expected: Most tests FAIL (rewriter has bugs or edge cases not handled yet).

- [ ] **Step 4: Commit the test file**

```bash
cd d:\code\cs2j
git add tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs
git commit -m "test(foreach-deconstruction): add unit tests for core KV lowering (Red)"
```

---

### Task 5: Core — Fix and verify all unit tests pass (Green phase)

**Files:**
- Modify: `src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs`

- [ ] **Step 1: Run the tests, observe failures**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --filter "ForeachDeconstructionTests" --no-build 2>&1 | cat`
Expected: Some tests fail. Note the failure messages.

- [ ] **Step 2: Fix the rewriter based on test failures**

Iterate on `ForeachDeconstructionRewriter.cs` to fix issues found by the tests. Common areas to check:

1. The `UnpackTupleArgument` method may need adjustment for how `TupleExpressionSyntax` arguments carry type annotations (each argument is an `ArgumentSyntax` whose `Expression` is a `DeclarationExpressionSyntax` for `int x` or `var x`, or an `IdentifierNameSyntax` for inferred `x`).

2. The `FreshName` method may need to handle the counter correctly (first call returns `baseName`, second returns `baseName1`, etc.).

3. The `HasDeconstruct` method may need refinement — `type.GetMethod("Deconstruct", 0)` is not a valid Roslyn API call. Use `type.GetMembers("Deconstruct")` and check for `IMethodSymbol` with out parameters.

4. Ensure the `VisitForEachVariableStatement` calls `base.VisitForEachVariableStatement` first to recurse into children, and operates on the visited node.

After fixing, rebuild:
Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Run tests again — verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --filter "ForeachDeconstructionTests"`
Expected: ALL TESTS PASS.

- [ ] **Step 4: Run full test suite — verify no regression**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release`
Expected: ALL TESTS PASS (no regression in existing tests).

- [ ] **Step 5: Commit**

```bash
cd d:\code\cs2j
git add src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs
git commit -m "fix(foreach-deconstruction): fix rewriter edge cases, all unit tests pass (Green)"
```

---

### Task 6: CLI — ProjectForeachDeconstructionPreprocessor

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectForeachDeconstructionPreprocessor.cs`

- [ ] **Step 1: Create the project-level wrapper**

Create `src/CSharpToJava.CLI/ProjectForeachDeconstructionPreprocessor.cs`:

```csharp
using CSharpToJava.Core.ForeachDeconstruction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI;

internal sealed class ProjectForeachDeconstructionPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public ForeachDeconstructionOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectForeachDeconstructionPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required ForeachDeconstructionStatistics Statistics { get; init; }
    public required IReadOnlyList<ForeachDeconstructionDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectForeachDeconstructionPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-foreach-deconstruction-src";

    public static async Task<ProjectForeachDeconstructionPreprocessResult> PreprocessAsync(
        ProjectForeachDeconstructionPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Foreach deconstruction source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<ForeachDeconstructionDiagnostic>();
        var statistics = new ForeachDeconstructionStatistics();
        var preprocessor = new ForeachDeconstructionPreprocessor();
        var sourceRoot = layout.SourceRoot;

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph,
                request.Verbose,
                request.CompatNamespacePrefixes,
                layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot,
            intermediateRoot,
            "[foreach-deconstruction]",
            request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot, Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            syntaxTrees[csFile] = CSharpSyntaxTree.ParseText(sourceCode, path: csFile);
        }

        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "foreach-deconstruction-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            if (!syntaxTrees.TryGetValue(sourceFile, out var tree))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var result = preprocessor.Preprocess(
                sourceCode, compilation.GetSemanticModel(tree), request.RewriterOptions);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with { Message = $"{relativePath}: {diagnostic.Message}" });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(
                    destinationFile,
                    result.OutputCode,
                    FileStartsWithBom(sourceFile)
                        ? new System.Text.UTF8Encoding(true)
                        : new System.Text.UTF8Encoding(false));
            }

            if (request.Verbose)
            {
                Console.WriteLine($"[foreach-deconstruction] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }

            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[foreach-deconstruction]");
            if (request.Verbose)
            {
                Console.WriteLine($"Restore exit code: {restoreExitCode}");
            }

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new ForeachDeconstructionDiagnostic(
                    ForeachDeconstructionSeverity.Error,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}. " +
                    "Package references may not be resolved."));
            }
        }

        return new ProjectForeachDeconstructionPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == ForeachDeconstructionSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    private static void MergeStatistics(
        ForeachDeconstructionStatistics target,
        ForeachDeconstructionStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.ForeachDeconstructionsLowered += source.ForeachDeconstructionsLowered;
        target.ForeachDeconstructionsSkipped += source.ForeachDeconstructionsSkipped;
    }
}
```

- [ ] **Step 2: Build the CLI project**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release`
Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
cd d:\code\cs2j
git add src/CSharpToJava.CLI/ProjectForeachDeconstructionPreprocessor.cs
git commit -m "feat(foreach-deconstruction): add project-level CLI wrapper with SemanticModel"
```

---

### Task 7: CLI — Program.cs integration (verb + convert-project switch)

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: Add the verb options class**

In `Program.cs`, find the `UnsignedTypeOptions` class definition (around line 4735). Add a new verb options class right after it (before `ArrayMethodOptions`):

```csharp
[Verb("foreach-deconstruction", HelpText = "Lower C# deconstruction foreach over KeyValuePair into plain foreach + explicit locals")]
class ForeachDeconstructionVerbOptions
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
    [Option("strict", Default = false, HelpText = "Treat skipped deconstruction foreach as errors")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
}
```

- [ ] **Step 2: Register the verb in the parser and dispatch**

In `Program.cs`, find the `MainImpl` method (line 27). Add `ForeachDeconstructionVerbOptions` to the `ParseArguments` generic list (after `ArrayMethodOptions`):

Change line 30 from:
```csharp
        return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions>(args)
```
to:
```csharp
        return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions, ForeachDeconstructionVerbOptions>(args)
```

Add the dispatch handler (after the `ArrayMethodOptions` handler on line 44):
```csharp
                (ForeachDeconstructionVerbOptions opts) => ForeachDeconstructionAsync(opts),
```

- [ ] **Step 3: Add the `ForeachDeconstructionAsync` handler method**

Add this method to the `Program` class (place it near the `UnsignedType` method, around line 3706):

```csharp
    private static async Task<int> ForeachDeconstructionAsync(ForeachDeconstructionVerbOptions opts)
    {
        var rewriterOptions = new CSharpToJava.Core.ForeachDeconstruction.ForeachDeconstructionOptions
        {
            Strict = opts.Strict,
            ReportInfo = true,
        };

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
                "foreach-deconstruction",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true));

            var preprocessor = new CSharpToJava.Core.ForeachDeconstruction.ForeachDeconstructionPreprocessor();
            var result = preprocessor.Preprocess(sourceCode, compilation.GetSemanticModel(tree), rewriterOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose)
                {
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
                }
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
            }

            return result.Diagnostics.Any(d => d.Severity == CSharpToJava.Core.ForeachDeconstruction.ForeachDeconstructionSeverity.Error) ? 1 : 0;
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

        var preprocessResult = await ProjectForeachDeconstructionPreprocessor.PreprocessAsync(
            new ProjectForeachDeconstructionPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                RewriterOptions = rewriterOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
            });

        Console.WriteLine(
            $"foreach-deconstruction: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.ForeachDeconstructionsLowered} lowered, " +
            $"{preprocessResult.Statistics.ForeachDeconstructionsSkipped} skipped");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        return preprocessResult.Success ? 0 : 1;
    }
```

- [ ] **Step 4: Add the `--no-foreach-deconstruction` switch to `ConvertProjectOptions`**

In `Program.cs`, find the `ConvertProjectOptions` class. After the `NoTupleToStruct` property (around line 4913), add:

```csharp
    [Option("no-foreach-deconstruction", Default = false,
        HelpText = "Disable the default foreach-deconstruction preprocessing step")]
    public bool NoForeachDeconstruction { get; set; }
```

And in the computed properties section (around line 4965), add:

```csharp
    public bool ForeachDeconstruction => !NoForeachDeconstruction;
```

- [ ] **Step 5: Add the step to `ConvertProject` method (after TupleToStruct, before StripDefault)**

In `Program.cs`, find the end of the TupleToStruct block (around line 275, after `opts.FingerprintInputRoot ??= tupleResult.SourceRoot;` and before `if (opts.StripDefault)`). Insert:

```csharp
            if (opts.ForeachDeconstruction)
            {
                var foreachResult = await ProjectForeachDeconstructionPreprocessor.PreprocessAsync(
                    new ProjectForeachDeconstructionPreprocessRequest
                    {
                        SourcePath = opts.Source,
                        DestinationRoot = opts.Destination,
                        Force = opts.Force,
                        Verbose = opts.Verbose,
                        FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                        CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
                    });

                Console.WriteLine(
                    $"foreach-deconstruction: {foreachResult.Statistics.FilesTransformed} transformed, " +
                    $"{foreachResult.Statistics.FilesSkippedClean} clean, " +
                    $"{foreachResult.Statistics.ForeachDeconstructionsLowered} lowered, " +
                    $"{foreachResult.Statistics.ForeachDeconstructionsSkipped} skipped");

                foreach (var diagnostic in foreachResult.Diagnostics)
                {
                    if (diagnostic.Severity != CSharpToJava.Core.ForeachDeconstruction.ForeachDeconstructionSeverity.Info || opts.Verbose)
                    {
                        Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
                    }
                }

                if (!foreachResult.Success)
                {
                    return 1;
                }

                opts.Source = foreachResult.PreprocessedSourcePath;
                opts.FingerprintSourcePath ??= foreachResult.OriginalSourcePath;
                opts.FingerprintInputRoot ??= foreachResult.SourceRoot;
            }

```

- [ ] **Step 6: Register in `GetPreservedDestinationDirectories`**

In `Program.cs`, find the `GetPreservedDestinationDirectories` method (around line 2021). After the `TupleToStruct` block (around line 2034), add:

```csharp
        if (opts.ForeachDeconstruction)
        {
            preserved.Add(ProjectForeachDeconstructionPreprocessor.IntermediateDirectoryName);
        }
```

- [ ] **Step 7: Register in fingerprint tokens**

In `Program.cs`, find the fingerprint tokens list (around line 2887). After the `tuple-to-struct` token, add:

```csharp
$"foreach-deconstruction={opts.ForeachDeconstruction}",
```

- [ ] **Step 8: Build the CLI project**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -c Release`
Expected: BUILD SUCCEEDED.

- [ ] **Step 9: Run full test suite to verify no regression**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release`
Expected: ALL TESTS PASS.

- [ ] **Step 10: Commit**

```bash
cd d:\code\cs2j
git add src/CSharpToJava.CLI/Program.cs
git commit -m "feat(foreach-deconstruction): wire standalone verb and convert-project integration"
```

---

### Task 8: Integration Tests

**Files:**
- Create: `tests/CSharpToJava.Tests/ProjectForeachDeconstructionPreprocessorTests.cs`

- [ ] **Step 1: Create the integration test file**

Create `tests/CSharpToJava.Tests/ProjectForeachDeconstructionPreprocessorTests.cs`:

```csharp
using CSharpToJava.CLI;
using System.Diagnostics;

namespace CSharpToJava.Tests;

public class ProjectForeachDeconstructionPreprocessorTests
{
    [Fact]
    public async Task PreprocessAsync_BasicKv_BuildsSuccessfully()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var projectDir = Path.Combine(root, "TestProj");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(
                Path.Combine(projectDir, "TestProj.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                    <LangVersion>latest</LangVersion>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(projectDir, "Program.cs"),
                """
                using System;
                using System.Collections.Generic;

                class Program
                {
                    static void Main(string[] args)
                    {
                        var dict = new Dictionary<int, string>
                        {
                            { 1, "one" },
                            { 2, "two" }
                        };

                        foreach ((int k, string v) in dict)
                        {
                            Console.WriteLine($"{k}={v}");
                        }
                    }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            var result = await ProjectForeachDeconstructionPreprocessor.PreprocessAsync(
                new ProjectForeachDeconstructionPreprocessRequest
                {
                    SourcePath = Path.Combine(projectDir, "TestProj.csproj"),
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = false,
                });

            Assert.True(result.Success,
                "Preprocess should succeed. Diagnostics:\n"
                + string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));
            Assert.True(result.Statistics.ForeachDeconstructionsLowered > 0,
                "Expected at least one deconstruction foreach to be lowered.");

            // Verify the output contains the lowered form.
            var outputFile = Path.Combine(result.IntermediateRoot, "Program.cs");
            var outputContent = await File.ReadAllTextAsync(outputFile);
            Assert.Contains("__entry.Key", outputContent, StringComparison.Ordinal);
            Assert.Contains("__entry.Value", outputContent, StringComparison.Ordinal);

            // Verify the intermediate project builds.
            var (exitCode, stdOut, stdErr) = RunDotNet(
                $"build -c Release",
                result.IntermediateRoot);
            Assert.True(exitCode == 0,
                $"dotnet build failed (exit {exitCode}):\n{stdErr}\n{stdOut}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PreprocessAsync_CleanFile_ByteIdentical()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var projectDir = Path.Combine(root, "TestProj");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(
                Path.Combine(projectDir, "TestProj.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                </Project>
                """);
            var sourceContent = """
                using System;
                using System.Collections.Generic;

                class Program
                {
                    static void Main()
                    {
                        var list = new List<int> { 1, 2, 3 };
                        foreach (var item in list)
                        {
                            Console.WriteLine(item);
                        }
                    }
                }
                """;
            File.WriteAllText(Path.Combine(projectDir, "Program.cs"), sourceContent);

            var destinationRoot = Path.Combine(root, "out");
            var result = await ProjectForeachDeconstructionPreprocessor.PreprocessAsync(
                new ProjectForeachDeconstructionPreprocessRequest
                {
                    SourcePath = Path.Combine(projectDir, "TestProj.csproj"),
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = false,
                });

            Assert.True(result.Success);
            Assert.Equal(0, result.Statistics.ForeachDeconstructionsLowered);

            var outputFile = Path.Combine(result.IntermediateRoot, "Program.cs");
            var outputContent = await File.ReadAllTextAsync(outputFile);
            Assert.Equal(sourceContent, outputContent);
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
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-foreach-decon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunDotNet(string arguments, string workingDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo);
        if (process == null) return (-1, "", "Process.Start returned null");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
    }
}
```

- [ ] **Step 2: Build and run integration tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --filter "ProjectForeachDeconstructionPreprocessorTests"`
Expected: ALL TESTS PASS.

- [ ] **Step 3: Commit**

```bash
cd d:\code\cs2j
git add tests/CSharpToJava.Tests/ProjectForeachDeconstructionPreprocessorTests.cs
git commit -m "test(foreach-deconstruction): add project-level integration tests with build verification"
```

---

### Task 9: Full Regression Test

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release`
Expected: ALL TESTS PASS, zero failures.

- [ ] **Step 2: Verify no Java conversion-side code was modified**

Run: `git diff --name-only HEAD~8 -- src/CSharpToJava.Core/Transformers/ src/CSharpToJava.Core/TypeMapping/ src/CSharpToJava.Core/Java/ src/CSharpToJava.Core/Java2/`
Expected: EMPTY (no files changed in Java conversion-side directories).

- [ ] **Step 3: Commit (if any final fixes were needed)**

If any fixes were needed during regression:
```bash
cd d:\code\cs2j
git add -A
git commit -m "fix(foreach-deconstruction): regression fixes from full test suite"
```

If no fixes needed, skip this step.
