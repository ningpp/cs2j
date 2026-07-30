# ReadOnlyStructMaker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a C# source preprocessor that converts eligible structs to readonly structs with multi-level conversion (L0-L5), pattern recognition, method migration, and call-site updates.

**Architecture:** Follows the GotoEliminator module pattern — a sealed entry-point class in `CSharpToJava.Core.ReadOnlyStructMaker` namespace, with diagnostics/options/result records, CSharpSyntaxRewriter-based transformers, and a CLI verb. Analysis uses Roslyn SemanticModel for project-level call-graph construction. Rewriting is syntax-tree-level, preserving trivia.

**Tech Stack:** .NET 10, Microsoft.CodeAnalysis.CSharp 4.12.0, xUnit 2.9.3, CommandLineParser (CLI)

---

## File Structure

| Path | Responsibility |
|------|---------------|
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs` | Enums (Severity, StructPattern, ConversionLevel, MigrationType), diagnostic record, statistics class, result record |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs` | Options record with V2 flags |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/PatternRecognizer.cs` | Classify struct into Pattern A-F; produce PatternAnalysisResult |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs` | Combine pattern + call-graph → AnalyzeResult with ConversionLevel |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs` | Build method→callees map; detect field mutations, ref-this escapes |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ConstructorGenerator.cs` | Generate ctor for L3 data-container conversion |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs` | CSharpSyntaxRewriter: L1/L2/L3 struct-level rewrites + helper rewriters |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs` | L5: transform mutating methods → pure methods returning new instance |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/CallSiteUpdater.cs` | L5: update call sites (void→assign, array element, property access) |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs` | Public entry point orchestrating all phases |
| `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs` | Project-level file-walk + integration (mirrors ProjectGotoPreprocessor) |
| `src/CSharpToJava.CLI/Program.cs` | Add `make-readonly` verb + `--no-make-readonly` in ConvertProjectOptions |
| `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs` | Unit tests for L0-L3 |
| `tests/CSharpToJava.Tests/ReadOnlyStructMakerL5Tests.cs` | Unit tests for L5 method migration |

---

### Task 1: Diagnostics, Enums, and Result Types

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
using CSharpToJava.Core.ReadOnlyStructMaker;

namespace CSharpToJava.Tests;

