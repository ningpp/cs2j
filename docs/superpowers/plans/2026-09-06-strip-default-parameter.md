# Strip Default Parameter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a C#→C# preprocessing pipeline that strips default parameter values from method/constructor declarations and generates equivalent C# overload methods, producing compilable C# that the main translator can handle without default-value logic.

**Architecture:** A new `DefaultParameterStripper` module in `CSharpToJava.Core` follows the established `UnsignedTypeRewriter` / `GenericPreprocessor` pattern: a public entry class with `Collect` (Pass 1: global conflict catalog) and `Strip` (Pass 2: per-file rewrite) methods, backed by a `CSharpSyntaxRewriter` that strips `= value` from parameters and injects forwarding overload methods. A `ProjectDefaultParameterStripper` in `CSharpToJava.CLI` orchestrates the multi-file pipeline (identical structure to `ProjectGenericPreprocessor`). A `DefaultParameterVerifier` (Pass 3) re-parses output trees and asserts no residual defaults remain in managed contexts. The `strip-default` CLI verb and `convert-project` integration (default-on, before unsigned) complete the feature.

**Tech Stack:** C# / .NET 10, Microsoft.CodeAnalysis.CSharp 4.12.0 (Roslyn), xUnit, CommandLineParser

---

## File Structure

### New files — Core module (`src/CSharpToJava.Core/DefaultParameterStripper/`)

| File | Responsibility |
|------|----------------|
| `DefaultParameterStripperOptions.cs` | `sealed record` with `Verbose`, `Strict`, `GenerateOverloads`. |
| `DefaultParameterStripperResult.cs` | `sealed record` with `OutputCode`, `Changed`, `Diagnostics`, `Statistics`. |
| `DefaultParameterStripperStatistics.cs` | `sealed class` with counters matching spec §3. |
| `DefaultParameterStripDiagnostics.cs` | `enum DefaultParameterStripSeverity { Info, Warning, Error }` + `sealed record DefaultParameterStripDiagnostic(Severity, Message)`. |
| `DefaultParameterStripper.cs` | Public entry: `Collect(tree, model)` + `Strip(sourceCode, model, options)`. Orchestrates Pass 1 catalog, Pass 2 rewrite, formatting. |
| `DefaultParameterCatalog.cs` | Pass 1 global catalog: tracks existing member signatures and pending overload signatures for cross-file conflict detection. |
| `DefaultParameterDeclarationRewriter.cs` | `CSharpSyntaxRewriter` that strips defaults from `ParameterSyntax` and generates overload methods. |
| `DefaultParameterOverloadBuilder.cs` | Static helper that constructs overload declarations from a stripped original, applying all rules from spec §4.3. |
| `DefaultParameterVerifier.cs` | Pass 3: re-parses output syntax tree, walks all `ParameterSyntax` nodes, asserts no residual `Default` in managed contexts. |

### New file — CLI project preprocessor (`src/CSharpToJava.CLI/`)

| File | Responsibility |
|------|----------------|
| `ProjectDefaultParameterStripper.cs` | `internal static` class with `PreprocessAsync`. Structurally identical to `ProjectGenericPreprocessor`. `IntermediateDirectoryName = ".cs2j-strip-default-src"`. |

### New files — Tests

| File | Responsibility |
|------|----------------|
| `tests/CSharpToJava.Tests/DefaultParameterStripperTests.cs` | Single-file unit tests for the rewriter. |
| `tests/CSharpToJava.ProjectTests/StripDefaultProjectTests.cs` | Project-level integration tests. |
| `tests/SampleStripDefaultSolution/` | Fixture solution (Lib + App + .sln). |

### Modified files

| File | Changes |
|------|---------|
| `src/CSharpToJava.CLI/Program.cs` | Add `StripDefaultOptions` verb, `StripDefault(opts)` handler, register in `ParseArguments`, add `--no-strip-default` to `ConvertProjectOptions`, insert strip-default step before unsigned in `ConvertProject`, add to `GetPreservedDestinationDirectories`, add to `GetProjectConversionOptionTokens`. |
| `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` | Add `Content` include for `SampleStripDefaultSolution`. |
| `.gitignore` | Add `.cs2j-strip-default-src/` entry. |

---

## Task 1: Options, Result, Statistics, Diagnostics Records

**Files:**
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripperOptions.cs`
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripperResult.cs`
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripperStatistics.cs`
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripDiagnostics.cs`

- [ ] **Step 1: Create the options record**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripperOptions.cs`:
```csharp
namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed record DefaultParameterStripperOptions(
    bool Verbose = false,
    bool Strict = false,
    bool GenerateOverloads = true);
```

- [ ] **Step 2: Create the statistics class**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripperStatistics.cs`:
```csharp
namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed class DefaultParameterStripperStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int DeclarationsStripped;
    public int ParametersStripped;
    public int OverloadsGenerated;
    public int SkippedNoBody;
    public int SkippedConflict;
    public int SkippedHidesAbstract;
    public int SkippedUnsupported;
}
```

- [ ] **Step 3: Create the diagnostics enum + record**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripDiagnostics.cs`:
```csharp
namespace CSharpToJava.Core.DefaultParameterStripper;

public enum DefaultParameterStripSeverity { Info, Warning, Error }

public sealed record DefaultParameterStripDiagnostic(
    DefaultParameterStripSeverity Severity,
    string Message);
```

- [ ] **Step 4: Create the result record**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripperResult.cs`:
```csharp
namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed record DefaultParameterStripperResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<DefaultParameterStripDiagnostic> Diagnostics,
    DefaultParameterStripperStatistics Statistics);
```

- [ ] **Step 5: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: Build succeeded with 0 errors

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/DefaultParameterStripper/
git commit -m "feat(strip-default): add options, result, statistics, diagnostics records"
```

---

## Task 2: DefaultParameterCatalog (Pass 1 conflict detection)

**Files:**
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterCatalog.cs`
- Test: `tests/CSharpToJava.Tests/DefaultParameterCatalogTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/CSharpToJava.Tests/DefaultParameterCatalogTests.cs`:
```csharp
using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public class DefaultParameterCatalogTests
{
    [Fact]
    public void Collect_SingleMethod_RegistersExistingSignature()
    {
        var src = "class C { public void M(int a, string b = \"x\") { } }";
        var (tree, model) = Parse(src);
        var catalog = new DefaultParameterCatalog();
        catalog.Collect(tree, model);

        var method = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().First();
        var symbol = model.GetDeclaredSymbol(method)!;
        Assert.True(catalog.HasExistingSignature(
            symbol.ContainingType!, "M", "global::C.M(int, string)"));
    }

    [Fact]
    public void HasPendingOverload_InitiallyFalse()
    {
        var catalog = new DefaultParameterCatalog();
        Assert.False(catalog.HasPendingOverload(null!, "M", "global::C.M()"));
    }

    [Fact]
    public void RegisterPendingOverload_ThenHasPendingReturnsTrue()
    {
        var catalog = new DefaultParameterCatalog();
        catalog.RegisterPendingOverload(null!, "M", "global::C.M()");
        Assert.True(catalog.HasPendingOverload(null!, "M", "global::C.M()"));
    }

    private static (SyntaxTree tree, SemanticModel model) Parse(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("cat-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree, compilation.GetSemanticModel(tree));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "DefaultParameterCatalogTests" -v`