public partial class ReadOnlyStructMakerTests
{
    [Fact]
    public void Diagnostics_Record_RoundTrip()
    {
        var d = new ReadOnlyStructMakerDiagnostic(
            ReadOnlyStructSeverity.Info, "Point", "converted", ConversionLevel.DirectAdd,
            StructPattern.FullImmutable, "Point.cs", 12);
        Assert.Equal(ReadOnlyStructSeverity.Info, d.Severity);
        Assert.Equal("Point", d.StructName);
        Assert.Equal(ConversionLevel.DirectAdd, d.Level);
        Assert.Equal(StructPattern.FullImmutable, d.Pattern);
        Assert.Equal(12, d.Line);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new ReadOnlyStructMakerStatistics();
        Assert.Equal(0, s.StructsScanned);
        Assert.Equal(0, s.StructsConverted);
        Assert.Equal(0, s.Level5_MethodMigrate);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerTests" --no-restore -v q`
Expected: FAIL — namespace `CSharpToJava.Core.ReadOnlyStructMaker` not found

- [ ] **Step 3: Write implementation**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs
namespace CSharpToJava.Core.ReadOnlyStructMaker;

public enum ReadOnlyStructSeverity { Info, Warning, Error }

public enum StructPattern
{
    FullImmutable,               // A
    AlreadyReadonly,             // B
    PrivateSetter,               // C
    DataContainer,               // D
    MutableMethods,              // E
    MutableMethodsNonMigratable, // F
}

public enum ConversionLevel
{
    Skip,            // L0
    DirectAdd,       // L1
    PropertyConvert, // L2
    DataContainer,   // L3
    MethodMigrate,   // L5
    NotConvertible,  // L7
}

public enum MigrationType
{
    VoidToStruct,
    ThisToStruct,
    OtherReturnToOut,
}

public sealed record ReadOnlyStructMakerDiagnostic(
    ReadOnlyStructSeverity Severity,
    string StructName,
    string Reason,
    ConversionLevel? Level,
    StructPattern? Pattern,
    string? FilePath = null,
    int? Line = null);

public sealed class ReadOnlyStructMakerStatistics
{
    public int StructsScanned;
    public int StructsConverted;
    public int StructsSkipped;
    public int StructsFailed;
    public int Level0_Skipped;
    public int Level1_DirectAdd;
    public int Level2_PropertyConvert;
    public int Level3_DataContainer;
    public int Level5_MethodMigrate;
    public int MethodsMigrated;
    public int CallSitesUpdated;
    public int Failed_RefThisEscape;
    public int Failed_VirtualOrInterface;
    public int Failed_DelegateReferenced;
    public int Failed_ComplexMutableState;
    public int Failed_NameConflict;
    public int Failed_RefFieldMutation;
}

public sealed record ReadOnlyStructMakerResult(
    string? OutputCode,
    bool Changed,
    IReadOnlyDictionary<string, string> ChangedFiles,
    IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics,
    ReadOnlyStructMakerStatistics Statistics);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerTests" --no-restore -v q`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
git commit -m "feat(readonly-struct): add diagnostics, enums, and result types"
```

---

### Task 2: Options

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Append to ReadOnlyStructMakerTests.cs
[Fact]
public void Options_Defaults()
{
    var o = new ReadOnlyStructMakerOptions();
    Assert.Equal("DoNotMakeReadOnly", o.OptOutAttributeName);
    Assert.True(o.ReportSkipped);
    Assert.False(o.Strict);
    Assert.True(o.EnableMethodMigration);
    Assert.True(o.EnableDtoConversion);
    Assert.True(o.UpdateCallSites);
    Assert.Null(o.AllowedLevels);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.Tests --filter "Options_Defaults" --no-restore -v q`
Expected: FAIL — type not found

- [ ] **Step 3: Write implementation**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs
namespace CSharpToJava.Core.ReadOnlyStructMaker;

public sealed class ReadOnlyStructMakerOptions
{
    public string OptOutAttributeName { get; init; } = "DoNotMakeReadOnly";
    public bool ReportSkipped { get; init; } = true;
    public bool Strict { get; init; }
    public bool EnableMethodMigration { get; init; } = true;
    public bool EnableDtoConversion { get; init; } = true;
    public bool UpdateCallSites { get; init; } = true;
    public IReadOnlySet<ConversionLevel>? AllowedLevels { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.Tests --filter "Options_Defaults" --no-restore -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
git commit -m "feat(readonly-struct): add options type"
```

---

### Task 3: PatternRecognizer — Skip Detection (L0)

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/PatternRecognizer.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Append to ReadOnlyStructMakerTests.cs
[Fact]
public void AlreadyReadonlyStruct_IsSkipped()
{
    var src = "readonly struct S { public readonly int X; }";
    var result = RunMaker(src);
    Assert.False(result.Changed);
    Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
}

[Fact]
public void RefStruct_IsSkipped()
{
    var src = "ref struct S { public int X; }";
    var result = RunMaker(src);
    Assert.False(result.Changed);
}

[Fact]
public void PartialStruct_IsSkipped()
{
    var src = "partial struct S { public int X; }";
    var result = RunMaker(src);
    Assert.False(result.Changed);
}

[Fact]
public void DoNotMakeReadOnlyAttribute_IsSkipped()
{
    var src = """
    [System.AttributeUsage(System.AttributeTargets.Struct)]
    class DoNotMakeReadOnlyAttribute : System.Attribute { }
    [DoNotMakeReadOnly]
    struct S { public int X; }
    """;
    var result = RunMaker(src);
    Assert.False(result.Changed);
}

// Helper — will be defined once and reused
private static ReadOnlyStructMakerResult RunMaker(string source, ReadOnlyStructMakerOptions? options = null)
{
    var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
    var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("test",
        new[] { tree },
        new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
        new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
    return new Core.ReadOnlyStructMaker.ReadOnlyStructMaker().MakeReadOnly(tree, compilation.GetSemanticModel(tree), options);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerTests" --no-restore -v q`
Expected: FAIL — `ReadOnlyStructMaker` class not found

- [ ] **Step 3: Write PatternRecognizer**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/PatternRecognizer.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed record PatternAnalysisResult(
    StructPattern Pattern,
    ConversionLevel SuggestedLevel,
    string Reason,
    IReadOnlyList<IFieldSymbol> Fields,
    IReadOnlyList<IPropertySymbol> Properties,
    IReadOnlyList<IMethodSymbol> MutatingMethods);

internal sealed class PatternRecognizer
{
    private readonly ReadOnlyStructMakerOptions _options;

    public PatternRecognizer(ReadOnlyStructMakerOptions options) => _options = options;

    public PatternAnalysisResult Recognize(INamedTypeSymbol symbol, StructDeclarationSyntax syntax, SemanticModel model)
    {
        var fields = symbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst).ToList();
        var properties = symbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic).ToList();
        var methods = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic).ToList();

        // Detect mutating methods (assign to fields or properties of this)
        var mutatingMethods = new List<IMethodSymbol>();
        foreach (var method in methods)
        {
            var methodSyntax = method.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (methodSyntax != null && IsMutating(methodSyntax, model))
                mutatingMethods.Add(method);
        }

        // Also check property setters that modify backing fields
        var mutatingSetters = properties.Where(p => p.SetMethod != null && !p.SetMethod.IsInitOnly)
            .Where(p => IsPropertySetterMutating(p, model)).ToList();

        if (!mutatingMethods.Any() && !mutatingSetters.Any())
        {
            // No mutating methods — classify by field/property state
            if (fields.All(f => f.IsReadOnly))
                return new(StructPattern.AlreadyReadonly, ConversionLevel.DirectAdd,
                    "all fields already readonly", fields, properties, Array.Empty<IMethodSymbol>());

            if (fields.All(f => f.IsReadOnly || IsOnlyAssignedInConstructor(f, symbol)))
                return new(StructPattern.FullImmutable, ConversionLevel.DirectAdd,
                    "all fields only assigned in constructor", fields, properties, Array.Empty<IMethodSymbol>());

            if (properties.All(p => p.SetMethod == null || p.SetMethod.DeclaredAccessibility == Accessibility.Private))
                return new(StructPattern.PrivateSetter, ConversionLevel.PropertyConvert,
                    "all setters are private", fields, properties, Array.Empty<IMethodSymbol>());

            return new(StructPattern.DataContainer, ConversionLevel.DataContainer,
                "data container with public setters", fields, properties, Array.Empty<IMethodSymbol>());
        }

        // Has mutating methods — migratable or not determined later by StructAnalyzer
        return new(StructPattern.MutableMethods, ConversionLevel.MethodMigrate,
            "has mutating methods", fields, properties, mutatingMethods);
    }

    private static bool IsMutating(MethodDeclarationSyntax method, SemanticModel model)
    {
        // A method is mutating if it assigns to any instance field or property of the containing struct
        return method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
        {
            var symbol = model.GetSymbolInfo(assignment.Left).Symbol;
            return symbol is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false, SetMethod: not null };
        }) || method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>().Any(unary =>
        {
            if (unary.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
                return false;
            var symbol = model.GetSymbolInfo(unary.Operand).Symbol;
            return symbol is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false };
        }) || method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().Any(unary =>
        {
            var symbol = model.GetSymbolInfo(unary.Operand).Symbol;
            return symbol is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false };
        });
    }

    private static bool IsPropertySetterMutating(IPropertySymbol property, SemanticModel model)
    {
        // Auto-properties with public setter are "mutating" for readonly purposes
        return property.SetMethod != null &&
               property.SetMethod.DeclaredAccessibility != Accessibility.Private;
    }

    private static bool IsOnlyAssignedInConstructor(IFieldSymbol field, INamedTypeSymbol containingType)
    {
        // Heuristic: field is not readonly but only assigned in ctor
        // Full check requires syntax walk — done in StructAnalyzer
        return false; // Conservative: defer to StructAnalyzer
    }
}
```

- [ ] **Step 4: Write minimal ReadOnlyStructMaker entry point (stub for L0)**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

public sealed class ReadOnlyStructMaker
{
    public ReadOnlyStructMakerResult MakeReadOnly(
        SyntaxTree tree,
        SemanticModel semanticModel,
        ReadOnlyStructMakerOptions? options = null)
    {
        options ??= new ReadOnlyStructMakerOptions();
        var stats = new ReadOnlyStructMakerStatistics();
        var diagnostics = new List<ReadOnlyStructMakerDiagnostic>();
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var structs = root.DescendantNodes().OfType<StructDeclarationSyntax>().ToList();
        stats.StructsScanned = structs.Count;

        var analyzer = new StructAnalyzer(options, semanticModel);
        var analysisResults = new Dictionary<StructDeclarationSyntax, AnalyzeResult>();

        foreach (var structSyntax in structs)
        {
            var symbol = semanticModel.GetDeclaredSymbol(structSyntax);
            if (symbol == null) continue;

            var result = analyzer.Analyze(symbol, structSyntax);
            analysisResults[structSyntax] = result;

            if (result.Level == ConversionLevel.Skip)
            {
                stats.StructsSkipped++;
                stats.Level0_Skipped++;
                if (options.ReportSkipped)
                    diagnostics.Add(new(ReadOnlyStructSeverity.Info, result.QualifiedName ?? "?",
                        result.Reason, result.Level, result.Pattern));
            }
            else if (result.Level == ConversionLevel.NotConvertible)
            {
                stats.StructsFailed++;
                diagnostics.Add(new(ReadOnlyStructSeverity.Warning, result.QualifiedName ?? "?",
                    result.Reason, result.Level, result.Pattern));
            }
        }

        // No rewrites yet for L0-only — return unchanged
        return new ReadOnlyStructMakerResult(
            root.ToFullString(), Changed: false,
            new Dictionary<string, string>(), diagnostics, stats);
    }
}
```

- [ ] **Step 5: Write StructAnalyzer with L0 skip logic**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed record AnalyzeResult(
    ConversionLevel Level,
    StructPattern Pattern,
    bool ShouldRewrite,
    string Reason,
    string? QualifiedName,
    IReadOnlyList<MethodMigrationInfo>? MethodMigrations = null);

internal sealed record MethodMigrationInfo(
    IMethodSymbol Method,
    MigrationType Type,
    MethodDeclarationSyntax Syntax);

internal sealed class StructAnalyzer
{
    private readonly ReadOnlyStructMakerOptions _options;
    private readonly SemanticModel _model;
    private readonly PatternRecognizer _recognizer;

    public StructAnalyzer(ReadOnlyStructMakerOptions options, SemanticModel model)
    {
        _options = options;
        _model = model;
        _recognizer = new PatternRecognizer(options);
    }

    public AnalyzeResult Analyze(INamedTypeSymbol symbol, StructDeclarationSyntax syntax)
    {
        var name = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // L0 checks
        if (syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                "already readonly struct (skipped)", name);

        if (syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                "ref struct (skipped)", name);

        if (syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                "partial struct (skipped)", name);

        if (HasOptOutAttribute(symbol))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                $"[{_options.OptOutAttributeName}] attribute (skipped)", name);

        // Pattern recognition
        var pattern = _recognizer.Recognize(symbol, syntax, _model);

        return new(pattern.SuggestedLevel, pattern.Pattern, true, pattern.Reason, name);
    }

    private bool HasOptOutAttribute(INamedTypeSymbol symbol)
    {
        return symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == _options.OptOutAttributeName ||
            a.AttributeClass?.Name == _options.OptOutAttributeName + "Attribute");
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerTests" --no-restore -v q`
Expected: PASS (6 tests: 2 from Task 1 + 4 L0 tests)