Expected: FAIL — `DefaultParameterCatalog` not found

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterCatalog.cs`:
```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

internal sealed class DefaultParameterCatalog
{
    private static readonly SymbolDisplayFormat s_qualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat;

    private readonly HashSet<string> _existingSignatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingOverloads = new(StringComparer.Ordinal);

    public void Collect(SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            RegisterExistingSignature(model, method);
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            RegisterExistingSignature(model, ctor);
    }

    public bool HasExistingSignature(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _existingSignatures.Contains(BuildKey(containingSymbol, memberName, paramTypeString));

    public bool HasPendingOverload(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _pendingOverloads.Contains(BuildKey(containingSymbol, memberName, paramTypeString));

    public void RegisterPendingOverload(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _pendingOverloads.Add(BuildKey(containingSymbol, memberName, paramTypeString));

    public static string BuildParamTypeString(
        SemanticModel model,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        int cutAt)
    {
        var parts = new List<string>(cutAt);
        for (var i = 0; i < cutAt && i < parameters.Count; i++)
        {
            var paramInfo = model.GetSymbolInfo(parameters[i].Type!);
            if (paramInfo.Symbol is ITypeSymbol typeSymbol)
                parts.Add(typeSymbol.ToDisplayString(s_qualifiedFormat));
            else
                parts.Add(parameters[i].Type!.ToString());
        }
        return string.Join(", ", parts);
    }

    public static string BuildFullParamTypeString(
        SemanticModel model,
        SeparatedSyntaxList<ParameterSyntax> parameters)
        => BuildParamTypeString(model, parameters, parameters.Count);

    private void RegisterExistingSignature(SemanticModel model, MemberDeclarationSyntax member)
    {
        var symbol = model.GetDeclaredSymbol(member);
        if (symbol == null) return;
        var name = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            _ => null,
        };
        if (name == null) return;
        var parameters = member switch
        {
            MethodDeclarationSyntax m => m.ParameterList.Parameters,
            ConstructorDeclarationSyntax c => c.ParameterList.Parameters,
            _ => (SeparatedSyntaxList<ParameterSyntax>?)null,
        };
        if (parameters == null) return;
        var key = BuildKey(symbol.ContainingType ?? symbol, name,
            BuildFullParamTypeString(model, parameters.Value));
        _existingSignatures.Add(key);
    }

    private static string BuildKey(ISymbol containingSymbol, string memberName, string paramTypeString)
    {
        var containingKey = containingSymbol == null
            ? "<global>"
            : containingSymbol.ToDisplayString(s_qualifiedFormat);
        return $"{containingKey}::{memberName}({paramTypeString})";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "DefaultParameterCatalogTests" -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterCatalog.cs tests/CSharpToJava.Tests/DefaultParameterCatalogTests.cs
git commit -m "feat(strip-default): add DefaultParameterCatalog for cross-file conflict detection"
```

---

## Task 3: DefaultParameterOverloadBuilder

**Files:**
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterOverloadBuilder.cs`
- Test: `tests/CSharpToJava.Tests/DefaultParameterOverloadBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/CSharpToJava.Tests/DefaultParameterOverloadBuilderTests.cs`:
```csharp
using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class DefaultParameterOverloadBuilderTests
{
    private readonly ITestOutputHelper _output;
    public DefaultParameterOverloadBuilderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SingleDefault_GeneratesOneOverload()
    {
        var src = """class C { public void M(int a, string b = "x") { } }""";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Single(overloads);
        var code = overloads[0].ToFullString();
        _output.WriteLine(code);
        Assert.Contains("public void M(int a)", code);
        Assert.Contains("M(a, \"x\")", code);
    }

    [Fact]
    public void TwoDefaults_GeneratesTwoOverloads()
    {
        var src = """class C { public void M(int a, string b = "x", bool c = true) { } }""";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Equal(2, overloads.Count);
        Assert.Contains("M(a, \"x\", true)", overloads[0].ToFullString());
        Assert.Contains("M(a, b, true)", overloads[1].ToFullString());
    }

    [Fact]
    public void GenericMethod_WritesExplicitTypeArgs()
    {
        var src = """class C { public T M<T>(int a, string b = "x") where T : class { return default; } }""";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Single(overloads);
        var code = overloads[0].ToFullString();
        Assert.Contains("M<T>(a, \"x\")", code);
        Assert.Contains("where T : class", code);
    }

    [Fact]
    public void NonVoidMethod_UsesReturn()
    {
        var src = """class C { public int M(int a, string b = "x") { return 0; } }""";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Single(overloads);
        Assert.Contains("return M(a, \"x\")", overloads[0].ToFullString());
    }

    [Fact]
    public void Constructor_UsesThisInitializer()
    {
        var src = """class C { public C(int a, string b = "x") { } }""";
        var (tree, model) = Parse(src);
        var ctor = tree.GetRoot().DescendantNodes().OfType<ConstructorDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(ctor, model, new DefaultParameterStripperStatistics());
        Assert.Single(overloads);
        var code = overloads[0].ToFullString();
        Assert.Contains("public C(int a)", code);
        Assert.Contains(": this(a, \"x\")", code);
    }

    [Fact]
    public void NoDefaults_ReturnsEmpty()
    {
        var src = "class C { public void M(int a) { } }";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Empty(overloads);
    }

    private static (SyntaxTree tree, SemanticModel model) Parse(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("ob-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree, compilation.GetSemanticModel(tree));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "DefaultParameterOverloadBuilderTests" -v`
Expected: FAIL — `DefaultParameterOverloadBuilder` not found

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterOverloadBuilder.cs`:
```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// Constructs overload declarations from a stripped original.
/// For each cut point (a parameter with a default value, excluding params),
/// generates a method/constructor with fewer parameters that forwards to
/// the original using the verbatim default value expressions.
/// </summary>
internal static class DefaultParameterOverloadBuilder
{
    public static List<MethodDeclarationSyntax> BuildOverloads(
        MethodDeclarationSyntax method, SemanticModel model,
        DefaultParameterStripperStatistics stats)
        => BuildOverloadsCore(method, method.ParameterList.Parameters,
            method.Modifiers, method.ReturnType, method.Identifier,
            method.TypeParameterList, method.ConstraintClauses,
            stats, isMethod: true);

    public static List<ConstructorDeclarationSyntax> BuildOverloads(
        ConstructorDeclarationSyntax ctor, SemanticModel model,
        DefaultParameterStripperStatistics stats)
    {
        var result = new List<ConstructorDeclarationSyntax>();
        var indices = CollectDefaultIndices(ctor.ParameterList.Parameters);
        if (indices.Count == 0) return result;
        if (!ValidateOptionalTail(ctor.ParameterList.Parameters, indices[0]))
        {
            stats.SkippedUnsupported++;
            return result;
        }
        foreach (var cutAt in indices.Where(i => i >= indices[0]))
        {
            var overload = BuildCtorOverload(ctor, ctor.ParameterList.Parameters, cutAt);
            if (overload != null)
            {
                result.Add(overload);
                stats.OverloadsGenerated++;
            }
        }
        return result;
    }

    public static List<LocalFunctionStatementSyntax> BuildOverloads(
        LocalFunctionStatementSyntax localFunc, SemanticModel model,
        DefaultParameterStripperStatistics stats)
    {
        var result = new List<LocalFunctionStatementSyntax>();
        var indices = CollectDefaultIndices(localFunc.ParameterList.Parameters);
        if (indices.Count == 0) return result;
        if (!ValidateOptionalTail(localFunc.ParameterList.Parameters, indices[0]))
        {
            stats.SkippedUnsupported++;
            return result;
        }
        foreach (var cutAt in indices.Where(i => i >= indices[0]))
        {
            var overload = BuildLocalFuncOverload(localFunc, localFunc.ParameterList.Parameters, cutAt);
            if (overload != null)
            {
                result.Add(overload);
                stats.OverloadsGenerated++;
            }
        }
        return result;
    }

    // --- core helpers ---

    private static List<MethodDeclarationSyntax> BuildOverloadsCore(
        MethodDeclarationSyntax method,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        SyntaxTokenList modifiers,
        TypeSyntax returnType,
        SyntaxToken identifier,
        TypeParameterListSyntax? typeParamList,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        DefaultParameterStripperStatistics stats,
        bool isMethod)
    {
        var result = new List<MethodDeclarationSyntax>();
        var indices = CollectDefaultIndices(parameters);
        if (indices.Count == 0) return result;
        if (!ValidateOptionalTail(parameters, indices[0]))
        {
            stats.SkippedUnsupported++;
            return result;
        }

        var isVoid = returnType is PredefinedTypeSyntax pt
            && pt.Keyword.IsKind(SyntaxKind.VoidKeyword);
        var filteredModifiers = FilterModifiers(modifiers);

        foreach (var cutAt in indices.Where(i => i >= indices[0]))
        {
            var retainedParams = new List<ParameterSyntax>();
            for (var i = 0; i < cutAt; i++)
                retainedParams.Add(parameters[i].WithDefault(null));

            var args = BuildForwardingArgs(parameters, cutAt);

            // Build invocation with explicit type args for generic methods
            ExpressionSyntax target;
            if (typeParamList != null && typeParamList.Parameters.Count > 0)
            {
                var typeArgs = string.Join(", ", typeParamList.Parameters.Select(p => p.Identifier.Text));
                target = SyntaxFactory.IdentifierName(
                    SyntaxFactory.Identifier(identifier.Text + "<" + typeArgs + ">"));
            }
            else
            {
                target = SyntaxFactory.IdentifierName(identifier);
            }

            var invocation = SyntaxFactory.InvocationExpression(target,
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

            StatementSyntax bodyStmt = isVoid
                ? SyntaxFactory.ExpressionStatement(invocation)
                : SyntaxFactory.ReturnStatement(invocation);

            var overload = SyntaxFactory.MethodDeclaration(returnType, identifier)
                .WithModifiers(filteredModifiers)
                .WithParameterList(SyntaxFactory.ParameterList(
                    SyntaxFactory.SeparatedList(retainedParams)))
                .WithTypeParameterList(typeParamList)
                .WithConstraintClauses(constraintClauses)
                .WithBody(SyntaxFactory.Block(bodyStmt));

            result.Add(overload);
            stats.OverloadsGenerated++;
        }
        return result;
    }

    private static ConstructorDeclarationSyntax? BuildCtorOverload(
        ConstructorDeclarationSyntax original,
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt)
    {
        var retainedParams = new List<ParameterSyntax>();
        for (var i = 0; i < cutAt; i++)
            retainedParams.Add(parameters[i].WithDefault(null));

        var args = BuildForwardingArgs(parameters, cutAt);
        var initializer = SyntaxFactory.ConstructorInitializer(
            SyntaxKind.ThisConstructorInitializer,
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

        return SyntaxFactory.ConstructorDeclaration(original.Identifier)
            .WithModifiers(FilterModifiers(original.Modifiers))
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList(retainedParams)))
            .WithInitializer(initializer)
            .WithBody(SyntaxFactory.Block());
    }

    private static LocalFunctionStatementSyntax? BuildLocalFuncOverload(
        LocalFunctionStatementSyntax original,
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt)
    {
        var retainedParams = new List<ParameterSyntax>();
        for (var i = 0; i < cutAt; i++)
            retainedParams.Add(parameters[i].WithDefault(null));

        var args = BuildForwardingArgs(parameters, cutAt);
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(original.Identifier),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));

        var isVoid = original.ReturnType is PredefinedTypeSyntax pt
            && pt.Keyword.IsKind(SyntaxKind.VoidKeyword);
        StatementSyntax bodyStmt = isVoid
            ? SyntaxFactory.ExpressionStatement(invocation)
            : SyntaxFactory.ReturnStatement(invocation);

        return SyntaxFactory.LocalFunctionStatement(original.ReturnType, original.Identifier)
            .WithModifiers(original.Modifiers)
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList(retainedParams)))
            .WithTypeParameterList(original.TypeParameterList)
            .WithBody(SyntaxFactory.Block(bodyStmt));
    }