- [ ] **Step 7: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
git commit -m "feat(readonly-struct): PatternRecognizer + StructAnalyzer with L0 skip logic"
```

---

### Task 4: L1 — Direct Add readonly

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs`
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void FullImmutable_AddsReadonly()
{
    var src = """
    struct S {
        private int _x;
        public S(int x) { _x = x; }
        public int X => _x;
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("readonly struct S", result.OutputCode);
}

[Fact]
public void AlreadyReadonlyFields_AddsReadonly()
{
    var src = "struct S { public readonly int X; public readonly int Y; }";
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("readonly struct S", result.OutputCode);
}

[Fact]
public void EmptyStruct_AddsReadonly()
{
    var src = "struct Empty { }";
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("readonly struct Empty", result.OutputCode);
}

[Fact]
public void PreservesXmlAttributeAndComments()
{
    var src = """
    /// <summary>A point.</summary>
    [System.Serializable]
    struct Point { public readonly double X; public readonly double Y; }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("/// <summary>A point.</summary>", result.OutputCode);
    Assert.Contains("[System.Serializable]", result.OutputCode);
    Assert.Contains("readonly struct Point", result.OutputCode);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests --filter "FullImmutable_AddsReadonly" --no-restore -v q`
Expected: FAIL — result.Changed is false (no rewriter yet)

- [ ] **Step 3: Write ReadOnlyStructRewriter with L1**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class ReadOnlyStructRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<StructDeclarationSyntax, AnalyzeResult> _results;

    public ReadOnlyStructRewriter(Dictionary<StructDeclarationSyntax, AnalyzeResult> results)
        => _results = results;

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        if (!_results.TryGetValue(node, out var result) || !result.ShouldRewrite)
            return base.VisitStructDeclaration(node);

        var rewritten = result.Level switch
        {
            ConversionLevel.DirectAdd => ApplyDirectAdd(node),
            ConversionLevel.PropertyConvert => ApplyPropertyConvert(node),
            _ => node
        };

        return base.VisitStructDeclaration(rewritten);
    }

    internal static StructDeclarationSyntax ApplyDirectAdd(StructDeclarationSyntax node)
    {
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            return node;

        var readonlyToken = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        if (node.Modifiers.Count > 0)
            return node.AddModifiers(readonlyToken);

        // No modifiers: insert before 'struct' keyword, preserving leading trivia
        var newKeyword = node.Keyword.WithLeadingTrivia(SyntaxFactory.TriviaList());
        readonlyToken = readonlyToken.WithLeadingTrivia(node.Keyword.LeadingTrivia);
        return node.WithKeyword(newKeyword).AddModifiers(readonlyToken);
    }

    private static StructDeclarationSyntax ApplyPropertyConvert(StructDeclarationSyntax node)
    {
        // L2: remove 'private set' from accessors
        var rewriter = new PrivateSetRemover();
        var result = (StructDeclarationSyntax)rewriter.Visit(node);
        return ApplyDirectAdd(result);
    }

    private sealed class PrivateSetRemover : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAccessorList(AccessorListSyntax node)
        {
            var accessors = node.Accessors.Where(a =>
                !a.IsKind(SyntaxKind.SetAccessorDeclaration) ||
                !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)));
            return node.WithAccessors(SyntaxFactory.List(accessors));
        }
    }
}
```

- [ ] **Step 4: Update ReadOnlyStructMaker to invoke rewriter**

Replace the "No rewrites yet" section in `ReadOnlyStructMaker.cs` with:

```csharp
        // Rewrite
        var rewriter = new ReadOnlyStructRewriter(analysisResults);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var changed = newRoot.ToFullString() != root.ToFullString();

        if (changed)
        {
            foreach (var (syntax, result) in analysisResults.Where(kv => kv.Value.ShouldRewrite))
            {
                stats.StructsConverted++;
                switch (result.Level)
                {
                    case ConversionLevel.DirectAdd: stats.Level1_DirectAdd++; break;
                    case ConversionLevel.PropertyConvert: stats.Level2_PropertyConvert++; break;
                    case ConversionLevel.DataContainer: stats.Level3_DataContainer++; break;
                }
                diagnostics.Add(new(ReadOnlyStructSeverity.Info, result.QualifiedName ?? "?",
                    result.Reason, result.Level, result.Pattern));
            }
        }

        return new ReadOnlyStructMakerResult(
            newRoot.ToFullString(), changed,
            new Dictionary<string, string>(), diagnostics, stats);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerTests" --no-restore -v q`
Expected: PASS (all tests)

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
git commit -m "feat(readonly-struct): L1 direct-add + L2 private-set removal"
```

---

### Task 5: L2 — Private Setter Conversion Tests

**Files:**
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void PrivateSetter_RemovesSet_AddsReadonly()
{
    var src = """
    struct S {
        internal int X { get; private set; }
        internal int Y { get; private set; }
        internal S(int x, int y) { X = x; Y = y; }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("readonly struct S", result.OutputCode);
    Assert.DoesNotContain("private set", result.OutputCode);
    Assert.Contains("{ get; }", result.OutputCode);
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test tests/CSharpToJava.Tests --filter "PrivateSetter_RemovesSet" --no-restore -v q`
Expected: PASS (already implemented in Task 4)

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
git commit -m "test(readonly-struct): L2 private setter conversion"
```

---

### Task 6: L3 — Data Container Conversion

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/ConstructorGenerator.cs`
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs`
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void DataContainer_GetSetProperties_ConvertsToGetOnly()
{
    var src = """
    struct EdgeConstraints {
        public int Direction { get; set; }
        public double Separation { get; set; }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("readonly struct EdgeConstraints", result.OutputCode);
    Assert.DoesNotContain("set;", result.OutputCode);
    Assert.Contains("public EdgeConstraints(", result.OutputCode);
}

[Fact]
public void DataContainer_InternalFields_ConvertsToProperties()
{
    var src = """
    struct Pixel {
        internal int X;
        internal int Y;
        internal Pixel(int x, int y) { X = x; Y = y; }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("readonly struct Pixel", result.OutputCode);
    Assert.Contains("internal int X { get; }", result.OutputCode);
}

[Fact]
public void DataContainer_ExistingCtor_NotDuplicated()
{
    var src = """
    struct S {
        internal int A;
        internal int B;
        internal S(int a, int b) { A = a; B = b; }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    // Should NOT generate a second constructor with same signature
    var ctorCount = result.OutputCode!.Split("internal S(").Length - 1;
    Assert.Equal(1, ctorCount);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests --filter "DataContainer" --no-restore -v q`
Expected: FAIL

- [ ] **Step 3: Write ConstructorGenerator**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/ConstructorGenerator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class ConstructorGenerator
{
    private static readonly HashSet<string> ObjectMemberNames = new()
        { "GetType", "Equals", "GetHashCode", "ToString" };

    public static bool HasNameConflict(IReadOnlyList<IFieldSymbol> fields)
        => fields.Any(f => ObjectMemberNames.Contains(f.Name));

    public ConstructorDeclarationSyntax? Generate(
        string structName,
        IReadOnlyList<(string Name, TypeSyntax Type, Accessibility Access)> members)
    {
        if (members.Count == 0) return null;

        var parameters = members.Select(m =>
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(ToCamelCase(m.Name)))
                .WithType(m.Type)).ToList();

        var assignments = members.Select(m =>
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(m.Name),
                    SyntaxFactory.IdentifierName(ToCamelCase(m.Name))))).ToList();

        var accessMod = members.All(m => m.Access == Accessibility.Internal)
            ? SyntaxKind.InternalKeyword
            : SyntaxKind.PublicKeyword;

        return SyntaxFactory.ConstructorDeclaration(structName)
            .AddModifiers(SyntaxFactory.Token(accessMod))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(SyntaxFactory.Block(assignments));
    }

    private static string ToCamelCase(string name)
        => name.Length > 0 && char.IsUpper(name[0])
            ? char.ToLowerInvariant(name[0]) + name[1..]
            : name;
}
```

- [ ] **Step 4: Add L3 rewrite to ReadOnlyStructRewriter**

Add to `VisitStructDeclaration` switch:

```csharp
ConversionLevel.DataContainer => ApplyDataContainer(node),
```

And implement:

```csharp
private static StructDeclarationSyntax ApplyDataContainer(StructDeclarationSyntax node)
{
    var structName = node.Identifier.Text;
    var membersForCtor = new List<(string, TypeSyntax, Accessibility)>();

    // 1. Convert get/set auto-properties to get-only
    var newMembers = new SyntaxList<MemberDeclarationSyntax>();
    foreach (var member in node.Members)
    {
        if (member is PropertyDeclarationSyntax prop &&
            prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
        {
            var getter = prop.AccessorList.Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            var newProp = prop.WithAccessorList(
                SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
            newMembers = newMembers.Add(newProp);
            membersForCtor.Add((prop.Identifier.Text, prop.Type,
                prop.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                    ? Accessibility.Public : Accessibility.Internal));
        }
        else if (member is FieldDeclarationSyntax field &&
                 !field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)) &&
                 !field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        {
            // Convert internal/private fields to get-only properties
            foreach (var variable in field.Declaration.Variables)
            {
                var prop = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                    .WithModifiers(field.Modifiers)
                    .WithAccessorList(SyntaxFactory.AccessorList(
                        SyntaxFactory.SingletonList(
                            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
                newMembers = newMembers.Add(prop);
                membersForCtor.Add((variable.Identifier.Text, field.Declaration.Type,
                    Accessibility.Internal));
            }
        }
        else
        {
            newMembers = newMembers.Add(member);
        }
    }

    var rewritten = node.WithMembers(newMembers);

    // 2. Generate constructor if no equivalent exists
    var ctorGen = new ConstructorGenerator();
    var ctor = ctorGen.Generate(structName, membersForCtor);
    if (ctor != null && !HasEquivalentCtor(rewritten, ctor))
    {
        rewritten = rewritten.AddMembers(ctor);
    }

    // 3. Add readonly
    return ApplyDirectAdd(rewritten);
}

private static bool HasEquivalentCtor(StructDeclarationSyntax node, ConstructorDeclarationSyntax newCtor)
{
    var newParams = newCtor.ParameterList.Parameters;
    return node.Members.OfType<ConstructorDeclarationSyntax>().Any(existing =>
    {
        var ep = existing.ParameterList.Parameters;
        if (ep.Count != newParams.Count) return false;
        for (int i = 0; i < ep.Count; i++)
            if (ep[i].Type?.ToString() != newParams[i].Type?.ToString()) return false;
        return true;
    });
}
```

- [ ] **Step 5: Update StructAnalyzer to detect field-only-assigned-in-ctor (Pattern A)**

In `StructAnalyzer.Analyze`, after pattern recognition, add full-immutable detection:

```csharp
// After L0 checks, before pattern recognition:
var allFieldsReadonlyOrCtorOnly = CheckAllFieldsImmutable(symbol, syntax);
if (allFieldsReadonlyOrCtorOnly && !HasMutatingInstanceMethods(symbol, syntax))
    return new(ConversionLevel.DirectAdd, StructPattern.FullImmutable, true,
        "all fields only assigned in constructor", name);
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerTests" --no-restore -v q`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
git commit -m "feat(readonly-struct): L3 data container conversion with ctor generation"
```

---

### Task 7: L7 — Not Convertible Detection

**Files:**
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void HeavyMutable_ArrayField_NotConvertible()
{
    var src = """
    struct Cache {
        private int[] _items;
        private int _count;
        public void Clear() { _count = 0; }
        public void Insert(int item) { _items[_count++] = item; }
    }
    """;
    var result = RunMaker(src);
    Assert.False(result.Changed);
    Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
}

[Fact]
public void PublicFields_DirectlyAssigned_NotConvertible()
{
    var src = """
    struct Point {
        public double X;
        public double Y;
    }
    class User {
        void M() { var p = new Point(); p.X = 5; }
    }
    """;
    var result = RunMaker(src);
    Assert.False(result.Changed);
    Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
}
```

- [ ] **Step 2: Run tests to verify they fail**

- [ ] **Step 3: Implement L7 detection in StructAnalyzer**

Add checks for:
- Array/collection fields with mutating methods → NotConvertible
- Public fields assigned outside the struct → NotConvertible
- Method count > threshold with complex state → NotConvertible

- [ ] **Step 4: Run tests to verify they pass**

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
git commit -m "feat(readonly-struct): L7 not-convertible detection (heavy mutable, public fields)"
```

---

### Task 8: CallGraphBuilder

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void RefThisEscape_SkipsL5()
{
    var src = """
    struct S {
        public int V;
        public void Mutate() { V++; }
    }
    class C {
        void M(ref S s) { s.Mutate(); }
    }
    """;
    var result = RunMaker(src);
    // Should be NotConvertible because struct is passed by ref and mutated externally
    Assert.Contains(result.Diagnostics, d =>
        d.Level == ConversionLevel.NotConvertible || d.Level == ConversionLevel.Skip);
}
```

- [ ] **Step 2: Implement CallGraphBuilder**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class CallGraphBuilder
{
    private readonly CSharpCompilation _compilation;

    public CallGraphBuilder(CSharpCompilation compilation) => _compilation = compilation;

    /// <summary>Check if any field of the struct is modified through a ref/out parameter.</summary>
    public bool IsModifiedThroughRef(INamedTypeSymbol structSymbol)
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // Find parameters of this struct type with ref/out
            foreach (var param in root.DescendantNodes().OfType<ParameterSyntax>())
            {
                if (param.Modifiers.All(m => !m.IsKind(SyntaxKind.RefKeyword) && !m.IsKind(SyntaxKind.OutKeyword)))
                    continue;

                var paramSymbol = model.GetDeclaredSymbol(param);
                if (paramSymbol?.Type is not INamedTypeSymbol paramType) continue;
                if (!SymbolEqualityComparer.Default.Equals(paramType.OriginalDefinition, structSymbol)) continue;

                // Check if any member access on this parameter assigns to a field
                var paramName = param.Identifier.Text;
                var containingMethod = param.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                if (containingMethod == null) continue;

                foreach (var assignment in containingMethod.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                        memberAccess.Expression is IdentifierNameSyntax id &&
                        id.Identifier.Text == paramName)
                    {
                        return true;
                    }
                }

                foreach (var unary in containingMethod.DescendantNodes()
                    .OfType<PostfixUnaryExpressionSyntax>()
                    .Concat(containingMethod.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>().Cast<PostfixUnaryExpressionSyntax>()))
                {
                    // Check ++ / -- on ref param fields
                }
            }
        }
        return false;
    }

    /// <summary>Check if public fields are assigned outside the struct definition.</summary>
    public bool HasExternalPublicFieldAssignment(INamedTypeSymbol structSymbol)
    {
        var publicFields = structSymbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.DeclaredAccessibility == Accessibility.Public && !f.IsStatic && !f.IsConst)
            .ToHashSet<IFieldSymbol>(SymbolEqualityComparer.Default);

        if (!publicFields.Any()) return false;

        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var symbol = model.GetSymbolInfo(assignment.Left).Symbol;
                if (symbol is IFieldSymbol field && publicFields.Contains(field))
                {
                    // Check assignment is outside the struct
                    var containingStruct = assignment.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
                    if (containingStruct == null) return true;
                    var containingSymbol = model.GetDeclaredSymbol(containingStruct);
                    if (!SymbolEqualityComparer.Default.Equals(containingSymbol, structSymbol)) return true;
                }
            }
        }
        return false;
    }
}
```

- [ ] **Step 3: Integrate CallGraphBuilder into StructAnalyzer**

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerTests" --no-restore -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs
git commit -m "feat(readonly-struct): CallGraphBuilder with ref-mutation and public-field detection"
```

---

### Task 9: MethodMigrator — Void Methods (L5)

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerL5Tests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CSharpToJava.Tests/ReadOnlyStructMakerL5Tests.cs
using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public class ReadOnlyStructMakerL5Tests
{
    private static ReadOnlyStructMakerResult RunMaker(string source, ReadOnlyStructMakerOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Core.ReadOnlyStructMaker.ReadOnlyStructMaker()
            .MakeReadOnly(tree, compilation.GetSemanticModel(tree), options);
    }

    [Fact]
    public void VoidMutatingMethod_MigratedToReturnStruct()
    {
        var src = """
        struct Counter {
            public int Value;
            public void Increment() { Value++; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Counter", result.OutputCode);
        Assert.Contains("public Counter Increment()", result.OutputCode);
        Assert.Contains("var result = this;", result.OutputCode);
        Assert.Contains("return result;", result.OutputCode);
    }

    [Fact]
    public void ReturnThisMethod_MigratedToReturnCopy()
    {
        var src = """
        struct Acc {
            public int V;
            public Acc Add(int n) { V += n; return this; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("var result = this;", result.OutputCode);
        Assert.Contains("return result;", result.OutputCode);
        Assert.DoesNotContain("return this;", result.OutputCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

- [ ] **Step 3: Implement MethodMigrator**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class MethodMigrator
{
    private readonly string _structName;

    public MethodMigrator(string structName) => _structName = structName;

    public MethodDeclarationSyntax Migrate(MethodDeclarationSyntax method)
    {
        var body = method.Body;
        if (body == null) return method; // expression-bodied not handled yet

        // 1. Insert `var result = this;` at top
        var resultDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator("result")
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.ThisExpression())))));

        // 2. Replace field accesses with result.field — handled by rewriting body
        var newBody = new ThisToResultRewriter().Visit(body)!;

        // 3. Replace `return this;` with `return result;`
        newBody = (BlockSyntax)new ReturnThisRewriter().Visit(newBody)!;

        // 4. Prepend result declaration
        newBody = newBody.WithStatements(
            newBody.Statements.Insert(0, resultDecl.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed)));

        // 5. Ensure return type is struct name (for void methods)
        var returnType = method.ReturnType.IsKind(SyntaxKind.PredefinedType) &&
                         ((PredefinedTypeSyntax)method.ReturnType).Keyword.IsKind(SyntaxKind.VoidKeyword)
            ? SyntaxFactory.IdentifierName(_structName)
            : method.ReturnType;

        // 6. Ensure there's a `return result;` at end for void methods
        if (method.ReturnType.IsKind(SyntaxKind.PredefinedType) &&
            ((PredefinedTypeSyntax)method.ReturnType).Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            var returnStatement = SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result"));
            newBody = newBody.AddStatements(returnStatement);
        }

        return method.WithReturnType(returnType).WithBody(newBody);
    }

    private sealed class ThisToResultRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
            => SyntaxFactory.IdentifierName("result").WithTriviaFrom(node);
    }

    private sealed class ReturnThisRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression is ThisExpressionSyntax)
                return node.WithExpression(SyntaxFactory.IdentifierName("result"));
            return base.VisitReturnStatement(node);
        }
    }
}
```

- [ ] **Step 4: Integrate MethodMigrator into the pipeline**

Update `ReadOnlyStructRewriter.VisitStructDeclaration` to handle `ConversionLevel.MethodMigrate`:

```csharp
ConversionLevel.MethodMigrate => ApplyMethodMigrate(node, result),
```

```csharp
private StructDeclarationSyntax ApplyMethodMigrate(StructDeclarationSyntax node, AnalyzeResult result)
{
    var rewritten = node;
    if (result.MethodMigrations != null)
    {
        var migrator = new MethodMigrator(node.Identifier.Text);
        foreach (var migration in result.MethodMigrations)
        {
            var migrated = migrator.Migrate(migration.Syntax);
            rewritten = rewritten.ReplaceNode(migration.Syntax, migrated);
        }
    }
    return ApplyDirectAdd(rewritten);
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerL5" --no-restore -v q`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ tests/CSharpToJava.Tests/ReadOnlyStructMakerL5Tests.cs
git commit -m "feat(readonly-struct): L5 MethodMigrator for void and return-this methods"
```

---

### Task 10: CallSiteUpdater — Void Call Sites

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/CallSiteUpdater.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerL5Tests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void VoidCallSite_UpdatedToAssignment()
{
    var src = """
    struct Counter {
        public int Value;
        public void Increment() { Value++; }
    }
    class User {
        void M() {
            var c = new Counter();
            c.Increment();
        }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("c = c.Increment();", result.OutputCode);
}

[Fact]
public void ArrayElementCallSite_UpdatedToAssignment()
{
    var src = """
    struct Size {
        public double W;
        public void Pad(double p) { W += p; }
    }
    class User {
        void M(Size[] sizes) {
            sizes[0].Pad(5);
        }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("sizes[0] = sizes[0].Pad(5);", result.OutputCode);
}
```

- [ ] **Step 2: Implement CallSiteUpdater**

```csharp
// src/CSharpToJava.Core/ReadOnlyStructMaker/CallSiteUpdater.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class CallSiteUpdater : CSharpSyntaxRewriter
{
    private readonly HashSet<string> _migratedVoidMethods;
    private readonly SemanticModel _model;

    public CallSiteUpdater(HashSet<string> migratedVoidMethods, SemanticModel model)
    {
        _migratedVoidMethods = migratedVoidMethods;
        _model = model;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;
            if (_migratedVoidMethods.Contains(methodName))
            {
                var receiver = memberAccess.Expression;
                // s.Method(args) → s = s.Method(args)
                var assignment = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    receiver,
                    invocation);
                return node.WithExpression(assignment);
            }
        }
        return base.VisitExpressionStatement(node);
    }
}
```

- [ ] **Step 3: Integrate CallSiteUpdater into ReadOnlyStructMaker**

After struct rewriting, run CallSiteUpdater on the entire compilation unit for L5 structs.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMakerL5" --no-restore -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ tests/CSharpToJava.Tests/ReadOnlyStructMakerL5Tests.cs
git commit -m "feat(readonly-struct): CallSiteUpdater for void method call sites"
```

---

### Task 11: L5 — OtherReturnToOut Migration

**Files:**
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerL5Tests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void OtherReturnMethod_GetsOutParameter()
{
    var src = """
    struct Rect {
        public double Left;
        public bool AddWithCheck(double x) {
            bool wider = x < Left;
            if (wider) Left = x;
            return wider;
        }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("out Rect", result.OutputCode);
}
```

- [ ] **Step 2: Implement OtherReturnToOut in MethodMigrator**

- [ ] **Step 3: Run tests**

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(readonly-struct): L5 OtherReturnToOut migration with out parameter"
```

---

### Task 12: Property Setter Migration (WithXxx)

**Files:**
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs`
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerL5Tests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void PropertySetter_MigratedToWithMethod()
{
    var src = """
    struct Box {
        private double _w;
        public double W { get => _w; set { _w = value; } }
        public void Pad(double p) { _w += p; }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);
    Assert.Contains("WithW(", result.OutputCode);
}
```

- [ ] **Step 2: Implement property setter → WithXxx migration**

- [ ] **Step 3: Run tests**

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(readonly-struct): property setter migration to WithXxx methods"
```

---

### Task 13: CLI Verb `make-readonly`

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`
- Create: `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs`

- [ ] **Step 1: Add MakeReadOnlyOptions verb class to Program.cs**

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
    [Option("no-method-migration", Default = false)]
    public bool NoMethodMigration { get; set; }
    [Option("no-dto-conversion", Default = false)]
    public bool NoDtoConversion { get; set; }
    [Option("min-level", Default = "L1", HelpText = "Minimum conversion level (L1-L5)")]
    public string MinLevel { get; set; } = "L1";
}
```

- [ ] **Step 2: Register verb in ParseArguments**

```csharp
return await Parser.Default.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions>(args)
    .MapResult(
        (ConvertOptions opts) => ConvertFile(opts),
        (ConvertProjectOptions opts) => ConvertProject(opts),
        (AnalyzeOptions opts) => AnalyzeProject(opts),
        (EliminateGotoOptions opts) => EliminateGoto(opts),
        (MakeReadOnlyOptions opts) => MakeReadonly(opts),
        errs => Task.FromResult(1)
    );
```

- [ ] **Step 3: Implement MakeReadonly handler**

- [ ] **Step 4: Add --no-make-readonly to ConvertProjectOptions**

```csharp
[Option("no-make-readonly", Default = false, HelpText = "Disable readonly-struct preprocessing")]
public bool NoMakeReadOnly { get; set; }
public bool MakeReadOnly => !NoMakeReadOnly;
```

- [ ] **Step 5: Create ProjectReadOnlyStructPreprocessor**

Mirror `ProjectGotoPreprocessor` pattern with `.cs2j-readonly-src/` intermediate directory.

- [ ] **Step 6: Build and verify CLI**

Run: `dotnet build src/CSharpToJava.CLI -v q`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add src/CSharpToJava.CLI/
git commit -m "feat(readonly-struct): CLI verb make-readonly + convert-project integration"
```

---

### Task 14: Integration with convert-project Pipeline

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs` (ConvertProject method)

- [ ] **Step 1: Insert readonly preprocessing before goto elimination in ConvertProject**

In the `ConvertProject` method, before the `if (opts.EliminateGoto)` block:

```csharp
if (opts.MakeReadOnly)
{
    var readonlyResult = await ProjectReadOnlyStructPreprocessor.PreprocessAsync(
        new ProjectReadOnlyStructPreprocessRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = Path.GetTempPath(),
            Verbose = opts.Verbose,
            Options = new ReadOnlyStructMakerOptions
            {
                EnableMethodMigration = !opts.ReadOnlyNoMethodMigration,
            }
        });
    if (readonlyResult.Success)
    {
        opts.Source = readonlyResult.PreprocessedSourcePath;
        Console.WriteLine($"make-readonly: {readonlyResult.Statistics.StructsConverted} converted, " +
            $"{readonlyResult.Statistics.StructsSkipped} skipped, " +
            $"{readonlyResult.Statistics.StructsFailed} failed");
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/CSharpToJava.CLI -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.CLI/Program.cs
git commit -m "feat(readonly-struct): integrate make-readonly into convert-project pipeline"
```

---

### Task 15: End-to-End Verification

**Files:**
- Test: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

- [ ] **Step 1: Write end-to-end compilation test**

```csharp
[Fact]
public void ConvertedCode_CompilesWithoutErrors()
{
    var src = """
    struct Point { public readonly int X; public readonly int Y; }
    struct Size {
        internal double W;
        internal double H;
        internal Size(double w, double h) { W = w; H = h; }
    }
    struct Config {
        public string Name { get; set; }
        public int Level { get; set; }
    }
    """;
    var result = RunMaker(src);
    Assert.True(result.Changed);

    var outputTree = CSharpSyntaxTree.ParseText(result.OutputCode!);
    var compilation = CSharpCompilation.Create("verify",
        new[] { outputTree },
        new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Assert.Empty(errors);
}

[Fact]
public void Idempotent_SecondRun_NoChange()
{
    var src = "struct S { public readonly int X; }";
    var first = RunMaker(src);
    Assert.True(first.Changed);
    var second = RunMaker(first.OutputCode!);
    Assert.False(second.Changed);
}
```

- [ ] **Step 2: Run all tests**

Run: `dotnet test tests/CSharpToJava.Tests --filter "ReadOnlyStructMaker" --no-restore -v q`
Expected: ALL PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/
git commit -m "test(readonly-struct): end-to-end compilation and idempotency tests"
```

---

## Summary

| Task | Phase | Key Deliverable |
|------|-------|----------------|
| 1-2 | Foundation | Types, enums, options |
| 3 | L0 | PatternRecognizer + skip logic |
| 4-5 | L1/L2 | readonly addition + private-set removal |
| 6 | L3 | Data container conversion + ctor generation |
| 7 | L7 | Not-convertible detection |
| 8 | Infra | CallGraphBuilder (ref-mutation, public-field) |
| 9-10 | L5 | MethodMigrator + CallSiteUpdater |
| 11-12 | L5+ | Out-parameter + WithXxx migration |
| 13-14 | CLI | make-readonly verb + pipeline integration |
| 15 | Verify | End-to-end compilation + idempotency |