    private static List<int> CollectDefaultIndices(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        var indices = new List<int>();
        for (var i = 0; i < parameters.Count; i++)
            if (parameters[i].Default != null && !IsParams(parameters[i]))
                indices.Add(i);
        return indices;
    }

    private static bool ValidateOptionalTail(
        SeparatedSyntaxList<ParameterSyntax> parameters, int firstDefaultIdx)
    {
        for (var i = firstDefaultIdx; i < parameters.Count; i++)
            if (parameters[i].Default == null && !IsParams(parameters[i]))
                return false; // CS1737
        return true;
    }

    private static List<ArgumentSyntax> BuildForwardingArgs(
        SeparatedSyntaxList<ParameterSyntax> parameters, int cutAt)
    {
        var args = new List<ArgumentSyntax>();
        for (var i = 0; i < parameters.Count; i++)
        {
            args.Add(i < cutAt
                ? SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameters[i].Identifier))
                : SyntaxFactory.Argument(parameters[i].Default!.Value));
        }
        return args;
    }

    private static SyntaxTokenList FilterModifiers(SyntaxTokenList original)
    {
        var kept = new List<SyntaxToken>();
        foreach (var mod in original)
        {
            if (mod.IsKind(SyntaxKind.AbstractKeyword) || mod.IsKind(SyntaxKind.ExternKeyword)
                || mod.IsKind(SyntaxKind.PartialKeyword) || mod.IsKind(SyntaxKind.VirtualKeyword)
                || mod.IsKind(SyntaxKind.OverrideKeyword) || mod.IsKind(SyntaxKind.AsyncKeyword)
                || mod.IsKind(SyntaxKind.SealedKeyword))
                continue;
            kept.Add(mod);
        }
        return SyntaxFactory.TokenList(kept);
    }

    private static bool IsParams(ParameterSyntax param)
        => param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "DefaultParameterOverloadBuilderTests" -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterOverloadBuilder.cs tests/CSharpToJava.Tests/DefaultParameterOverloadBuilderTests.cs
git commit -m "feat(strip-default): add DefaultParameterOverloadBuilder for overload construction"
```

---

## Task 4: DefaultParameterDeclarationRewriter + DefaultParameterStripper entry

**Files:**
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs`
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripper.cs`
- Test: `tests/CSharpToJava.Tests/DefaultParameterStripperTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/CSharpToJava.Tests/DefaultParameterStripperTests.cs`:
```csharp
using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class DefaultParameterStripperTests
{
    private readonly ITestOutputHelper _output;
    public DefaultParameterStripperTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Options_Default_AllEnabled()
    {
        var o = new DefaultParameterStripperOptions();
        Assert.True(o.GenerateOverloads);
        Assert.False(o.Verbose);
        Assert.False(o.Strict);
    }

    [Fact]
    public void Statistics_StartsZero()
    {
        var s = new DefaultParameterStripperStatistics();
        Assert.Equal(0, s.FilesTransformed);
        Assert.Equal(0, s.OverloadsGenerated);
    }

    [Fact]
    public void Strip_NoDefaults_ReturnsUnchanged()
    {
        var result = Strip("class C { public void M(int a) { } }");
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void Strip_SingleDefault_StripsAndGeneratesOverload()
    {
        var result = Strip("""class C { public void M(int a, string b = "x") { } }""");
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.DeclarationsStripped);
        Assert.Equal(1, result.Statistics.ParametersStripped);
        Assert.Equal(1, result.Statistics.OverloadsGenerated);
        Assert.DoesNotContain("= \"x\"", result.OutputCode);
        Assert.Contains("public void M(int a)", result.OutputCode);
        Assert.Contains("M(a, \"x\")", result.OutputCode);
        _output.WriteLine(result.OutputCode);
    }

    [Fact]
    public void Strip_StaticMethod_GeneratesOverload()
    {
        var result = Strip("class C { public static int M(int a, int b = 10) { return a + b; } }");
        Assert.True(result.Changed);
        Assert.Contains("public static int M(int a)", result.OutputCode);
        Assert.Contains("return M(a, 10)", result.OutputCode);
    }

    [Fact]
    public void Strip_AbstractMethod_GeneratesConcreteOverload()
    {
        var result = Strip("""abstract class C { public abstract void M(int a, string b = "x"); }""");
        Assert.True(result.Changed);
        Assert.Contains("public void M(int a)", result.OutputCode);
        Assert.Contains("M(a, \"x\")", result.OutputCode);
    }

    [Fact]
    public void Strip_InterfaceMethod_GeneratesDimOverload()
    {
        var result = Strip("""interface I { void M(int a, string b = "x"); }""");
        Assert.True(result.Changed);
        Assert.Contains("void M(int a) => M(a, \"x\")", result.OutputCode);
    }

    [Fact]
    public void Strip_DelegateDeclaration_Unchanged()
    {
        var result = Strip("delegate void D(int a = 1);");
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.SkippedUnsupported);
    }

    [Fact]
    public void Strip_PartialMethod_Unchanged()
    {
        var result = Strip("partial class C { partial void M(int a = 1); }");
        Assert.False(result.Changed);
    }

    [Fact]
    public void Strip_OverrideMethod_StripsButNoOverload()
    {
        var src = """
        class B { public virtual void M(int a, string b = "x") { } }
        class D : B { public override void M(int a, string b = "y") { } }
        """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("= \"x\"", result.OutputCode);
        Assert.DoesNotContain("= \"y\"", result.OutputCode);
        // Base class should generate overload, override should not
        Assert.Contains("public void M(int a)", result.OutputCode);
    }

    [Fact]
    public void Strip_DefaultExpressionVerbatimCopy()
    {
        var src = """
        enum E { A, B }
        class C { public void M(int a, E b = E.A, string c = null, int d = -1) { } }
        """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.Contains("M(a, E.A, null, -1)", result.OutputCode);
    }

    [Fact]
    public void Strip_OutputIsSyntacticallyValid()
    {
        var src = """class C { public void M(int a, string b = "x", bool c = true) { } }""";
        var result = Strip(src);
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should be valid C#. Errors:\n" + string.Join("\n", errors));
    }

    private static DefaultParameterStripperResult Strip(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("strip-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var stripper = new DefaultParameterStripper();
        return stripper.Strip(src, compilation.GetSemanticModel(tree));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "DefaultParameterStripperTests" -v`
Expected: FAIL — `DefaultParameterStripper` not found

- [ ] **Step 3: Write the rewriter implementation**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs`:

This `CSharpSyntaxRewriter` overrides `VisitMethodDeclaration`, `VisitConstructorDeclaration`, and `VisitLocalFunctionStatement`. For each, it follows the spec §4.1 coverage matrix:

**Key logic for `VisitMethodDeclaration`:**

```csharp
public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
{
    if (!HasDefaults(node.ParameterList.Parameters))
        return base.VisitMethodDeclaration(node);

    // Skip partial / extern (spec §4.1)
    if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword) || m.IsKind(SyntaxKind.ExternKeyword)))
    {
        _stats.SkippedUnsupported++;
        return base.VisitMethodDeclaration(node);
    }

    // Explicit interface impl: strip defaults only, no overload (spec §4.1)
    // Override: strip defaults only, no overload (spec §4.1)
    bool stripOnly = node.ExplicitInterfaceSpecifier != null
        || node.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));

    _changed = true;
    _stats.DeclarationsStripped++;

    var strippedParams = StripDefaults(node.ParameterList);
    var strippedNode = node.WithParameterList(strippedParams);

    if (stripOnly || !_options.GenerateOverloads)
        return base.VisitMethodDeclaration(strippedNode)!;

    // Generate overloads
    var overloads = DefaultParameterOverloadBuilder.BuildOverloads(node, _model!, _stats);

    // For interface DIM: convert overload bodies to expression-bodied
    if (node.Parent is InterfaceDeclarationSyntax)
        overloads = overloads.ConvertAll(ToExpressionBodied);

    // For abstract: overload is concrete (not abstract)
    // (FilterModifiers already strips abstract)

    var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(strippedNode)!;
    var newMembers = new List<MemberDeclarationSyntax> { visited };
    newMembers.AddRange(overloads);
    return SyntaxFactory.List(newMembers);
}
```

**Helper methods:**

```csharp
private static ParameterListSyntax StripDefaults(ParameterListSyntax paramList)
{
    var newParams = paramList.Parameters
        .Select(p => p.Default != null ? p.WithDefault(null) : p)
        .ToArray();
    return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParams));
}

private static bool HasDefaults(SeparatedSyntaxList<ParameterSyntax> parameters)
    => parameters.Any(p => p.Default != null);

private static MethodDeclarationSyntax ToExpressionBodied(MethodDeclarationSyntax method)
{
    // Convert { return X(...); } → => X(...)
    if (method.Body is { Statements: [ExpressionStatementSyntax es] } body)
    {
        if (body.Statements[0].Expression is InvocationExpressionSyntax inv)
            return method.WithBody(null)
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(inv))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }
    if (method.Body is { Statements: [ReturnStatementSyntax rs] } body2
        && body2.Statements[0].Expression != null)
    {
        return method.WithBody(null)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                body2.Statements[0].Expression!))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }
    return method;
}
```

**For `VisitConstructorDeclaration`:** Same pattern — strip defaults, then `DefaultParameterOverloadBuilder.BuildOverloads(ctor, model, stats)`. Skip static constructors (no params). For struct parameterless constructor, check `LanguageVersion` per spec §4.5.

**For `VisitLocalFunctionStatement`:** Strip defaults, then `DefaultParameterOverloadBuilder.BuildOverloads(localFunc, model, stats)`.

**Skip entirely (return `base.Visit...`):** `VisitDelegateDeclaration`, `VisitIndexerDeclaration`, `VisitOperatorDeclaration`, `VisitConversionOperatorDeclaration`, `VisitPropertyDeclaration`, property accessors.

- [ ] **Step 4: Write the public entry class**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripper.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed class DefaultParameterStripper
{
    private readonly DefaultParameterCatalog _catalog = new();

    /// <summary>Pass 1: scan a syntax tree and register existing signatures.</summary>
    public void Collect(SyntaxTree tree, SemanticModel model)
        => _catalog.Collect(tree, model);

    /// <summary>Pass 2: strip defaults + generate overloads.</summary>
    public DefaultParameterStripperResult Strip(
        string sourceCode,
        SemanticModel? model = null,
        DefaultParameterStripperOptions? options = null)
    {
        options ??= new DefaultParameterStripperOptions();
        var stats = new DefaultParameterStripperStatistics { FilesScanned = 1 };
        var diagnostics = new List<DefaultParameterStripDiagnostic>();

        var tree = model != null
            ? model.SyntaxTree
            : CSharpSyntaxTree.ParseText(sourceCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Dirty check: any ParameterSyntax with Default in managed context
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new DefaultParameterStripperResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new DefaultParameterDeclarationRewriter(
            model, options, stats, diagnostics, _catalog);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            stats.FilesSkippedClean = 1;
            return new DefaultParameterStripperResult(sourceCode, false, diagnostics, stats);
        }

        using var workspace = new AdhocWorkspace();
        newRoot = (CompilationUnitSyntax)Formatter.Format(newRoot, workspace);

        stats.FilesTransformed = 1;
        return new DefaultParameterStripperResult(
            newRoot.ToFullString(), true, diagnostics, stats);
    }

    /// <summary>
    /// Dirty check: any Method/Constructor/LocalFunction with a parameter
    /// that has Default != null. Excludes delegate/indexer/operator.
    /// </summary>
    private static bool IsDirty(CompilationUnitSyntax root)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.ParameterList.Parameters.Any(p => p.Default != null))
                return true;
        }
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            if (ctor.ParameterList.Parameters.Any(p => p.Default != null))
                return true;
        }
        foreach (var local in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (local.ParameterList.Parameters.Any(p => p.Default != null))
                return true;
        }
        return false;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "DefaultParameterStripperTests" -v`
Expected: PASS (some tests may need rework on the rewriter's `VisitMethodDeclaration` return type — the rewriter must return a `SyntaxList<MemberDeclarationSyntax>` when inserting overloads, which requires wrapping in a `ClassDeclarationSyntax` rewrite. Adjust the rewriter to override `VisitClassDeclaration` / `VisitStructDeclaration` / `VisitInterfaceDeclaration` to insert generated overloads into the member list.)

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripper.cs tests/CSharpToJava.Tests/DefaultParameterStripperTests.cs
git commit -m "feat(strip-default): add DefaultParameterDeclarationRewriter and DefaultParameterStripper entry"
```

---

## Task 5: DefaultParameterVerifier (Pass 3 self-check)

**Files:**
- Create: `src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterVerifier.cs`
- Test: `tests/CSharpToJava.Tests/DefaultParameterVerifierTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/CSharpToJava.Tests/DefaultParameterVerifierTests.cs`:
```csharp
using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public class DefaultParameterVerifierTests
{
    [Fact]
    public void Verify_NoResidualDefaults_ReturnsNoErrors()
    {
        var code = "class C { public void M(int a) { } }";
        var errors = DefaultParameterVerifier.Verify(code);
        Assert.Empty(errors);
    }

    [Fact]
    public void Verify_ResidualDefaultInMethod_ReturnsError()
    {
        var code = """class C { public void M(int a, string b = "x") { } }""";
        var errors = DefaultParameterVerifier.Verify(code);
        Assert.Single(errors);
        Assert.Equal(DefaultParameterStripSeverity.Error, errors[0].Severity);
    }

    [Fact]
    public void Verify_DelegateDefault_Ignored()
    {
        var code = "delegate void D(int a = 1);";
        var errors = DefaultParameterVerifier.Verify(code);
        Assert.Empty(errors);
    }

    [Fact]
    public void Verify_IndexerDefault_Ignored()
    {
        var code = "class C { public int this[int i = 0] { get => 0; set { } } }";
        var errors = DefaultParameterVerifier.Verify(code);
        Assert.Empty(errors);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "DefaultParameterVerifierTests" -v`
Expected: FAIL — `DefaultParameterVerifier` not found

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterVerifier.cs`:
```csharp
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// Pass 3: re-parses the output syntax tree and asserts no residual
/// ParameterSyntax.Default exists in managed contexts (methods,
/// constructors, local functions). Excludes delegate, indexer,
/// operator, partial, extern, and property accessor contexts.
/// </summary>
internal static class DefaultParameterVerifier
{
    public static List<DefaultParameterStripDiagnostic> Verify(string sourceCode)
    {
        var errors = new List<DefaultParameterStripDiagnostic>();
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();

        // Check methods (excluding delegate, explicit interface impl still checked)
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // Skip partial / extern — they are intentionally not stripped
            if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)
                || m.IsKind(SyntaxKind.ExternKeyword)))
                continue;

            CheckParameters(method.ParameterList.Parameters, "method", method.Identifier.Text, errors);
        }

        // Check constructors
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            CheckParameters(ctor.ParameterList.Parameters, "constructor", ".ctor", errors);
        }

        // Check local functions
        foreach (var local in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            CheckParameters(local.ParameterList.Parameters, "local function", local.Identifier.Text, errors);
        }

        // Note: DelegateDeclarationSyntax, IndexerDeclarationSyntax,
        // OperatorDeclarationSyntax, ConversionOperatorDeclarationSyntax
        // are NOT checked — their defaults are intentionally preserved (spec §4.1).

        return errors;
    }

    private static void CheckParameters(
        SeparatedSyntaxList<ParameterSyntax> parameters,
        string context, string memberName,
        List<DefaultParameterStripDiagnostic> errors)
    {
        foreach (var param in parameters)
        {
            if (param.Default != null)
            {
                errors.Add(new DefaultParameterStripDiagnostic(
                    DefaultParameterStripSeverity.Error,
                    $"Residual default value found in {context} '{memberName}' parameter '{param.Identifier.Text}'"));
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "DefaultParameterVerifierTests" -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterVerifier.cs tests/CSharpToJava.Tests/DefaultParameterVerifierTests.cs
git commit -m "feat(strip-default): add DefaultParameterVerifier for Pass 3 self-check"
```

---

## Task 6: ProjectDefaultParameterStripper (CLI project preprocessor)

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectDefaultParameterStripper.cs`

- [ ] **Step 1: Write the implementation**

`src/CSharpToJava.CLI/ProjectDefaultParameterStripper.cs`:

This class follows the exact structure of `ProjectGenericPreprocessor` (in `src/CSharpToJava.CLI/ProjectGenericPreprocessor.cs`). Copy that file as a template and make these changes:

1. **Class name:** `ProjectDefaultParameterStripper`
2. **Request class:** `ProjectDefaultParameterStripRequest` with properties:
   - `SourcePath`, `DestinationRoot`, `Force = true`, `Verbose`
   - `FilterUnsupportedProjects = true`
   - `CompatNamespacePrefixes`
   - `VerifyBuild = true`, `VerifyTests = true`
   - `RewriterOptions` of type `DefaultParameterStripperOptions`

3. **Result class:** `ProjectDefaultParameterStripResult` with properties:
   - `OriginalSourcePath`, `SourceRoot`, `IntermediateRoot`, `PreprocessedSourcePath`
   - `Success`, `Statistics`, `Diagnostics`

4. **`IntermediateDirectoryName`** = `".cs2j-strip-default-src"`

5. **In `PreprocessAsync`:**
   - Replace `GenericPreprocessor` with `DefaultParameterStripper`
   - Replace `GenericPreprocessOptions` with `DefaultParameterStripperOptions`
   - Replace `GenericPreprocessStatistics` with `DefaultParameterStripperStatistics`
   - Replace `GenericPreprocessDiagnostic` with `DefaultParameterStripDiagnostic`
   - Replace `GenericPreprocessSeverity` with `DefaultParameterStripSeverity`
   - Use `preprocessor.Collect(tree, model)` in Pass 1 (before the per-file strip loop)
   - Use `preprocessor.Strip(sourceCode, model, request.RewriterOptions)` in Pass 2
   - After Pass 2, add Pass 3: call `DefaultParameterVerifier.Verify(outputCode)` on each transformed file; add Error diagnostics for any residual defaults
   - Use `ProjectUnsignedTypePreprocessor.GetBasicReferences()` for references
   - Use `CSharpParseOptions(preprocessorSymbols: new[] { "NETCOREAPP" })` for parsing
   - Copy the `VerifyBuildAndTestsAsync`, `RunDotNetCommandAsync`, `DeleteBuildOutputDirectories`, and `MergeStatistics` methods from `ProjectGenericPreprocessor`, adapting the types

6. **Log prefix:** `"[strip-default]"`

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: Build succeeded with 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.CLI/ProjectDefaultParameterStripper.cs
git commit -m "feat(strip-default): add ProjectDefaultParameterStripper for project-level preprocessing"
```

---

## Task 7: CLI verb `strip-default` and `convert-project` integration

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: Add `StripDefaultOptions` verb class**

Add after the `GenericPreprocessOptions` class (around line 3803):

```csharp
[Verb("strip-default", HelpText = "Strip default parameter values and generate C# overloads")]
class StripDefaultOptions
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
    [Option("strict", Default = false, HelpText = "Treat skipped declarations as fatal")]
    public bool Strict { get; set; }
    [Option("no-overloads", Default = false, HelpText = "Strip defaults but don't generate overloads")]
    public bool NoOverloads { get; set; }

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after strip-default preprocessing")]
    public bool NoVerifyBuild { get; set; }

    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after strip-default preprocessing")]
    public bool NoVerifyTests { get; set; }

    public bool FilterUnsupportedProjects => !NoFilterProjects;
    public bool VerifyBuild => !NoVerifyBuild;
    public bool VerifyTests => !NoVerifyTests;
}
```

- [ ] **Step 2: Register the verb in `ParseArguments`**

Modify line 26 to add `StripDefaultOptions`:

```csharp
return await Parser.Default.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions>(args)
    .MapResult(
        (ConvertOptions opts) => ConvertFile(opts),
        (ConvertProjectOptions opts) => ConvertProject(opts),
        (AnalyzeOptions opts) => AnalyzeProject(opts),
        (EliminateGotoOptions opts) => EliminateGoto(opts),
        (MakeReadOnlyOptions opts) => MakeReadonly(opts),
        (UnsignedTypeOptions opts) => UnsignedType(opts),
        (GenericPreprocessOptions opts) => GenericPreprocess(opts),
        (StripDefaultOptions opts) => StripDefault(opts),
        errs => Task.FromResult(1)
    );
```

- [ ] **Step 3: Add the `StripDefault` handler method**

Add after the `GenericPreprocess` method (around line 3620). Follow the exact pattern of `GenericPreprocess(opts)`:

```csharp
private static async Task<int> StripDefault(StripDefaultOptions opts)
{
    var rewriterOptions = new DefaultParameterStripperOptions(
        Verbose: opts.Verbose,
        Strict: opts.Strict,
        GenerateOverloads: !opts.NoOverloads);

    if (opts.Input != null)
    {
        // Single-file mode (same pattern as GenericPreprocess single-file)
        if (!File.Exists(opts.Input))
        {
            Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
            return 1;
        }
        var sourceCode = await File.ReadAllTextAsync(opts.Input);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("strip-default",
            new[] { tree },
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var stripper = new DefaultParameterStripper();
        var result = stripper.Strip(sourceCode, compilation.GetSemanticModel(tree), rewriterOptions);
        if (opts.Output != null)
        {
            await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
            if (opts.Verbose) Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
        }
        else Console.Write(result.OutputCode);
        foreach (var d in result.Diagnostics)
            Console.Error.WriteLine($"[{d.Severity}] {d.Message}");
        return result.Diagnostics.Any(d => d.Severity == DefaultParameterStripSeverity.Error) ? 1 : 0;
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

    var preprocessResult = await ProjectDefaultParameterStripper.PreprocessAsync(
        new ProjectDefaultParameterStripRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = opts.Destination,
            Force = true,
            Verbose = opts.Verbose,
            RewriterOptions = rewriterOptions,
            FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
            VerifyBuild = opts.VerifyBuild,
            VerifyTests = opts.VerifyTests,
        });

    Console.WriteLine(
        $"strip-default: {preprocessResult.Statistics.FilesTransformed} transformed, " +
        $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
        $"{preprocessResult.Statistics.DeclarationsStripped} declarations stripped, " +
        $"{preprocessResult.Statistics.OverloadsGenerated} overloads generated");

    foreach (var diagnostic in preprocessResult.Diagnostics)
    {
        Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
    }

    Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");

    return preprocessResult.Success ? 0 : 1;
}
```

- [ ] **Step 4: Add `--no-strip-default` to `ConvertProjectOptions`**

Add after `NoUnsignedType` (around line 3947):

```csharp
[Option("no-strip-default", Default = false,
    HelpText = "Disable the default strip-default-parameter preprocessing step")]
public bool NoStripDefault { get; set; }
```

And add a convenience property (near line 3978):

```csharp
public bool StripDefault => !NoStripDefault;
```

- [ ] **Step 5: Insert strip-default step in `ConvertProject` before unsigned**

Insert before the `if (opts.UnsignedType)` block (line 188):

```csharp
if (opts.StripDefault)
{
    var stripResult = await ProjectDefaultParameterStripper.PreprocessAsync(
        new ProjectDefaultParameterStripRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = opts.Destination,
            Force = opts.Force,
            Verbose = opts.Verbose,
            FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
            CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
            VerifyBuild = opts.VerifyBuild,
            VerifyTests = opts.VerifyTests,
        });

    Console.WriteLine(
        $"strip-default: {stripResult.Statistics.FilesTransformed} transformed, " +
        $"{stripResult.Statistics.FilesSkippedClean} clean, " +
        $"{stripResult.Statistics.DeclarationsStripped} declarations stripped, " +
        $"{stripResult.Statistics.OverloadsGenerated} overloads generated");

    foreach (var diagnostic in stripResult.Diagnostics)
    {
        Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
    }

    if (!stripResult.Success)
    {
        return 1;
    }

    opts.Source = stripResult.PreprocessedSourcePath;
    opts.FingerprintSourcePath ??= stripResult.OriginalSourcePath;
    opts.FingerprintInputRoot ??= stripResult.SourceRoot;
}
```

- [ ] **Step 6: Add to `GetPreservedDestinationDirectories`**

Add after the `opts.GenericPreprocess` check (line 1826):

```csharp
if (!opts.NoStripDefault)
{
    preserved.Add(ProjectDefaultParameterStripper.IntermediateDirectoryName);
}
```

- [ ] **Step 7: Add to `GetProjectConversionOptionTokens`**

Add after `"unsigned-type={opts.UnsignedType}"` (line 2569):

```csharp
$"strip-default={opts.StripDefault}",
```

- [ ] **Step 8: Add `using` directive at top of `Program.cs`**

Add after line 7:

```csharp
using CSharpToJava.Core.DefaultParameterStripper;
```

- [ ] **Step 9: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: Build succeeded with 0 errors

- [ ] **Step 10: Commit**

```bash
git add src/CSharpToJava.CLI/Program.cs
git commit -m "feat(strip-default): add strip-default verb and convert-project integration"
```

---

## Task 8: SampleStripDefaultSolution fixture

**Files:**
- Create: `tests/SampleStripDefaultSolution/SampleStripDefaultLib/Defaults.cs`
- Create: `tests/SampleStripDefaultSolution/SampleStripDefaultLib/SampleStripDefaultLib.csproj`
- Create: `tests/SampleStripDefaultSolution/SampleStripDefaultApp/Program.cs`
- Create: `tests/SampleStripDefaultSolution/SampleStripDefaultApp/SampleStripDefaultApp.csproj`
- Create: `tests/SampleStripDefaultSolution/SampleStripDefaultSolution.sln`
- Modify: `.gitignore`

- [ ] **Step 1: Create Lib fixture**

`tests/SampleStripDefaultSolution/SampleStripDefaultLib/Defaults.cs`:
```csharp
namespace SampleStripDefaultLib;

public class Service
{
    public void Process(int a, string b = "default", bool c = true)
    {
    }

    public int Calculate(int x, int y = 10, int z = 20)
    {
        return x + y + z;
    }

    public Service(string name = "unnamed")
    {
    }

    public static string Greet(string name = "world")
    {
        return $"Hello, {name}";
    }
}
```

`tests/SampleStripDefaultSolution/SampleStripDefaultLib/SampleStripDefaultLib.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create App fixture**

`tests/SampleStripDefaultSolution/SampleStripDefaultApp/Program.cs`:
```csharp
using SampleStripDefaultLib;

var s = new Service();
s.Process(1);              // omit b and c
s.Process(1, "hi");        // omit c
s.Process(1, "hi", false); // all args

var result = s.Calculate(5);          // omit y and z
var result2 = s.Calculate(5, 100);    // omit z
var result3 = s.Calculate(5, 100, 200);

var greeting = Service.Greet();       // omit name
var greeting2 = Service.Greet("Alice");

var named = new Service("my-service");
```

`tests/SampleStripDefaultSolution/SampleStripDefaultApp/SampleStripDefaultApp.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SampleStripDefaultLib\SampleStripDefaultLib.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create solution file**

`tests/SampleStripDefaultSolution/SampleStripDefaultSolution.sln`:
```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SampleStripDefaultLib", "SampleStripDefaultLib\SampleStripDefaultLib.csproj", "{11111111-1111-1111-1111-111111111111}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SampleStripDefaultApp", "SampleStripDefaultApp\SampleStripDefaultApp.csproj", "{22222222-2222-2222-2222-222222222222}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

- [ ] **Step 4: Add fixture to ProjectTests csproj**

Add a new `<ItemGroup>` to `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`:

```xml
  <ItemGroup>
    <!-- Include SampleStripDefaultSolution as content so it's available at test runtime -->
    <Content Include="..\SampleStripDefaultSolution\**\*.*">
      <Link>SampleStripDefaultSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 5: Add to .gitignore**

Add to `.gitignore` (after line 93, `.cs2j-generic-src/`):

```
.cs2j-strip-default-src/
```

- [ ] **Step 6: Build to verify fixture compiles**

Run: `dotnet build tests/SampleStripDefaultSolution/SampleStripDefaultSolution.sln -c Release`
Expected: Build succeeded with 0 errors

- [ ] **Step 7: Commit**

```bash
git add tests/SampleStripDefaultSolution/ tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj .gitignore
git commit -m "feat(strip-default): add SampleStripDefaultSolution fixture"
```

---

## Task 9: Project-level integration tests

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/StripDefaultProjectTests.cs`

- [ ] **Step 1: Write the integration tests**

`tests/CSharpToJava.ProjectTests/StripDefaultProjectTests.cs`:
```csharp
using CSharpToJava.CLI;
using CSharpToJava.Core.DefaultParameterStripper;

namespace CSharpToJava.ProjectTests;

public class StripDefaultProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleStripDefaultSolution");

    [Fact]
    public async Task CrossProject_CallSiteUnchanged_AndBuildSucceeds()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleStripDefaultSolution.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output");

        try
        {
            var result = await ProjectDefaultParameterStripper.PreprocessAsync(
                new ProjectDefaultParameterStripRequest
                {
                    SourcePath = slnPath,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.FilesTransformed > 0);
            Assert.True(result.Statistics.OverloadsGenerated > 0);

            // D1: App call sites must be byte-for-byte identical to original
            var originalAppPath = Path.Combine(FixtureDir, "SampleStripDefaultApp", "Program.cs");
            var preprocessedAppPath = Path.Combine(
                result.IntermediateRoot, "SampleStripDefaultApp", "Program.cs");
            var originalContent = await File.ReadAllTextAsync(originalAppPath);
            var preprocessedContent = await File.ReadAllTextAsync(preprocessedAppPath);
            Assert.Equal(originalContent, preprocessedContent);

            // Lib: defaults stripped
            var libPath = Path.Combine(result.IntermediateRoot, "SampleStripDefaultLib", "Defaults.cs");
            var libContent = await File.ReadAllTextAsync(libPath);
            Assert.DoesNotContain("= \"default\"", libContent);
            Assert.DoesNotContain("= true)", libContent);
            Assert.DoesNotContain("= 10)", libContent);
            Assert.DoesNotContain("= 20)", libContent);
            Assert.DoesNotContain("= \"unnamed\"", libContent);
            Assert.DoesNotContain("= \"world\"", libContent);

            // Overloads generated
            Assert.Contains("void Process(int a)", libContent);
            Assert.Contains("void Process(int a, string b)", libContent);
            Assert.Contains("int Calculate(int x)", libContent);
            Assert.Contains("int Calculate(int x, int y)", libContent);
            Assert.Contains("Service()", libContent);
            Assert.Contains("static string Greet()", libContent);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task NoDefaults_FilesTransformedZero()
    {
        var libDir = Path.Combine(FixtureDir, "SampleStripDefaultApp");
        var outputDir = Path.Combine(FixtureDir, ".test-output-nodata");

        try
        {
            var result = await ProjectDefaultParameterStripper.PreprocessAsync(
                new ProjectDefaultParameterStripRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                    FilterUnsupportedProjects = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(0, result.Statistics.FilesTransformed);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Rerun_DoesNotLeaveOldArtifacts()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleStripDefaultSolution.sln");
        var outputDir = Path.Combine(FixtureDir, ".test-output-rerun");

        try
        {
            for (var i = 0; i < 2; i++)
            {
                var result = await ProjectDefaultParameterStripper.PreprocessAsync(
                    new ProjectDefaultParameterStripRequest
                    {
                        SourcePath = slnPath,
                        DestinationRoot = outputDir,
                        Verbose = false,
                    });
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            }
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "StripDefaultProjectTests" -v`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.ProjectTests/StripDefaultProjectTests.cs
git commit -m "feat(strip-default): add project-level integration tests"
```

---

## Task 10: Full regression test

**Files:**
- None (test only)

- [ ] **Step 1: Build entire solution**

Run: `dotnet build -c Release`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test -c Release`
Expected: All tests pass (including existing tests — no regressions from the default-on `convert-project` integration)

- [ ] **Step 3: If regressions found in convert-project tests**

If any `CSharpToJava.ProjectTests` or `CSharpToJava.ConversionTests` that use `convert-project` fail, check whether the strip-default step is the cause. If so, add `--no-strip-default` to those specific test cases or fix the rewriter.

- [ ] **Step 4: Commit if any fixes were needed**

```bash
git add -A
git commit -m "fix(strip-default): resolve regressions in convert-project tests"
```

---

## Self-Review

### 1. Spec coverage

| Spec section | Task(s) | Notes |
|---|---|---|
| §3 Module layout | Task 1 (records), Task 2 (catalog), Task 3 (overload builder), Task 4 (rewriter + entry), Task 5 (verifier) | All 8 files created |
| §4.1 Coverage matrix | Task 4 (rewriter handles method/ctor/abstract/interface/local/explicit-impl/override; skip delegate/indexer/operator/partial/extern) | |
| §4.2 Strip defaults | Task 4 (`StripDefaults` helper) | |
| §4.3 Overload generation | Task 3 (`DefaultParameterOverloadBuilder`) | |
| §4.4 Conflict & hiding detection | Task 2 (`DefaultParameterCatalog`), Task 4 (rewriter checks catalog) | |
| §4.5 Struct parameterless ctor | Task 4 (rewriter `VisitConstructorDeclaration` checks `LanguageVersion`) | |
| §4.6 Formatting | Task 4 (`DefaultParameterStripper.Strip` uses `Formatter.Format`) | |
| §5.1 Project-level pipeline | Task 6 (`ProjectDefaultParameterStripper`) | |
| §5.2 Standalone verb | Task 7 (`StripDefaultOptions` + `StripDefault` handler) | |
| §5.3 `convert-project` integration | Task 7 (step 5-8) | |
| §6 Known limitations | Task 4 (delegate skipped, `[Optional]` not handled, partial/extern skipped) | |
| §7.1 Unit tests | Task 4 (`DefaultParameterStripperTests`) | |
| §7.2 Project-level tests | Task 9 (`StripDefaultProjectTests`) | |
| §7.4 Regression | Task 10 | |
| §8 Logging | Task 7 (uses `Console.Error.WriteLine` for diagnostics, matching existing pattern) | |

### 2. Placeholder scan

No TBD/TODO/placeholder text found. All steps contain concrete code or specific instructions referencing spec sections.

### 3. Type consistency

- `DefaultParameterStripperOptions` — consistent across Task 1 (create), Task 4 (used in `Strip`), Task 6 (project request), Task 7 (CLI handler)
- `DefaultParameterStripperResult` — Task 1 (create), Task 4 (returned by `Strip`)
- `DefaultParameterStripperStatistics` — Task 1 (create), Task 3 (passed to `BuildOverloads`), Task 4 (populated in `Strip`), Task 6 (aggregated in project preprocessor)
- `DefaultParameterStripDiagnostic` / `DefaultParameterStripSeverity` — Task 1 (create), Task 4 (collected in rewriter), Task 5 (produced by verifier), Task 6 (aggregated in project preprocessor), Task 7 (printed in handler)
- `DefaultParameterCatalog` — Task 2 (create), Task 4 (used by rewriter for conflict checks)
- `DefaultParameterOverloadBuilder.BuildOverloads` — Task 3 (create with 3 overloads: Method, Constructor, LocalFunction), Task 4 (called by rewriter)
- `DefaultParameterVerifier.Verify` — Task 5 (create), Task 6 (called in project preprocessor Pass 3)
- `ProjectDefaultParameterStripper.PreprocessAsync` — Task 6 (create), Task 7 (called in `ConvertProject` and `StripDefault` handler)
- `IntermediateDirectoryName` = `".cs2j-strip-default-src"` — consistent in Task 6 (definition), Task 7 (`GetPreservedDestinationDirectories`)