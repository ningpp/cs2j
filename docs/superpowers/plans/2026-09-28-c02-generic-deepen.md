# C02 Generic Deepen (`generic-deepen`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Flip the `generic-deepen` registry stub (catalog C02) to `Implemented` and deliver the Analyze→Plan→Rewrite Core pass that makes reified-generic runtime information explicit on the C# intermediate tree (token anchors for `typeof(T)`/`is T`/`as T`, declaration-pattern and `catch(T)` lowering, static-generic-field holders, static-method type-parameter hoisting), with diagnostics CS2J11xx and a residual gate.

**Architecture:** Whole-compilation analyzer (Roslyn `SemanticModel` over the runner's single merged `CSharpCompilation`) produces symbol-level plans; a per-file `CSharpSyntaxRewriter` (visiting the compilation's own syntax trees so node identity holds) applies active plans; generated holder source files are distributed per project in `AfterFilesAsync` (Tuple-pass pattern). Registry flip follows the established stub→Implemented path (descriptor in `PassCatalog`, adapter in `PassChainFactory`, stub removal + C30/C33 re-registration in `PassStubCatalog`). The C#→Java converter itself is NOT modified.

**Tech Stack:** .NET 10, Roslyn (Microsoft.CodeAnalysis.CSharp), xUnit 2.9.3. Spec: `docs/superpowers/specs/2026-09-28-c02-generic-deepen-design.md` (reviewed & amended 2026-09-28 — §13 records verification results; notably holder storage is lock-guarded `Dictionary<Type,object>` because TypeMappings.json has no ConcurrentDictionary mapping, and GD-STATIC v1 uses same-name shadowing with no override-family surgery).

**Spec milestones mapping:** Task 1–2 = M0, Task 3 = M1, Task 4 = M2, Task 5–6 = M3, Task 7 = M4. M5 (default-on flip) is intentionally out of scope (gated on spec §8 acceptance incl. sdk triage).

---

## Conventions

- **Worktree:** all code changes happen in `.worktrees/preproc-c02-generic-deepen` (branch `feature/preproc-c02-generic-deepen`, base `ff216bcc`). The spec/plan docs live in the main checkout; do not edit code there.
- **Build/test commands** (run from worktree root):
  - Unit: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepen"`
  - Project: `dotnet test tests/CSharpToJava.ProjectTests -c Release --filter "FullyQualifiedName~GenericDeepen"`
  - Registry: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~PassChainOrder|FullyQualifiedName~PassStubCatalog|FullyQualifiedName~PassListFormatter"`
  - Full: `dotnet build CSharpToJavaConverter.slnx -c Release` then `dotnet test` on each of the three test projects.
- **Existing-failure baseline:** `CrossProjectGenericClassTokenTests` = 2 failed / 2 passed before this work; must not exceed 2 failures after.
- **Commit style:** `feat(preprocessing): ...` / `test(preprocessing): ...` / `docs: ...`, one commit per task (or per milestone chunk for Tasks 5–7).
- **E10** = the existing `generic` pass (`GenericPreprocessor`). C02 runs after it and must not double-inject anchors where E10 sentinels (`var __cs2j_typeof_T = typeof(T);`) already exist.

---

### Task 0: Worktree setup

- [ ] **Step 1: Create the worktree**

```bash
cd /d/code/cs2j
git worktree add .worktrees/preproc-c02-generic-deepen -b feature/preproc-c02-generic-deepen ff216bcc
cd .worktrees/preproc-c02-generic-deepen
dotnet build CSharpToJavaConverter.slnx -c Release
```

Expected: build succeeds (0 errors). If not, stop and reconcile with the main checkout state first.

---

### Task 1 (M0): Core skeleton — options, diagnostics, statistics, result, syntax facts, shared ExpressionTreeFacts

**Files:**
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenOptions.cs`
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenDiagnostics.cs`
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenStatistics.cs`
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenResult.cs`
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenSyntaxFacts.cs`
- Create: `src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs`
- Modify: `src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs` (delegate `IsExpressionTreeLambda`)
- Test: `tests/CSharpToJava.Tests/GenericDeepenFactsTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/CSharpToJava.Tests/GenericDeepenFactsTests.cs`:

```csharp
using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.GenericPreprocess;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

public class GenericDeepenFactsTests
{
    private static (SemanticModel Model, LambdaExpressionSyntax Lambda) BindLambda(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("gd-facts",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var lambda = tree.GetRoot().DescendantNodes().OfType<LambdaExpressionSyntax>().First();
        return (model, lambda);
    }

    [Fact]
    public void ExpressionTreeFacts_Detects_ExpressionLambda()
    {
        var (model, lambda) = BindLambda(
            "using System; using System.Linq.Expressions; class C { void M() { Expression<Func<int,bool>> e = x => x > 1; } }");
        Assert.True(ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda));
    }

    [Fact]
    public void ExpressionTreeFacts_FuncLambda_IsNotExpressionTree()
    {
        var (model, lambda) = BindLambda(
            "using System; class C { void M() { Func<int,bool> f = x => x > 1; } }");
        Assert.False(ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda));
    }

    [Fact]
    public void SyntaxFacts_Names_AreDeterministic()
    {
        Assert.Equal("__cs2j_token_T", GenericDeepenSyntaxFacts.AnchorVariableName("T"));
        Assert.Equal("__cs2j_typeof_TKey", GenericDeepenSyntaxFacts.E10SentinelVariableName("TKey"));
        Assert.Equal("__Cs2jGenericHolder_", GenericDeepenSyntaxFacts.HolderPrefix);
    }

    [Fact]
    public void SyntaxFacts_IsTypeParameterType_SemanticAndFallback()
    {
        var tree = CSharpSyntaxTree.ParseText(
            "class C { void M<T>() { object o = typeof(T); } }");
        var compilation = CSharpCompilation.Create("gd-facts2",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var typeNode = tree.GetRoot().DescendantNodes().OfType<TypeOfExpressionSyntax>().First().Type;
        Assert.True(GenericDeepenSyntaxFacts.IsTypeParameterType(model, typeNode));
        // No-model conservative fallback: single uppercase letter qualifies; predefined names don't.
        Assert.True(GenericDeepenSyntaxFacts.IsTypeParameterType(null,
            SyntaxFactory.ParseTypeName("T")));
        Assert.False(GenericDeepenSyntaxFacts.IsTypeParameterType(null,
            SyntaxFactory.ParseTypeName("Int32")));
        Assert.True(GenericDeepenSyntaxFacts.IsTypeParameterType(null,
            SyntaxFactory.ParseTypeName("TValue")));
    }

    [Fact]
    public void Statistics_Merge_SumsCounters()
    {
        var a = new GenericDeepenStatistics { TokenAnchorsInserted = 1, IsAsSitesCovered = 2 };
        var b = new GenericDeepenStatistics { TokenAnchorsInserted = 3 };
        a.Merge(b);
        Assert.Equal(4, a.TokenAnchorsInserted);
        Assert.Equal(2, a.IsAsSitesCovered);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenFacts"`
Expected: compile errors (types not defined).

- [ ] **Step 3: Create the Core files**

`src/CSharpToJava.Core/GenericPreprocess/GenericDeepenOptions.cs`:

```csharp
namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Options for the generic-deepen (C02) preprocessing pass.</summary>
public sealed record GenericDeepenOptions
{
    /// <summary>GD-ANCHOR: insert token anchor statements at method entries.</summary>
    public bool RewriteTypeOfAnchors { get; init; } = true;

    /// <summary>GD-ISAS-B: lower type-parameter declaration patterns into statement form.</summary>
    public bool RewriteIsAsPatterns { get; init; } = true;

    /// <summary>GD-CATCH: lower catch(T) into a filtered catch.</summary>
    public bool RewriteCatchTypeParam { get; init; } = true;

    /// <summary>GD-STATIC: hoist class type parameters into static methods.</summary>
    public bool RewriteStaticMembers { get; init; } = true;

    /// <summary>GD-FIELD: replace static generic fields with holder accessors.</summary>
    public bool RewriteStaticFieldHolders { get; init; } = true;

    /// <summary>Propagate token needs across forwarding call edges and override families.</summary>
    public bool PropagateNestedTokens { get; init; } = true;

    /// <summary>Run the post-rewrite residual scan (strict turns findings into errors).</summary>
    public bool RunResidualScan { get; init; } = true;

    /// <summary>Residual findings and warnings become errors.</summary>
    public bool Strict { get; init; }

    /// <summary>Emit Info-level diagnostics.</summary>
    public bool ReportInfo { get; init; } = true;
}
```

`src/CSharpToJava.Core/GenericPreprocess/GenericDeepenDiagnostics.cs`:

```csharp
namespace CSharpToJava.Core.GenericPreprocess;

public enum GenericDeepenSeverity { Info, Warning, Error }

public enum GenericDeepenRule { Anchor, IsAs, Catch, StaticMember, StaticField, Nested, Residual, Infrastructure }

public sealed record GenericDeepenDiagnostic(
    GenericDeepenSeverity Severity,
    GenericDeepenRule Rule,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null)
{
    public override string ToString()
    {
        var level = Severity switch
        {
            GenericDeepenSeverity.Error => "error",
            GenericDeepenSeverity.Warning => "warning",
            _ => "info",
        };
        return $"{FilePath ?? "<memory>"}:{Line?.ToString() ?? "?"}: {level} {Code}: [{Rule}] {Message}";
    }
}

/// <summary>CS2J11xx diagnostic codes for the generic-deepen pass (spec §6).</summary>
public static class GenericDeepenCodes
{
    public const string WholeTreeRollback = "CS2J1100";
    public const string UndecidableSite = "CS2J1101";
    public const string StaticFamilyNotClosable = "CS2J1102";
    public const string StaticFieldNotRewritable = "CS2J1103";
    public const string PatternNotLiftable = "CS2J1104";
    public const string CatchLoweringPreconditionFailed = "CS2J1105";
    public const string CallSiteOutsideCompilation = "CS2J1106";
    public const string ExpressionTreeSite = "CS2J1110";
    public const string ResidualFound = "CS2J1120";
    public const string RestoreOrBuildFailed = "CS2J1130";
    public const string TestsFailed = "CS2J1131";
    public const string PlanNotSelfContained = "CS2J1190";
    public const string Info = "GD001";
}
```

`src/CSharpToJava.Core/GenericPreprocess/GenericDeepenStatistics.cs`:

```csharp
namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Counters for the generic-deepen pass (adapter merges per-file results).</summary>
public sealed class GenericDeepenStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int TokenAnchorsInserted;
    public int AnchorsReconciledFromE10;
    public int IsAsSitesCovered;
    public int TypeParamPatternsRewritten;
    public int CatchTypeParamsRewritten;
    public int StaticMethodsRewritten;
    public int StaticFieldHoldersGenerated;
    public int StaticFieldAccessorsGenerated;
    public int StaticFieldAccessesRewritten;
    public int CallSitesUpdated;
    public int NestedPropagations;
    public int ExpressionTreeSitesSkipped;
    public int PlansSkipped;
    public Dictionary<string, int> SkippedByReason { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ResidualsByRule { get; } = new(StringComparer.Ordinal);

    public void Merge(GenericDeepenStatistics source)
    {
        FilesScanned += source.FilesScanned;
        FilesTransformed += source.FilesTransformed;
        FilesSkippedClean += source.FilesSkippedClean;
        TokenAnchorsInserted += source.TokenAnchorsInserted;
        AnchorsReconciledFromE10 += source.AnchorsReconciledFromE10;
        IsAsSitesCovered += source.IsAsSitesCovered;
        TypeParamPatternsRewritten += source.TypeParamPatternsRewritten;
        CatchTypeParamsRewritten += source.CatchTypeParamsRewritten;
        StaticMethodsRewritten += source.StaticMethodsRewritten;
        StaticFieldHoldersGenerated += source.StaticFieldHoldersGenerated;
        StaticFieldAccessorsGenerated += source.StaticFieldAccessorsGenerated;
        StaticFieldAccessesRewritten += source.StaticFieldAccessesRewritten;
        CallSitesUpdated += source.CallSitesUpdated;
        NestedPropagations += source.NestedPropagations;
        ExpressionTreeSitesSkipped += source.ExpressionTreeSitesSkipped;
        PlansSkipped += source.PlansSkipped;
        MergeCounters(SkippedByReason, source.SkippedByReason);
        MergeCounters(ResidualsByRule, source.ResidualsByRule);
    }

    public string SummaryLine()
        => $"generic-deepen: {FilesTransformed} transformed, {FilesSkippedClean} clean " +
           $"({TokenAnchorsInserted} anchors, {TypeParamPatternsRewritten} patterns, " +
           $"{CatchTypeParamsRewritten} catches, {StaticMethodsRewritten} static methods, " +
           $"{StaticFieldAccessesRewritten} field accesses, {CallSitesUpdated} call sites, " +
           $"{ResidualsByRule.Values.Sum()} residuals)";

    private static void MergeCounters(Dictionary<string, int> target, Dictionary<string, int> source)
    {
        foreach (var (key, value) in source)
        {
            target.TryGetValue(key, out var existing);
            target[key] = existing + value;
        }
    }
}
```

Note: `SummaryLine` uses `System.Linq.Sum` — the file needs `using System.Linq;` at the top (add it).

`src/CSharpToJava.Core/GenericPreprocess/GenericDeepenResult.cs`:

```csharp
namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Result of rewriting one file.</summary>
public sealed record GenericDeepenFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<GenericDeepenDiagnostic> Diagnostics,
    GenericDeepenStatistics Statistics)
{
    public static GenericDeepenFileResult Unchanged(string sourceCode) =>
        new(sourceCode, false, Array.Empty<GenericDeepenDiagnostic>(), new GenericDeepenStatistics());
}
```

`src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// Shared predicate for lambdas assigned to Expression&lt;TDelegate&gt; (expression trees).
/// Extracted from JavaKeywordSyntaxFacts so every pass uses one definition (catalog §4 risk 8).
/// </summary>
public static class ExpressionTreeFacts
{
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

`src/CSharpToJava.Core/GenericPreprocess/GenericDeepenSyntaxFacts.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Naming conventions and type-parameter detection shared by generic-deepen (C02).</summary>
public static class GenericDeepenSyntaxFacts
{
    public const string AnchorPrefix = "__cs2j_token_";
    public const string E10SentinelPrefix = "__cs2j_typeof_";
    public const string HolderPrefix = "__Cs2jGenericHolder_";
    public const string GeneratedFileStem = "_Cs2jGenericHolders";

    public static string AnchorVariableName(string typeParamName) => AnchorPrefix + typeParamName;

    public static string E10SentinelVariableName(string typeParamName) => E10SentinelPrefix + typeParamName;

    /// <summary>Holder class name for a containing type, e.g. "Outer`1+Inner`2" → "Outer_1_Inner_2".</summary>
    public static string HolderTypeName(string containingTypeMetadataName)
        => HolderPrefix + containingTypeMetadataName
            .Replace('`', '_')
            .Replace('+', '_');

    /// <summary>
    /// Semantic-first type-parameter detection; falls back to the E10 conservative syntax
    /// heuristic (single uppercase letter, or "T"-prefixed Pascal name) only when the model
    /// cannot resolve the symbol.
    /// </summary>
    public static bool IsTypeParameterType(SemanticModel? model, TypeSyntax? type)
    {
        if (type == null) return false;
        if (type is NullableTypeSyntax nullable) type = nullable.ElementType;
        if (type is ArrayTypeSyntax array) type = array.ElementType;
        if (type is not IdentifierNameSyntax identifier) return false;

        if (model != null)
        {
            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol is ITypeParameterSymbol) return true;
            if (symbol != null) return false;
        }

        var name = identifier.Identifier.ValueText;
        return LooksLikeTypeParameterName(name);
    }

    internal static bool LooksLikeTypeParameterName(string name)
        => (name.Length == 1 && char.IsUpper(name[0]))
           || (name.Length > 1 && name[0] == 'T' && char.IsUpper(name[1]) && !IsPredefinedTypeName(name));

    internal static bool IsPredefinedTypeName(string name) => name is
        "TObject" or "TString" or "TBoolean" or "TByte" or "TSByte" or "TChar" or "TDecimal"
        or "TDouble" or "TSingle" or "TInt32" or "TUInt32" or "TInt64" or "TUInt64"
        or "TInt16" or "TUInt16" or "TIntPtr" or "TUIntPtr" or "TObject[]" or "TEnum"
        or "TDelegate" or "TAction" or "TFunc" or "TType" or "TException" or "TAttribute";
}
```

(The predefined-name list mirrors the E10 helper in `GenericSyntaxRewriter.IsPredefinedTypeName`; after creating this file, open `src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs` and change the private helpers `IsTypeParameterType`/`IsTypeParameterElementType`/`IsPredefinedTypeName` (L670-743) to delegate to `GenericDeepenSyntaxFacts` so the two passes cannot drift — keep their signatures, replace bodies with one-line delegations.)

`src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs` — replace the body of `IsExpressionTreeLambda` (L56-63) with a delegation and add the using:

```csharp
using CSharpToJava.Core.Analysis;
// ... existing usings ...
    /// <summary>判断 lambda 是否赋给 Expression&lt;TDelegate&gt;（表达式树）。</summary>
    public static bool IsExpressionTreeLambda(SemanticModel model, LambdaExpressionSyntax lambda)
        => ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenFacts"`
Expected: 5 passed. Also run `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~JavaKeyword"` to confirm the extraction broke nothing.

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core tests/CSharpToJava.Tests
git commit -m "feat(preprocessing): generic-deepen core skeleton (options/diagnostics/statistics/facts) + shared ExpressionTreeFacts"
```

---

### Task 2 (M0): Registry flip + CLI adapter

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs` (PassIds const + Implemented entry)
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs` (remove GenericDeepen stub + `Ids.GenericDeepen`; add `closed-metadata`/`variance-audit` stubs; fix attribute-normalize RunAfter)
- Create: `src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs` (switch case after `PassIds.Generic`)
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs` (locked chain 16→17 ids)
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs` (stub count 23→24)
- Modify: `tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs` (descriptor existence/dir-name assertions)
- Test: `tests/CSharpToJava.Tests/Preprocessing/GenericDeepenRegistryTests.cs`

- [ ] **Step 1: Write the failing registry tests**

`tests/CSharpToJava.Tests/Preprocessing/GenericDeepenRegistryTests.cs`:

```csharp
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

public class GenericDeepenRegistryTests
{
    [Fact]
    public void GenericDeepen_Is_Implemented_WithC02CatalogItems()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.GenericDeepen);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Equal("C02", descriptor.CatalogItems);
        Assert.Equal(".cs2j-generic-deepen-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.False(descriptor.DefaultEnabled);
        Assert.Equal(new[] { PassIds.Generic }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
    }

    [Fact]
    public void Registry_PlacesGenericDeepen_BetweenGenericAndRecordReadOnly()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.Select(p => p.Id).ToList();
        var gd = ordered.IndexOf("generic-deepen");
        Assert.True(gd > 0, "generic-deepen must be registered");
        Assert.Equal("generic", ordered[gd - 1]);
        Assert.Equal("record-readonly", ordered[gd + 1]);
    }

    [Fact]
    public void C30_C33_HaveIndependentStubs()
    {
        var registry = PassCatalog.BuildRegistry();
        var closed = registry.Require("closed-metadata");
        var variance = registry.Require("variance-audit");
        Assert.Equal(PassStatus.Stub, closed.Status);
        Assert.Equal("C30", closed.CatalogItems);
        Assert.Contains("generic-deepen", closed.RunAfterIds);
        Assert.Equal(PassStatus.Stub, variance.Status);
        Assert.Equal("C33", variance.CatalogItems);
    }

    [Fact]
    public void AttributeNormalize_StillAnchorsAfterGenericDeepen()
    {
        var attribute = PassCatalog.BuildRegistry().Require("attribute-normalize");
        Assert.Contains("generic-deepen", attribute.RunAfterIds);
    }

    [Fact]
    public void Factory_CreatesAdapter_ForGenericDeepen()
    {
        var config = new PassChainConfiguration(
        )
        {
        };
        // PassChainConfiguration has three required lazy factories; build via the same
        // helper PassChainOrderTests uses (copy its config construction into this test):
        // MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
        // RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
        // JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
        var adapter = PassChainFactory.CreateAdapter(PassIds.GenericDeepen, TestChainConfig.Build());
        Assert.Equal("generic-deepen", adapter.Descriptor.Id);
    }
}
```

If `PassChainConfiguration` construction is awkward standalone, add a tiny internal static `TestChainConfig.Build()` helper next to `PassChainOrderTests` (or make this test live in the same file region and reuse its local helper) — mirror whatever `PassChainOrderTests.CreateAdapter_constructs_adapter_for_every_implemented_pass` (L149-165) already does and copy that construction verbatim.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenRegistry"`
Expected: FAIL — `PassIds.GenericDeepen` undefined / ImplementedById throws.

- [ ] **Step 3: Apply registry edits**

`src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs` — in `PassIds`, after `public const string Generic = "generic";` add:

```csharp
    public const string GenericDeepen = "generic-deepen";
```

In `PassCatalog.Implemented`, immediately after the `generic` entry (before `record-readonly`) insert:

```csharp
        new(
            PassIds.GenericDeepen,
            "Generic erasure deepening",
            Adapters.GenericDeepenPassAdapter.IntermediateDirectoryName,
            PassStage.Abi,
            PassStatus.Implemented,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Generic },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Make reified generic runtime information explicit for Java type erasure (typeof(T)/is-as T/static generic fields).",
            catalogItems: "C02"),
```

`src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs`:
1. Delete the `Ids.GenericDeepen` const line and the whole generic-deepen stub entry (L111-120).
2. Add two new id constants to `Ids` (alphabetical-ish placement next to the others):

```csharp
        public const string ClosedMetadata = "closed-metadata";
        public const string VarianceAudit = "variance-audit";
```

3. Change attribute-normalize's `runAfterIds: new[] { Ids.GenericDeepen }` → `runAfterIds: new[] { PassIds.GenericDeepen }`.
4. Add the two replacement stubs where the generic-deepen stub used to sit:

```csharp
        new(
            Ids.ClosedMetadata,
            "Closed-type metadata",
            ".cs2j-closed-metadata-src",
            PassStage.Abi,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.GenericDeepen },
            description: "Consume closed java.lang.reflect.Type metadata keyed by generic-deepen tokens (P2; blocked on PatchClosed* archaeology).",
            catalogItems: "C30"),
        new(
            Ids.VarianceAudit,
            "Variance and constraint audit",
            ".cs2j-variance-audit-src",
            PassStage.Declarative,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Generic },
            description: "Audit out/in variance and struct/new() constraints; report usage sites needing rewrite (P3).",
            catalogItems: "C33"),
```

`src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs`:

```csharp
using CSharpToJava.Core.GenericPreprocess;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the generic-deepen (C02) pass: whole-tree analyze once, per-file
/// rewrite, generated holder files distributed after all files (Tuple pattern).
/// </summary>
internal sealed class GenericDeepenPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-generic-deepen-src";

    private readonly GenericDeepenOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly GenericDeepenPreprocessor _preprocessor = new();
    private readonly List<GenericDeepenDiagnostic> _diagnostics = new();
    private readonly GenericDeepenStatistics _statistics = new();

    public GenericDeepenPassAdapter(
        GenericDeepenOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.GenericDeepen);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public GenericDeepenStatistics Statistics => _statistics;
    public IReadOnlyList<GenericDeepenDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        _preprocessor.BindCompilation(compilation);
        _preprocessor.Analyze(_options);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(context.SemanticModel!);
        _statistics.Merge(result.Statistics);
        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine($"[generic-deepen] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Warning,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Error,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.RestoreOrBuildFailed,
            $"dotnet build failed with exit code {exitCode} after generic-deepen preprocessing; the rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Error,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after generic-deepen preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == GenericDeepenSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != GenericDeepenSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();
}
```

Note: at this milestone `GenericDeepenPreprocessor` does not exist yet — create the empty facade in the same commit:

`src/CSharpToJava.Core/GenericPreprocess/GenericDeepenPreprocessor.cs` (M0 stub version):

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>
/// Core entry for the generic-deepen pass: bind the whole-tree compilation once,
/// analyze symbol-level plans, then rewrite file trees and expose generated files.
/// </summary>
public sealed class GenericDeepenPreprocessor
{
    private Compilation? _compilation;
    private GenericDeepenAnalysis _analysis = GenericDeepenAnalysis.Empty;
    private GenericDeepenOptions _options = new();

    public GenericDeepenAnalysis Analysis => _analysis;

    public void BindCompilation(Compilation compilation) => _compilation = compilation;

    public GenericDeepenAnalysis Analyze(GenericDeepenOptions options)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        _options = options;
        _analysis = new GenericDeepenAnalyzer(_compilation, options).Analyze();
        return _analysis;
    }

    /// <summary>Rewrite the syntax tree backing <paramref name="model"/> (same tree instance the plans were built from).</summary>
    public GenericDeepenFileResult RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        var stats = new GenericDeepenStatistics();
        stats.FilesScanned = 1;
        var diagnostics = new List<GenericDeepenDiagnostic>();
        var byNode = new Dictionary<Microsoft.CodeAnalysis.SyntaxNode, TokenAnchorPlan>();
        foreach (var plan in _analysis.Anchors)
        {
            if (plan.Declaration.SyntaxTree == model.SyntaxTree)
            {
                byNode[plan.Declaration] = plan;
            }
        }

        if (byNode.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            return new GenericDeepenFileResult(model.SyntaxTree.ToString(), false, diagnostics, stats);
        }

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var rewriter = new GenericDeepenRewriter(byNode, stats, diagnostics, model.SyntaxTree.FilePath);
        var newRoot = rewriter.Visit(root);
        if (newRoot == root)
        {
            stats.FilesSkippedClean = 1;
            return new GenericDeepenFileResult(model.SyntaxTree.ToString(), false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new GenericDeepenFileResult(TryFormat(newRoot), true, diagnostics, stats);
    }

    private static string TryFormat(SyntaxNode root)
    {
        try
        {
            return root.NormalizeWhitespace().ToFullString();
        }
        catch
        {
            return root.ToFullString();
        }
    }
}
```

with a minimal `GenericDeepenAnalysis` + `TokenAnchorPlan` + `GenericDeepenParamRef` in `GenericDeepenAnalyzer.cs` (full version in Task 3 — for M0, `Analyze()` may return `GenericDeepenAnalysis.Empty` with an empty plan list; the file must compile and the adapter must byte-copy clean trees).

`src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs` — after the `PassIds.Generic` case add:

```csharp
            PassIds.GenericDeepen => new GenericDeepenPassAdapter(
                new GenericDeepenOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),
```

- [ ] **Step 4: Update the locked tests**

`PassChainOrderTests.Registry_order_matches_the_locked_historical_chain`: insert `"generic-deepen"` in the expected 16-id sequence right after `"generic"` (now 17 ids).
`PassStubCatalogTests`: change the expected stub count from 23 to 24.
`ProjectPreprocessorsConsistentTests`: add to the existence/`IntermediateDirectoryName` block:

```csharp
        // generic-deepen: adapter-declared directory (no facade class by design).
        Assert.Equal(".cs2j-generic-deepen-src",
            PassCatalog.ImplementedById(PassIds.GenericDeepen).IntermediateDirectoryName);
        Assert.NotNull(Type.GetType("CSharpToJava.CLI.Preprocessing.Adapters.GenericDeepenPassAdapter, CSharpToJava.CLI"));
```

(If the type-existence assertion is awkward because of internals, keep only the descriptor assertion — the registry tests already cover the rest.)

- [ ] **Step 5: Run the registry + order tests**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~PassChainOrder|FullyQualifiedName~PassStubCatalog|FullyQualifiedName~PassListFormatter|FullyQualifiedName~GenericDeepenRegistry|FullyQualifiedName~ProjectPreprocessorsConsistent"`
Expected: all PASS.

- [ ] **Step 6: Verify the CLI surface**

Run: `dotnet run --project src/CSharpToJava.CLI -c Release -- preprocess --list-passes | grep generic`
Expected: `generic-deepen` row shows `Implemented` and ` off `, plus `closed-metadata`/`variance-audit` stub rows.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(preprocessing): flip generic-deepen stub to Implemented (C02) + register closed-metadata/variance-audit stubs"
```

---

### Task 3 (M1): Token anchors — analyzer, rewriter, E10 reconciliation, family sync, forwarding

**Files:**
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenAnalyzer.cs` (full implementation)
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenRewriter.cs`
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs` (delegate type-param helpers to `GenericDeepenSyntaxFacts`)
- Test: `tests/CSharpToJava.Tests/GenericDeepenTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/CSharpToJava.Tests/GenericDeepenTests.cs`:

```csharp
using CSharpToJava.Core.GenericPreprocess;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class GenericDeepenTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
    };

    private static GenericDeepenFileResult Run(
        string source,
        GenericDeepenOptions? options = null,
        string fileName = "Test.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        var compilation = CSharpCompilation.Create("gd-test",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new GenericDeepenPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options ?? new GenericDeepenOptions());
        return preprocessor.RewriteFile(compilation.GetSemanticModel(tree));
    }

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("gd-verify",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void TypeOfT_GetsAnchorAtBlockStart()
    {
        var result = Run("class C { public string N<T>() { return typeof(T).Name; } }");
        Assert.True(result.Changed);
        Assert.Contains("global::System.Type __cs2j_token_T = typeof(T);", result.OutputCode);
        Assert.Contains("CS2J", string.Join(";", result.Diagnostics.Select(d => d.Code))); // may be empty; anchors are silent
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void IsT_AsT_Kept_AndAnchored()
    {
        var result = Run("""
            class C {
                public bool F<T>(object v) where T : class { return v is T; }
                public T? G<T>(object v) where T : class { return v as T; }
            }
            """);
        Assert.True(result.Changed);
        Assert.Contains("v is T", result.OutputCode);
        Assert.Contains("v as T", result.OutputCode);
        Assert.Contains("__cs2j_token_T", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void NoNeed_UnchangedFile()
    {
        var result = Run("class C { public int Add(int a, int b) { return a + b; } }");
        Assert.False(result.Changed);
    }

    [Fact]
    public void E10Sentinel_NotDoubleInjected()
    {
        var result = Run("""
            class C {
                public string Forward<T>(object v) {
                    var __cs2j_typeof_T = typeof(T);
                    return Describe<T>(v);
                }
                public string Describe<T>(object v) { return typeof(T).Name + v; }
            }
            """);
        Assert.Single(result.OutputCode.Split("__cs2j_token_").Where(s => s != result.OutputCode) .Count() > 0
            ? result.OutputCode.Split("__cs2j_token_").Length - 1
            : 0);
        Assert.DoesNotContain("var __cs2j_typeof_T = typeof(T);\r\n        global::System.Type __cs2j_token_T", result.OutputCode.Replace("\n", "\r\n"));
        Assert.Equal(1, result.Statistics.AnchorsReconciledFromE10 + 0); // T reconciled in Forward; Describe gets its own anchor
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ExpressionBodiedMethod_IsWrapped()
    {
        var result = Run("class C { public string N<T>() => typeof(T).Name; }");
        Assert.True(result.Changed);
        Assert.Contains("global::System.Type __cs2j_token_T = typeof(T);", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void AnchorNameCollision_GetsSuffix()
    {
        var result = Run("""
            class C {
                public string N<T>() {
                    var __cs2j_token_T = 0;
                    var n = typeof(T).Name;
                    return __cs2j_token_T + n.Length.ToString();
                }
            }
            """);
        Assert.Contains("__cs2j_token_T_2 = typeof(T);", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void OverrideFamily_SyncsAnchorToSiblings()
    {
        var result = Run("""
            abstract class B {
                public virtual string N<T>(T x) { return "b"; }
            }
            class D : B {
                public override string N<T>(T x) { return typeof(T).Name; }
            }
            """);
        Assert.True(result.Changed);
        // Both members carry the anchor; D keeps its typeof(T) body.
        Assert.Equal(2, result.OutputCode.Split("__cs2j_token_T = typeof(T)").Length - 1);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void AbstractFamily_MaterializesStubBody()
    {
        var result = Run("""
            abstract class B {
                public abstract string N<T>(T x);
            }
            class D : B {
                public override string N<T>(T x) { return typeof(T).Name; }
            }
            """);
        Assert.True(result.Changed);
        Assert.Contains("virtual string", result.OutputCode.Replace("public virtual", "virtual"));
        Assert.Contains("typeof(T);", result.OutputCode);
        Assert.Contains("throw new global::System.NotImplementedException();", result.OutputCode);
        Assert.DoesNotContain("abstract string N", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ForwardingCall_PropagatesAnchorToCaller()
    {
        var result = Run("""
            class C {
                public string Forward<T>(object v) { return Describe<T>(v); }
                public string Describe<T>(object v) { return typeof(T).Name + v; }
            }
            """);
        Assert.True(result.Changed);
        var forward = result.OutputCode.Substring(result.OutputCode.IndexOf("Forward"));
        Assert.Contains("__cs2j_token_T", forward.Substring(0, forward.IndexOf("Describe")));
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ClassLevelTypeParam_AnchoredInInstanceMethod()
    {
        var result = Run("""
            class C<T> {
                public string Name() { return typeof(T).Name; }
            }
            """);
        Assert.True(result.Changed);
        Assert.Contains("global::System.Type __cs2j_token_T = typeof(T);", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void MultipleParams_OrderedClassThenMethod()
    {
        var result = Run("""
            class Outer<TK> {
                public string N<TV>() { return typeof(TK).Name + typeof(TV).Name; }
            }
            """);
        Assert.True(result.Changed);
        var tk = result.OutputCode.IndexOf("__cs2j_token_TK");
        var tv = result.OutputCode.IndexOf("__cs2j_token_TV");
        Assert.True(tk >= 0 && tv > tk, "class param anchor must precede method param anchor");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenTests"`
Expected: FAIL (anchor plans not yet produced).

- [ ] **Step 3: Implement `GenericDeepenAnalyzer.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

public enum GenericDeepenParamKind { ClassTypeParameter, MethodTypeParameter }

/// <summary>Type parameter referenced by (kind, ordinal) so family members with renamed parameters stay comparable.</summary>
public sealed record GenericDeepenParamRef(GenericDeepenParamKind Kind, int Ordinal, string Name)
    : IComparable<GenericDeepenParamRef>
{
    public int CompareTo(GenericDeepenParamRef? other)
    {
        if (other == null) return 1;
        var kind = Kind.CompareTo(other.Kind);
        return kind != 0 ? kind : Ordinal.CompareTo(other.Ordinal);
    }
}

public sealed class TokenAnchorPlan
{
    public required IMethodSymbol Method { get; init; }
    public required MethodDeclarationSyntax Declaration { get; init; }
    public required IReadOnlyList<GenericDeepenParamRef> Params { get; init; }
    /// <summary>Member without a body (abstract/interface): materialize a stub body carrying typeof statements (E10 precedent).</summary>
    public bool MaterializeBody { get; init; }
    public bool IsInterfaceMember { get; init; }
    public bool FromFamilySync { get; init; }
    public string FilePath => Declaration.SyntaxTree.FilePath;
}

public sealed class GenericDeepenAnalysis
{
    public required IReadOnlyList<TokenAnchorPlan> Anchors { get; init; }
    public required IReadOnlyList<GenericDeepenDiagnostic> Diagnostics { get; init; }
    public required GenericDeepenStatistics Statistics { get; init; }

    public static GenericDeepenAnalysis Empty { get; } = new()
    {
        Anchors = Array.Empty<TokenAnchorPlan>(),
        Diagnostics = Array.Empty<GenericDeepenDiagnostic>(),
        Statistics = new GenericDeepenStatistics(),
    };
}

public sealed class GenericDeepenAnalyzer
{
    private readonly Compilation _compilation;
    private readonly GenericDeepenOptions _options;
    private readonly List<GenericDeepenDiagnostic> _diagnostics = new();

    private readonly Dictionary<IMethodSymbol, MethodDeclarationSyntax> _methodDecls = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, SortedSet<GenericDeepenParamRef>> _needs = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, List<ForwardEdge>> _forwardEdges = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, List<IMethodSymbol>> _families = new(SymbolEqualityComparer.Default);

    private readonly record struct ForwardEdge(IMethodSymbol Callee, int CalleeParamOrdinal, GenericDeepenParamRef CallerParam);

    private sealed class ParamRefComparer : IComparer<GenericDeepenParamRef>
    {
        public static readonly ParamRefComparer Instance = new();
        public int Compare(GenericDeepenParamRef? x, GenericDeepenParamRef? y) => (x ?? throw new ArgumentNullException(nameof(x))).CompareTo(y);
    }

    public GenericDeepenAnalyzer(Compilation compilation, GenericDeepenOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    public GenericDeepenAnalysis Analyze()
    {
        foreach (var tree in _compilation.SyntaxTrees.Where(t => t.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            WalkTree(tree);
        }

        BuildFamilies();
        PropagateNeeds();

        var anchors = new List<TokenAnchorPlan>();
        foreach (var (method, needs) in _needs)
        {
            if (needs.Count == 0) continue;
            if (!_methodDecls.TryGetValue(method, out var declaration)) continue;
            var isInterface = method.ContainingType?.TypeKind == TypeKind.Interface;
            var hasBody = declaration.Body != null || declaration.ExpressionBody != null;
            var isPartial = declaration.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
            if (isPartial && !hasBody)
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.Anchor,
                    GenericDeepenCodes.StaticFamilyNotClosable,
                    $"Partial method '{method.ToDisplayString()}' needs a token anchor but has no body; skipped.",
                    declaration.SyntaxTree.FilePath, declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            anchors.Add(new TokenAnchorPlan
            {
                Method = method,
                Declaration = declaration,
                Params = needs.ToList(),
                MaterializeBody = !hasBody,
                IsInterfaceMember = isInterface,
            });
        }

        anchors.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath) != 0
            ? string.CompareOrdinal(a.FilePath, b.FilePath)
            : a.Declaration.SpanStart.CompareTo(b.Declaration.SpanStart));

        return new GenericDeepenAnalysis
        {
            Anchors = anchors,
            Diagnostics = _diagnostics,
            Statistics = new GenericDeepenStatistics(),
        };
    }

    // ---- phase 1: per-tree walk ----

    private void WalkTree(SyntaxTree tree)
    {
        var model = _compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        foreach (var md in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(md);
            if (symbol == null) continue;
            var original = symbol.OriginalDefinition;
            _methodDecls[original] = md;
            foreach (var need in CollectDirectNeeds(symbol, md, model))
            {
                AddNeed(original, need);
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            CollectForwardEdge(invocation, model);
        }
    }

    private static IEnumerable<GenericDeepenParamRef> CollectDirectNeeds(
        IMethodSymbol method, MethodDeclarationSyntax declaration, SemanticModel model)
    {
        var needs = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
        var visible = CollectVisibleParams(method);
        if (visible.Count == 0) yield break;

        var bodyNodes = declaration.Body == null
            ? declaration.ExpressionBody!.DescendantNodes().ToList()
            : declaration.Body.DescendantNodes().ToList();

        foreach (var node in bodyNodes)
        {
            switch (node)
            {
                case TypeOfExpressionSyntax typeOf:
                    AddIfVisible(model, typeOf.Type, visible, needs);
                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IsExpression)
                                                       || binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AsExpression):
                    if (binary.Right is TypeSyntax rightType)
                    {
                        AddIfVisible(model, rightType, visible, needs);
                    }
                    break;
            }
        }

        foreach (var need in needs) yield return need;
    }

    private static void AddIfVisible(
        SemanticModel model, TypeSyntax type,
        List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
        ISet<GenericDeepenParamRef> needs)
    {
        var syntax = type;
        if (syntax is NullableTypeSyntax n) syntax = n.ElementType;
        if (syntax is not IdentifierNameSyntax identifier) return;
        var symbol = model.GetSymbolInfo(identifier).Symbol as ITypeParameterSymbol;
        if (symbol == null) return;
        var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, symbol));
        if (match.Ref != null) needs.Add(match.Ref);
    }

    private static List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> CollectVisibleParams(IMethodSymbol method)
    {
        var result = new List<(ITypeParameterSymbol, GenericDeepenParamRef)>();
        if (method.IsGenericMethod)
        {
            foreach (var tp in method.OriginalDefinition.TypeParameters)
            {
                result.Add((tp, new GenericDeepenParamRef(GenericDeepenParamKind.MethodTypeParameter, tp.Ordinal, tp.Name)));
            }
        }

        var type = method.ContainingType?.OriginalDefinition;
        while (type != null)
        {
            foreach (var tp in type.TypeParameters)
            {
                result.Add((tp, new GenericDeepenParamRef(GenericDeepenParamKind.ClassTypeParameter, tp.Ordinal, tp.Name)));
            }
            type = type.ContainingType?.OriginalDefinition;
        }

        return result;
    }

    private void CollectForwardEdge(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee || !callee.IsGenericMethod)
        {
            return;
        }

        var caller = model.GetEnclosingSymbol(invocation.SpanStart) as IMethodSymbol;
        if (caller == null || !_methodDecls.ContainsKey(caller.OriginalDefinition)) return;

        var visible = CollectVisibleParams(caller);
        for (var i = 0; i < callee.TypeArguments.Length; i++)
        {
            if (callee.TypeArguments[i] is not ITypeParameterSymbol tp) continue;
            var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, tp));
            if (match.Ref == null) continue;
            if (!_forwardEdges.TryGetValue(caller.OriginalDefinition, out var edges))
            {
                edges = new List<ForwardEdge>();
                _forwardEdges[caller.OriginalDefinition] = edges;
            }
            edges.Add(new ForwardEdge(callee.OriginalDefinition, i, match.Ref));
        }
    }

    // ---- phase 2: families ----

    private void BuildFamilies()
    {
        foreach (var type in AllTypes(_compilation.Assembly.GlobalNamespace))
        {
            foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind == MethodKind.Ordinary && member.IsOverride && member.OverriddenMethod != null)
                {
                    var root = member;
                    while (root.OverriddenMethod != null) root = root.OverriddenMethod;
                    AddToFamily(root.OriginalDefinition, root.OriginalDefinition);
                    AddToFamily(root.OriginalDefinition, member.OriginalDefinition);
                }
            }

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var ifaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (type.FindImplementationForInterfaceMember(ifaceMethod) is IMethodSymbol impl)
                    {
                        AddToFamily(ifaceMethod.OriginalDefinition, ifaceMethod.OriginalDefinition);
                        AddToFamily(ifaceMethod.OriginalDefinition, impl.OriginalDefinition);
                    }
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        foreach (var nested in root.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in AllTypes(nested))
            {
                yield return deeper;
            }
        }

        foreach (var ns in root.GetNamespaceMembers())
        {
            foreach (var type in AllTypes(ns))
            {
                yield return type;
            }
        }
    }

    private void AddToFamily(IMethodSymbol root, IMethodSymbol member)
    {
        if (!_families.TryGetValue(root, out var members))
        {
            members = new List<IMethodSymbol>();
            _families[root] = members;
        }

        if (!members.Contains(member, SymbolEqualityComparer.Default)) members.Add(member);
    }

    // ---- phase 3: fixpoint ----

    private void PropagateNeeds()
    {
        bool changed;
        do
        {
            changed = false;

            foreach (var (_, members) in _families)
            {
                var union = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
                foreach (var member in members)
                {
                    if (_needs.TryGetValue(member, out var memberNeeds))
                    {
                        foreach (var need in memberNeeds) union.Add(need);
                    }
                }

                foreach (var member in members)
                {
                    if (!_needs.TryGetValue(member, out var current))
                    {
                        current = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
                        _needs[member] = current;
                    }

                    var arity = member.OriginalDefinition.TypeParameters.Length;
                    foreach (var need in union)
                    {
                        if (need.Kind == GenericDeepenParamKind.MethodTypeParameter && need.Ordinal >= arity)
                        {
                            continue; // member cannot carry this parameter; reported once below
                        }

                        if (current.Add(need)) changed = true;
                    }
                }
            }

            foreach (var (caller, edges) in _forwardEdges)
            {
                foreach (var edge in edges)
                {
                    if (!_needs.TryGetValue(edge.Callee, out var calleeNeeds)) continue;
                    if (!calleeNeeds.Contains(new GenericDeepenParamRef(GenericDeepenParamKind.MethodTypeParameter, edge.CalleeParamOrdinal, string.Empty)))
                    {
                        continue;
                    }

                    if (!_needs.TryGetValue(caller, out var callerNeeds))
                    {
                        callerNeeds = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
                        _needs[caller] = callerNeeds;
                    }

                    if (callerNeeds.Add(edge.CallerParam)) changed = true;
                }
            }
        }
        while (changed);
    }

    private void AddNeed(IMethodSymbol method, GenericDeepenParamRef need)
    {
        if (!_needs.TryGetValue(method, out var set))
        {
            set = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
            _needs[method] = set;
        }

        set.Add(need);
    }
}
```

Implementation notes (apply while writing, do not skip):
- `SortedSet<GenericDeepenParamRef>` member lookup by `(Kind, Ordinal)` ignores `Name`: record equality includes Name, so the forward-edge `Contains` probe uses a dummy name — instead write a helper `static bool HasKindOrdinal(set, kind, ordinal)` and use it; do NOT rely on record equality for that check.
- The family-union "member cannot carry this parameter" case must emit **one** `CS2J1102` Warning diagnostic per family+param (add a `_reportedFamilyGaps` HashSet of strings to deduplicate).
- E10 `Activator.CreateInstance<T>()` / `Array.CreateInstance(typeof(T), …)` forms need no special handling here: they surface as `typeof(T)` / type-argument sites already.

- [ ] **Step 4: Implement `GenericDeepenRewriter.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Applies generic-deepen plans to one syntax tree of the bound compilation.</summary>
internal sealed class GenericDeepenRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<SyntaxNode, TokenAnchorPlan> _anchorsByNode;
    private readonly GenericDeepenStatistics _stats;
    private readonly List<GenericDeepenDiagnostic> _diagnostics;
    private readonly string _filePath;

    public GenericDeepenRewriter(
        Dictionary<SyntaxNode, TokenAnchorPlan> anchorsByNode,
        GenericDeepenStatistics stats,
        List<GenericDeepenDiagnostic> diagnostics,
        string filePath)
    {
        _anchorsByNode = anchorsByNode;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (!_anchorsByNode.TryGetValue(node, out var plan)) return visited;

        if (plan.MaterializeBody)
        {
            return MaterializeStubBody(visited, plan);
        }

        visited = WrapExpressionBody(visited);
        if (visited.Body == null) return visited;

        var toInsert = AnchorsToInsert(visited, plan);
        if (toInsert.Count == 0) return visited;

        var newBody = visited.Body.WithStatements(visited.Body.Statements.InsertRange(0, toInsert));
        return visited.WithBody(newBody);
    }

    private List<StatementSyntax> AnchorsToInsert(MethodDeclarationSyntax method, TokenAnchorPlan plan)
    {
        var result = new List<StatementSyntax>();
        var body = method.Body!;
        var existing = ExistingAnchorParameterNames(body);
        var usedNames = CollectIdentifierNames(method);

        foreach (var param in plan.Params)
        {
            if (existing.Contains(param.Name))
            {
                _stats.AnchorsReconciledFromE10++;
                continue;
            }

            var variable = UniqueName(GenericDeepenSyntaxFacts.AnchorVariableName(param.Name), usedNames);
            usedNames.Add(variable);
            result.Add(CreateAnchorStatement(variable, param.Name));
            _stats.TokenAnchorsInserted++;
        }

        return result;
    }

    /// <summary>Names already anchored by E10 sentinels or earlier anchors in leading statements.</summary>
    private static HashSet<string> ExistingAnchorParameterNames(BlockSyntax body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in body.Statements)
        {
            if (statement is not LocalDeclarationStatementSyntax local) break;
            foreach (var variable in local.Declaration.Variables)
            {
                if ((variable.Identifier.ValueText.StartsWith(GenericDeepenSyntaxFacts.AnchorPrefix, StringComparison.Ordinal)
                     || variable.Identifier.ValueText.StartsWith(GenericDeepenSyntaxFacts.E10SentinelPrefix, StringComparison.Ordinal))
                    && variable.Initializer?.Value is TypeOfExpressionSyntax typeOf
                    && typeOf.Type is IdentifierNameSyntax identifier)
                {
                    names.Add(identifier.Identifier.ValueText);
                }
            }
        }

        return names;
    }

    private static HashSet<string> CollectIdentifierNames(MethodDeclarationSyntax method)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in method.DescendantNodes().SelectMany(n => n.ChildTokens())
                     .Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
        {
            names.Add(token.ValueText);
        }

        return names;
    }

    private static string UniqueName(string baseName, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = baseName + "_" + i;
            if (!usedNames.Contains(candidate)) return candidate;
        }
    }

    internal static StatementSyntax CreateAnchorStatement(string variableName, string paramText)
        => SyntaxFactory.ParseStatement($"global::System.Type {variableName} = typeof({paramText});")
            .WithLeadingTrivia(SyntaxFactory.Comment($"// __cs2j: token anchor ({paramText})"), SyntaxFactory.ElasticMarker)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

    private MethodDeclarationSyntax MaterializeStubBody(MethodDeclarationSyntax method, TokenAnchorPlan plan)
    {
        var statements = new List<StatementSyntax>();
        foreach (var param in plan.Params)
        {
            statements.Add(SyntaxFactory.ParseStatement($"typeof({param.Name});")
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed)));
        }

        statements.Add(SyntaxFactory.ParseStatement("throw new global::System.NotImplementedException();")
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        var body = SyntaxFactory.Block(statements);
        var result = method.WithBody(body).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));

        var isAbstract = result.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        if (isAbstract && plan.IsInterfaceMember == false)
        {
            var modifiers = result.Modifiers
                .Select(m => m.IsKind(SyntaxKind.AbstractKeyword) ? SyntaxFactory.Token(SyntaxKind.VirtualKeyword).WithTriviaFrom(m) : m)
                .ToTokenList();
            result = result.WithModifiers(modifiers);
        }

        _stats.TokenAnchorsInserted += plan.Params.Count;
        return result;
    }

    private static MethodDeclarationSyntax WrapExpressionBody(MethodDeclarationSyntax method)
    {
        if (method.Body != null || method.ExpressionBody == null) return method;
        var @return = SyntaxFactory.ReturnStatement(method.ExpressionBody.Expression);
        var body = SyntaxFactory.Block(@return);
        return method.WithBody(body).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
    }
}
```

(Fix the stray parenthesis in the `typeof({param.Name});` statement while transcribing — the code must compile.)

- [ ] **Step 5: Delegate E10 helpers**

In `src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs`, replace the bodies of the private `IsTypeParameterType` (L670-725), `IsTypeParameterElementType` (L727-731) and `IsPredefinedTypeName` (L733-743) in `GenericSyntaxRewriter` with delegations to `GenericDeepenSyntaxFacts` (keep signatures; single source of truth per spec §10 risk 8).

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenTests"`
Expected: all PASS. If `E10Sentinel_NotDoubleInjected` is flaky due to assertion style, tighten the assertion to: parse the output, find `Forward`, assert its body's first statement is the E10 sentinel and no `__cs2j_token_T` declaration for `T` exists inside `Forward` (assert `AnchorsReconciledFromE10 >= 1`).

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(preprocessing): GD-ANCHOR L0/L1 token anchors with override-family sync, forwarding propagation and E10 reconciliation"
```

---

### Task 4 (M2): Declaration-pattern and catch(T) lowering

**Files:**
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenAnalyzer.cs` (pattern/catch plan collection)
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenRewriter.cs` (VisitIfStatement / VisitCatchClause)
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenPreprocessor.cs` (route new plans into the per-tree node map)
- Test: append to `tests/CSharpToJava.Tests/GenericDeepenTests.cs`

- [ ] **Step 1: Write the failing tests** (append inside `GenericDeepenTests`)

```csharp
    [Fact]
    public void DeclarationPattern_LoweredToSingleEvaluationIf()
    {
        var result = Run("""
            class C {
                public string D<T>(object v) where T : class {
                    if (v is T typed) { return "yes:" + typed.ToString(); }
                    return "no";
                }
            }
            """);
        Assert.True(result.Changed);
        Assert.Contains("IsInstanceOfType(", result.OutputCode);
        Assert.Contains("T typed = (T)(object)", result.OutputCode);
        Assert.DoesNotContain("v is T typed", result.OutputCode);
        Assert.Equal(1, result.Statistics.TypeParamPatternsRewritten);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void DeclarationPattern_Discard_NoDeclaration()
    {
        var result = Run("""
            class C {
                public string D<T>(object v) where T : class {
                    if (v is T _) { return "yes"; }
                    return "no";
                }
            }
            """);
        Assert.Contains("IsInstanceOfType(", result.OutputCode);
        Assert.DoesNotContain("(object)__cs2j_pat", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void PatternInCompositeCondition_ReportsCS2J1104()
    {
        var result = Run("""
            class C {
                public bool D<T>(object v) where T : class {
                    return v is T typed && typed != null;
                }
            }
            """);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == GenericDeepenCodes.PatternNotLiftable);
    }

    [Fact]
    public void PatternVariableUsedAfterIf_ReportsCS2J1104()
    {
        var result = Run("""
            class C {
                public string D<T>(object v) where T : class {
                    if (v is T typed) { return "yes"; }
                    return typed?.ToString() ?? "no";
                }
            }
            """);
        Assert.Contains(result.Diagnostics, d => d.Code == GenericDeepenCodes.PatternNotLiftable);
    }

    [Fact]
    public void ConcretePattern_Unchanged()
    {
        var result = Run("""
            class C {
                public string D(object v) {
                    if (v is string typed) { return "yes:" + typed; }
                    return "no";
                }
            }
            """);
        Assert.False(result.Changed);
    }

    [Fact]
    public void CatchTypeParam_LoweredToFilteredCatch()
    {
        var result = Run("""
            class C {
                public string R<TException>(global::System.Action a) where TException : global::System.Exception {
                    try { a(); return "ok"; }
                    catch (TException ex) { return "caught:" + ex.Message; }
                }
            }
            """);
        Assert.True(result.Changed);
        Assert.Contains("catch (global::System.Exception __cs2j_e", result.OutputCode);
        Assert.Contains("IsAssignableFrom(__cs2j_e", result.OutputCode);
        Assert.Contains("TException ex = (TException)(object)__cs2j_e", result.OutputCode);
        Assert.Equal(1, result.Statistics.CatchTypeParamsRewritten);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CatchTypeParam_WithoutVariable_Lowered()
    {
        var result = Run("""
            class C {
                public string R<TException>(global::System.Action a) where TException : global::System.Exception {
                    try { a(); return "ok"; }
                    catch (TException) { return "caught"; }
                }
            }
            """);
        Assert.Contains("IsAssignableFrom(", result.OutputCode);
        Assert.DoesNotContain("(object)__cs2j_e", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CatchTypeParam_WithFilter_ReportsCS2J1105()
    {
        var result = Run("""
            class C {
                public string R<TException>(global::System.Action a) where TException : global::System.Exception {
                    try { a(); return "ok"; }
                    catch (TException ex) when (ex.Message.Length > 0) { return "caught"; }
                }
            }
            """);
        Assert.Contains(result.Diagnostics, d => d.Code == GenericDeepenCodes.CatchLoweringPreconditionFailed);
    }

    [Fact]
    public void MultiCatch_OrderPreserved()
    {
        var result = Run("""
            class C {
                public string R<TException>(global::System.Action a) where TException : global::System.Exception {
                    try { a(); return "ok"; }
                    catch (TException ex) { return "t"; }
                    catch (global::System.ArgumentException ex) { return "ae"; }
                    catch { return "any"; }
                }
            }
            """);
        var t = result.OutputCode.IndexOf("catch (global::System.Exception __cs2j_e");
        var ae = result.OutputCode.IndexOf("catch (global::System.ArgumentException");
        var any = result.OutputCode.IndexOf("catch\r\n", StringComparison.Ordinal) >= 0
            ? result.OutputCode.IndexOf("catch\r\n", StringComparison.Ordinal)
            : result.OutputCode.IndexOf("catch\n", StringComparison.Ordinal);
        Assert.True(t >= 0 && ae > t && any > ae);
        AssertCompiles(result.OutputCode);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenTests"`
Expected: new tests FAIL.

- [ ] **Step 3: Add pattern/catch plans to the analyzer**

Add plan types (same file, after `TokenAnchorPlan`):

```csharp
public sealed class TypeParamPatternPlan
{
    public required IsPatternExpressionSyntax Pattern { get; init; }
    public required IfStatementSyntax If { get; init; }
    public required GenericDeepenParamRef Param { get; init; }
    public required string VariableName { get; init; }
    public required string TempName { get; init; }
    public required bool IsDiscard { get; init; }
}

public sealed class CatchTypeParamPlan
{
    public required CatchClauseSyntax Clause { get; init; }
    public required GenericDeepenParamRef Param { get; init; }
    public required string? VariableName { get; init; }
    public required string ExceptionVariableName { get; init; }
}
```

Extend `GenericDeepenAnalysis` with `Patterns` and `Catches` (default empty arrays; update `Empty` and the analyzer constructor result). Convert `CollectDirectNeeds` from `static` to an instance method and extend its body-node loop:

```csharp
case IsPatternExpressionSyntax pattern:
    CollectPattern(pattern, declaration, model, visible, needs);
    break;
case CatchClauseSyntax catchClause:
    CollectCatch(catchClause, declaration, model, visible, needs);
    break;
```

```csharp
private readonly List<TypeParamPatternPlan> _patterns = new();
private readonly List<CatchTypeParamPlan> _catches = new();
private int _patternTempCounter;

private void CollectPattern(
    IsPatternExpressionSyntax pattern,
    MethodDeclarationSyntax declaration,
    SemanticModel model,
    List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
    ISet<GenericDeepenParamRef> needs)
{
    if (pattern.Pattern is not DeclarationPatternSyntax declarationPattern
        || declarationPattern.Type is not IdentifierNameSyntax typeIdentifier
        || model.GetSymbolInfo(typeIdentifier).Symbol is not ITypeParameterSymbol tp)
    {
        return;
    }

    var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, tp));
    if (match.Ref == null) return;

    // Token need so the method entry gets an anchor.
    needs.Add(match.Ref);

    var liftable = pattern.Parent is IfStatementSyntax conditionOwner
        && conditionOwner.Condition == pattern
        && conditionOwner.Statement is BlockSyntax;

    var usedAfter = false;
    if (liftable && conditionOwner!.Parent is BlockSyntax enclosing)
    {
        var after = enclosing.Statements.Where(s => s.SpanStart >= conditionOwner.Span.End);
        usedAfter = after.Any(s => s.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(i => i.Identifier.ValueText == VariableNameOf(declarationPattern)));
    }

    if (!liftable || usedAfter)
    {
        _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Warning, GenericDeepenRule.IsAs, GenericDeepenCodes.PatternNotLiftable,
            $"Type-parameter pattern '{pattern}' is not statement-liftable here (composite condition, non-block body, or the pattern variable is used after the if); rewrite manually.",
            declaration.SyntaxTree.FilePath,
            pattern.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        return;
    }

    var designation = (SingleVariableDesignationSyntax)declarationPattern.Designation;
    var isDiscard = designation.Identifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.UnderscoreToken);
    _patterns.Add(new TypeParamPatternPlan
    {
        Pattern = pattern,
        If = conditionOwner!,
        Param = match.Ref,
        VariableName = designation.Identifier.ValueText,
        TempName = $"__cs2j_pat{_patternTempCounter++}",
        IsDiscard = isDiscard,
    });
}

private static string VariableNameOf(DeclarationPatternSyntax p)
    => ((SingleVariableDesignationSyntax)p.Designation).Identifier.ValueText;

private void CollectCatch(
    CatchClauseSyntax clause,
    MethodDeclarationSyntax declaration,
    SemanticModel model,
    List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
    ISet<GenericDeepenParamRef> needs)
{
    var caughtType = clause.Declaration?.Type;
    if (caughtType is not IdentifierNameSyntax identifier) return;
    if (model.GetSymbolInfo(identifier).Symbol is not ITypeParameterSymbol tp) return;
    var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, tp));
    if (match.Ref == null) return;

    needs.Add(match.Ref);

    if (clause.Filter != null)
    {
        _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Warning, GenericDeepenRule.Catch, GenericDeepenCodes.CatchLoweringPreconditionFailed,
            $"catch over type parameter '{identifier.Identifier.ValueText}' with a filter cannot be lowered automatically; rewrite manually.",
            declaration.SyntaxTree.FilePath,
            clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        return;
    }

    _catches.Add(new CatchTypeParamPlan
    {
        Clause = clause,
        Param = match.Ref,
        VariableName = clause.Declaration?.Identifier.ValueText,
        ExceptionVariableName = $"__cs2j_e{_catches.Count}",
    });
}
```

Expose both lists on `GenericDeepenAnalysis` (sorted by file+span like anchors).

- [ ] **Step 4: Add the rewrites**

In `GenericDeepenRewriter`: take two more dictionaries in the constructor (`patternsByNode`, `catchesByNode` keyed by `IfStatementSyntax` / `CatchClauseSyntax`), then:

```csharp
public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
{
    var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
    if (!_patternsByNode.TryGetValue(node, out var plan)) return visited;

    var originalExpression = ((IsPatternExpressionSyntax)node.Condition).Expression;
    var tempDeclaration = SyntaxFactory.ParseStatement($"var {plan.TempName} = {originalExpression};")
        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
    var condition = SyntaxFactory.ParseExpression(
        $"typeof({plan.Param.Name}).IsInstanceOfType({plan.TempName})");

    var thenStatements = new List<StatementSyntax>();
    if (!plan.IsDiscard)
    {
        thenStatements.Add(SyntaxFactory.ParseStatement(
            $"{plan.Param.Name} {plan.VariableName} = ({plan.Param.Name})(object){plan.TempName};"));
    }

    thenStatements.AddRange(((BlockSyntax)visited.Statement).Statements);
    var newIf = visited
        .WithCondition(condition)
        .WithStatement(SyntaxFactory.Block(thenStatements));

    _stats.TypeParamPatternsRewritten++;
    return SyntaxFactory.Block(tempDeclaration, newIf)
        .WithAdditionalAnnotations(); // block wrapper: temp + if share a scope, else-chain semantics preserved
}

public override SyntaxNode? VisitCatchClause(CatchClauseSyntax node)
{
    var visited = (CatchClauseSyntax)base.VisitCatchClause(node)!;
    if (!_catchesByNode.TryGetValue(node, out var plan)) return visited;

    var declaration = SyntaxFactory.CatchDeclaration(
        SyntaxFactory.ParseTypeName("global::System.Exception"),
        SyntaxFactory.Identifier(plan.ExceptionVariableName));
    var filter = SyntaxFactory.CatchFilterClause(SyntaxFactory.ParseExpression(
        $"typeof({plan.Param.Name}).IsAssignableFrom({plan.ExceptionVariableName}.GetType())"));

    var statements = new List<StatementSyntax>();
    if (plan.VariableName != null)
    {
        statements.Add(SyntaxFactory.ParseStatement(
            $"{plan.Param.Name} {plan.VariableName} = ({plan.Param.Name})(object){plan.ExceptionVariableName};"));
    }

    statements.AddRange(visited.Block?.Statements ?? Enumerable.Empty<StatementSyntax>());
    _stats.CatchTypeParamsRewritten++;
    return visited
        .WithDeclaration(declaration)
        .WithFilter(filter)
        .WithBlock(SyntaxFactory.Block(statements));
}
```

Wire the new dictionaries in `GenericDeepenPreprocessor.RewriteFile` (mirror the anchors routing, and include them in the "any plans for this tree" emptiness check).

- [ ] **Step 5: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenTests"`
Expected: PASS.

```bash
git add src tests
git commit -m "feat(preprocessing): GD-ISAS-B declaration-pattern lowering + GD-CATCH filtered catch with CS2J1104/1105 gates"
```

---

### Task 5 (M3a): Static generic fields — holder plans, holder generator, usage rewrite

**Files:**
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenHolderGenerator.cs`
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenAnalyzer.cs` (static-field plan collection + usage index)
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenRewriter.cs` (field removal + usage rewrite)
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenPreprocessor.cs` (expose usage maps + holder specs)
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs` (AfterFilesAsync writes + wires holder files)
- Test: append to `tests/CSharpToJava.Tests/GenericDeepenTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void MutableStaticGenericField_BecomesHolderAccess()
    {
        var result = Run("""
            class Box<T> where T : class {
                public static T? Latest;
                public T Keep(T v) { Latest = v; return Latest; }
            }
            """);
        Assert.True(result.Changed);
        Assert.DoesNotContain("public static T? Latest;", result.OutputCode);
        Assert.Contains("global::System.Collections.Generic", result.OutputCode + HolderText(result)); // holder carries the store
        Assert.Contains("__cs2j_set_Latest<T>(typeof(T), v)", result.OutputCode);
        Assert.Contains("__cs2j_get_Latest<T>(typeof(T))", result.OutputCode);
        AssertCompiles(result.OutputCode + HolderText(result));
    }

    [Fact]
    public void ReadonlySingletonField_BecomesAccessor()
    {
        var result = Run("""
            class Node<T> where T : class {
                public static readonly Node<T> Empty = new Node<T>();
                public Node<T>? Next;
            }
            """);
        Assert.DoesNotContain("public static readonly Node<T> Empty", result.OutputCode);
        Assert.Contains("=> new Node<T>();", HolderText(result));
        Assert.DoesNotContain("__cs2j_store", HolderText(result)); // singleton form has no dictionary
    }

    [Fact]
    public void ClosedTypeExternalUsage_PassesClosedArgs()
    {
        var result = Run("""
            class Box<T> where T : class {
                public static T? Latest;
            }
            class Consumer {
                public string Read() { return Box<string>.Latest?.Length.ToString() ?? "null"; }
            }
            """, fileName: "Consumer.cs");
        Assert.Contains("__cs2j_get_Latest<string>(typeof(string))", result.OutputCode);
    }

    [Fact]
    public void ThreadStaticField_SkippedWithInfo()
    {
        var result = Run("""
            class Box<T> where T : class {
                [global::System.ThreadStaticAttribute]
                public static T? Latest;
            }
            """);
        Assert.Contains("public static T? Latest;", result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == GenericDeepenCodes.Info);
    }

    [Fact]
    public void CompoundAssignmentOnField_ExcludesFieldWithCS2J1103()
    {
        var result = Run("""
            class Box<T> where T : class {
                public static int Count;
                public static T? Latest;
            }
            class Consumer {
                public void Bump() { Box<string>.Latest ??= default; }
            }
            """);
        Assert.Contains("public static T? Latest;", result.OutputCode); // left in place
        Assert.Contains(result.Diagnostics, d => d.Code == GenericDeepenCodes.StaticFieldNotRewritable);
    }
```

Helper used above (add to the test class):

```csharp
    private static string HolderText(GenericDeepenFileResult result)
        => string.Join("\n", GenericDeepenHolderGenerator.Generate(
            result.Statistics is null ? Array.Empty<StaticFieldPlan>() : LastAnalysis.StaticFields));
```

To make this testable, expose the analysis on the result: extend `GenericDeepenFileResult` with `GenericDeepenAnalysis? Analysis` (set by the preprocessor) and keep a `LastAnalysis` static field in the test class populated by `Run`. Simpler final shape: make `Run` return the analysis too via a tuple, or expose `preprocessor.Analysis` — preferred: change `Run` helper to `RunDetailed` returning `(GenericDeepenFileResult Result, GenericDeepenAnalysis Analysis)` and keep `Run` as a wrapper; `HolderText` joins the generator output for `Analysis.StaticFields`.

- [ ] **Step 2: Implement the plan types and analyzer collection**

```csharp
public sealed class StaticFieldUsagePlan
{
    public required SyntaxNode Node { get; init; }          // IdentifierNameSyntax of the field reference
    public required bool IsAssignmentTarget { get; init; }
    public required IReadOnlyList<string> TypeArgumentTexts { get; init; }
}

public sealed class StaticFieldPlan
{
    public required IFieldSymbol Field { get; init; }
    public required FieldDeclarationSyntax Declaration { get; init; }
    public required VariableDeclaratorSyntax Declarator { get; init; }
    public required string HolderTypeName { get; init; }
    public required string? HolderNamespace { get; init; }
    public required string FieldName { get; init; }
    public required IReadOnlyList<GenericDeepenParamRef> Params { get; init; }
    public required IReadOnlyList<string> ConstraintTexts { get; init; }
    public required string FieldTypeText { get; init; }
    public required string? InitializerText { get; init; }   // typeof(T) already replaced by token params
    public bool IsSingletonForm { get; init; }
    public bool NeedsSetter { get; init; }
    public required IReadOnlyList<StaticFieldUsagePlan> Usages { get; init; }
    public string FilePath => Declaration.SyntaxTree.FilePath;
}
```

Analyzer collection (instance method `CollectStaticFieldPlans(tree, model)` called from `WalkTree` after the method walk; only when `_options.RewriteStaticFieldHolders`):

1. For each `FieldDeclarationSyntax` with a `static` modifier inside a generic type chain (any ancestor `TypeDeclarationSyntax` with a `TypeParameterList`):
   - `[ThreadStatic]` (any attribute named `ThreadStatic`/`ThreadStaticAttribute`) → Info diagnostic (`GD001`, "owned by thread-static (C09)") and skip.
   - Multiple declarators → CS2J1103 skip (v1 single-declarator only).
   - Visible class params = type params of all ancestor type declarations (semantic: containing type chain `OriginalDefinition.TypeParameters`).
   - Referenced params = params appearing in the field type (`IFieldSymbol.Type` walked for `ITypeParameterSymbol` members of the visible set) ∪ params referenced by the initializer (identifier/`typeof` name match against visible param names).
   - Empty referenced set → not a C02 field, skip.
2. Usage index: walk all trees for `IdentifierNameSyntax` whose text equals the field name, semantic-confirm `GetSymbolInfo` resolves to the field's `OriginalDefinition`; classify:
   - inside `nameof(...)` → ignore;
   - parent (skipping member-access levels) is a simple `AssignmentExpressionSyntax` as the direct `Left`, whose parent is an `ExpressionStatementSyntax` → assignment target;
   - any appearance under a non-simple assignment (`+=`, `??=`, `++`), `ref`/`out` argument, or inside an expression-tree lambda (check with `ExpressionTreeFacts` walking ancestor lambdas) → whole-field exclusion + CS2J1103 / CS2J1110 diagnostics.
3. Render `TypeArgumentTexts` per usage: if the reference is `C<int>.F`, take the receiver `GenericName`'s type-argument texts (mapped to the containing type's parameter ordinals, filtered to the field's referenced params); if it is a bare `F`, walk ancestors to the `TypeDeclarationSyntax` matching the containing type name and use its `TypeParameterList` names; anything else (e.g. bare use in a derived class) → exclude field with CS2J1103.
4. `IsSingletonForm` = `readonly` modifier ∧ no assignment-target usages ∧ has initializer ∧ initializer contains no reference to static members of generic types (semantic check on each identifier in the initializer). Otherwise holder form.
5. `InitializerText`: serialize `Declarator.Initializer.Value`, then replace `typeof(<param>)` subtrees whose type binds to a referenced param with `__cs2j_token_<param>` (rewrite via a mini rewriter or by string replacement on exact `typeof(Name)` occurrences for each referenced param name).
6. `ConstraintTexts`: verbatim `where` clause texts of the containing type declaration filtered to referenced param names (copied onto the accessor so `T?`/`new()` stay legal).
7. `HolderTypeName = GenericDeepenSyntaxFacts.HolderTypeName(ContainingType.OriginalDefinition.MetadataName)`; `HolderNamespace = ContainingNamespace.ToDisplayString()` or null for the global namespace.

- [ ] **Step 3: Implement `GenericDeepenHolderGenerator.cs`**

```csharp
namespace CSharpToJava.Core.GenericPreprocess;

public sealed record GenericHolderFileSpec(string Content, IReadOnlyList<string> DeclaringFilePaths);

/// <summary>Generates the non-generic holder classes for static generic fields (GD-FIELD).</summary>
public static class GenericDeepenHolderGenerator
{
    /// <summary>One merged file content for the given plans (callers split by project before calling).</summary>
    public static IReadOnlyList<string> Generate(IReadOnlyList<StaticFieldPlan> plans)
    {
        var byHolder = plans.GroupBy(p => (p.HolderNamespace, p.HolderTypeName));
        var contents = new List<string>();
        foreach (var group in byHolder)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("// <auto-generated> cs2j generic-deepen (C02) static-generic-field holder. Do not edit.");
            if (group.Key.HolderNamespace is { } ns)
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
            }

            sb.AppendLine("[global::System.Runtime.CompilerServices.CompilerGenerated]");
            sb.AppendLine($"public static class {group.Key.HolderTypeName}");
            sb.AppendLine("{");
            var first = true;
            foreach (var plan in group)
            {
                if (!first) sb.AppendLine();
                first = false;
                AppendAccessors(sb, plan);
            }

            sb.AppendLine("}");
            if (group.Key.HolderNamespace is not null) sb.AppendLine("}");
            contents.Add(sb.ToString());
        }

        return contents;
    }

    private static void AppendAccessors(System.Text.StringBuilder sb, StaticFieldPlan plan)
    {
        var typeParams = string.Join(", ", plan.Params.Select(p => p.Name));
        var tokenParams = string.Join(", ",
            plan.Params.Select(p => $"global::System.Type __cs2j_token_{p.Name}"));
        var constraints = plan.ConstraintTexts.Count == 0
            ? ""
            : " " + string.Join(" ", plan.ConstraintTexts);
        var tokens = string.Join(", ", plan.Params.Select(p => $"typeof({p.Name})"));

        if (plan.IsSingletonForm)
        {
            sb.AppendLine($"    public static {plan.FieldTypeText} __cs2j_get_{plan.FieldName}<{typeParams}>({tokenParams}){constraints}");
            sb.AppendLine($"        => {plan.InitializerText};");
            return;
        }

        var key = string.Join(" + \"|\" + ",
            plan.Params.Select(p => $"__cs2j_token_{p.Name}.AssemblyQualifiedName"));
        var factory = plan.InitializerText ?? $"default({plan.FieldTypeText})";
        sb.AppendLine("    private static readonly object __cs2j_gate = new object();");
        sb.AppendLine("    private static readonly global::System.Collections.Generic.Dictionary<string, object> __cs2j_store =");
        sb.AppendLine("        new global::System.Collections.Generic.Dictionary<string, object>();");
        sb.AppendLine();
        sb.AppendLine($"    public static {plan.FieldTypeText} __cs2j_get_{plan.FieldName}<{typeParams}>({tokenParams}){constraints}");
        sb.AppendLine("    {");
        sb.AppendLine("        var __cs2j_key = " + key + ";");
        sb.AppendLine("        if (!__cs2j_store.TryGetValue(__cs2j_key, out var __cs2j_boxed))");
        sb.AppendLine("        {");
        sb.AppendLine("            lock (__cs2j_gate)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (!__cs2j_store.TryGetValue(__cs2j_key, out __cs2j_boxed))");
        sb.AppendLine("                {");
        sb.AppendLine("                    __cs2j_boxed = " + factory + ";");
        sb.AppendLine("                    __cs2j_store[__cs2j_key] = __cs2j_boxed;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine($"        return ({plan.FieldTypeText})__cs2j_boxed;");
        sb.AppendLine("    }");
        if (plan.NeedsSetter)
        {
            sb.AppendLine();
            sb.AppendLine($"    public static void __cs2j_set_{plan.FieldName}<{typeParams}>({tokenParams}, {plan.FieldTypeText} value){constraints}");
            sb.AppendLine("    {");
            sb.AppendLine("        lock (__cs2j_gate)");
            sb.AppendLine("        {");
            sb.AppendLine("            __cs2j_store[__cs2j_key_expr(__cs2j_token)] = value;".Replace("__cs2j_key_expr(__cs2j_token)", key));
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
    }
}
```

(The setter's dictionary key expression reuses the same `key` expression string — write it directly as `__cs2j_store[{key}] = value;` when transcribing; the `.Replace` trick above is only to keep the plan readable.)

- [ ] **Step 4: Rewriter field handling**

Constructor gains `fieldDeclaratorRemovals` (`HashSet<SyntaxNode>`), `fieldUsages` (`Dictionary<SyntaxNode, StaticFieldUsagePlan>`), and the `StaticFieldPlan` lookup for qualified names. Add:

```csharp
public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
{
    if (_fieldDeclRemovals.Contains(node)) return null; // single-declarator plans remove the whole statement
    return base.VisitFieldDeclaration(node);
}

public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
{
    if (node.Name is IdentifierNameSyntax name && _fieldUsages.ContainsKey(name))
    {
        return BuildFieldAccess(_fieldUsages[name]); // receiver (closed type) is discarded; type info goes into the args
    }

    return base.VisitMemberAccessExpression(node);
}

public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
{
    if (_fieldUsages.ContainsKey(node) && node.Parent is not MemberAccessExpressionSyntax)
    {
        return BuildFieldAccess(_fieldUsages[node]);
    }

    return base.VisitIdentifierName(node);
}

public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
{
    if (node.Expression is AssignmentExpressionSyntax assignment
        && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
        && FieldUsageOf(assignment.Left) is { IsAssignmentTarget: true } usage)
    {
        var right = Visit(assignment.Right) ?? assignment.Right;
        var invocation = BuildFieldSetter(usage, right);
        _stats.StaticFieldAccessesRewritten++;
        return node.WithExpression(invocation);
    }

    return base.VisitExpressionStatement(node);
}
```

`BuildFieldAccess`/`BuildFieldSetter` build `global::<ns>.<Holder>.__cs2j_get_<F><<args>>(typeof(<arg0>), …)` with `SyntaxFactory.ParseExpression`, and increment `StaticFieldAccessesRewritten`. `FieldUsageOf` resolves the left side's terminal identifier (direct or member-access name) against `_fieldUsages`. Also guard `VisitIdentifierName` against `nameof` contexts (the analyzer never plans those nodes, so no extra check is needed at rewrite time).

- [ ] **Step 5: Adapter `AfterFilesAsync`**

```csharp
public override Task AfterFilesAsync(PassTreeContext tree)
{
    DistributeHolderFiles(tree);
    return Task.CompletedTask;
}
```

`DistributeHolderFiles`: group `_preprocessor.Analysis.StaticFields` by owning project — use `ProjectCompileSet.ComputeCompiledFiles(intermediateProjectFile, tree.IntermediateRoot)` for each project in `tree.Layout` and match `plan.FilePath` (map source→intermediate paths via `tree.SourceRoot`/`tree.IntermediateRoot` prefix swap); for each non-empty group call `GenericDeepenHolderGenerator.Generate`, write `$"{GenericDeepenSyntaxFacts.GeneratedFileStem}.{projectName}.cs"` at `tree.IntermediateRoot` (UTF-8 no BOM), then wire a `<Compile Include="relative path" Link="filename" />` item into that project's csproj — copy `TuplePassAdapter.EnsureCompileItem`'s XML logic (XDocument load, remove matching entries, append ItemGroup). Increment `StaticFieldHoldersGenerated`. When no field plans exist, write nothing.

- [ ] **Step 6: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenTests"`
Expected: PASS (compile the holder text together with the rewritten file in the compile assertions).

```bash
git add src tests
git commit -m "feat(preprocessing): GD-FIELD static generic fields -> holder/accessor forms with per-project generated files"
```

---

### Task 6 (M3b): GD-STATIC — static method type-parameter hoisting

**Files:**
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenAnalyzer.cs`
- Modify: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenRewriter.cs`
- Test: append to `tests/CSharpToJava.Tests/GenericDeepenTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void StaticMethodReferencingClassParam_IsHoisted()
    {
        var result = Run("""
            class Echo<T> where T : class {
                public static T Identity(T value) => value;
            }
            class Consumer {
                public string Use() { return Echo<string>.Identity("x"); }
            }
            """);
        Assert.True(result.Changed);
        Assert.Contains("public static T Identity<T>(T value)", result.OutputCode);
        Assert.Contains("Echo<string>.Identity<string>(\"x\")", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void StaticMethod_BareCallInsideGenericClass_GetsClassArgs()
    {
        var result = Run("""
            class Echo<T> where T : class {
                public static T Identity(T value) => value;
                public static T Twice(T value) { return Identity(Identity(value)); }
            }
            """);
        Assert.Contains("Identity<T>(Identity<T>(value))", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void StaticMethodWithOwnTypeParams_SkippedWithCS2J1102()
    {
        var result = Run("""
            class Echo<T> where T : class {
                public static T Mix<V>(T a, V b) => a;
            }
            """);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == GenericDeepenCodes.StaticFamilyNotClosable);
    }

    [Fact]
    public void StaticPropertyReferencingClassParam_ReportsCS2J1102()
    {
        var result = Run("""
            class Box<T> where T : class {
                public static T? Shared => default;
            }
            """);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == GenericDeepenCodes.StaticFamilyNotClosable);
    }

    [Fact]
    public void InstanceMethodReferencingClassParam_NotHoisted()
    {
        var result = Run("""
            class Echo<T> where T : class {
                public T Identity(T value) => value;
            }
            """);
        Assert.DoesNotContain("Identity<T>(T value)", result.OutputCode);
    }

    [Fact]
    public void StaticCallerCascade_IsHoistedToo()
    {
        var result = Run("""
            class Echo<T> where T : class {
                public static T Identity(T value) => value;
                public static string Name() { return typeof(T).Name; }
                public static string Both(T v) { return Name() + Identity(v); }
            }
            """);
        Assert.Contains("public static string Both<T>(T v)", result.OutputCode);
        Assert.Contains("Identity<T>(v)", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }
```

Note: `Both` references `T` only through `Identity(v)`'s inferred forwarding and `typeof(T)` — both count (typeof directly; the inferred call edge via the constructed symbol's `TypeArguments`). The hoisted `Both` keeps its own anchor from the typeof need.

- [ ] **Step 2: Analyzer**

```csharp
public sealed class StaticMethodCallSitePlan
{
    public required InvocationExpressionSyntax Invocation { get; init; }
    public required IReadOnlyList<string> TypeArgumentTexts { get; init; }
}

public sealed class StaticMethodPlan
{
    public required IMethodSymbol Method { get; init; }
    public required MethodDeclarationSyntax Declaration { get; init; }
    public required IReadOnlyList<GenericDeepenParamRef> Params { get; init; }   // class params referenced (hoisted under the same names)
    public required IReadOnlyList<string> ConstraintTexts { get; init; }
    public required IReadOnlyList<StaticMethodCallSitePlan> CallSites { get; init; }
    public string FilePath => Declaration.SyntaxTree.FilePath;
}
```

Collection (`CollectStaticMethodPlans`, gated on `_options.RewriteStaticMembers`):
1. Candidate = `MethodKind.Ordinary`, `IsStatic`, containing-type chain has type params, `!IsPartialDefinition`/no `extern`, `TypeParameters.Length == 0`, containing type kind != Interface; references class params in signature (parameter/return types walked for visible type params) or body (identifier/typeof/is-as sites resolving to visible class params — reuse the direct-need walk, restricted to class-kind refs, plus constructed-call edges whose type argument binds to a class param).
2. Static-caller cascade fixpoint: a static method that passes a class param in a call to an already-planned method is itself a candidate (iterate until stable).
3. Exclusions with CS2J1102: own type parameters; `static abstract`/`static virtual` (interface members); method-group references outside invocations (walk `IdentifierName`s resolving to the method where the parent is not the invocation's expression, ignoring `nameof`); partial/extern bodies.
4. Call sites: all `InvocationExpressionSyntax` in the compilation resolving to the method; `TypeArgumentTexts` rendered exactly like field usages (receiver `C<int>.M` → `int`; bare inside `C<T>` → `T`; else exclude with CS2J1106).

- [ ] **Step 3: Rewriter**

`VisitMethodDeclaration` extension (after the anchor handling):

```csharp
if (_staticMethodsByNode.TryGetValue(node, out var staticPlan))
{
    visited = AttachTypeParameters(visited, staticPlan);
    _stats.StaticMethodsRewritten++;
}
```

`AttachTypeParameters` builds the `TypeParameterList` and constraint clauses by parsing a scratch declaration and stealing its nodes (avoids hand-building clause trivia):

```csharp
private static MethodDeclarationSyntax AttachTypeParameters(MethodDeclarationSyntax method, StaticMethodPlan plan)
{
    var names = string.Join(", ", plan.Params.Select(p => p.Name));
    var constraints = plan.ConstraintTexts.Count == 0 ? "" : " " + string.Join(" ", plan.ConstraintTexts);
    var scratch = SyntaxFactory.ParseMemberDeclaration(
        $"class __Scratch {{ void __M<{names}>(){constraints} {{ }} }}")!;
    var typeParameterList = scratch.DescendantNodes().OfType<TypeParameterListSyntax>().Single();
    var constraintClauses = scratch.DescendantNodes().OfType<TypeParameterConstraintClauseSyntax>().ToList();
    var updated = method.WithTypeParameterList(typeParameterList.WithTriviaFrom(method.ParameterList.CloseParenToken));
    if (constraintClauses.Count > 0)
    {
        updated = updated.WithConstraintClauses(
            SyntaxFactory.List(constraintClauses.Select((c, i) =>
                i == 0 ? c.WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed, SyntaxFactory Whitespace("    ")) : c)));
    }

    return updated;
}
```

(fix the obvious token separation typo when transcribing; the point is: parse `<T> where T : class` from a scratch member, attach with elastic trivia.)

`VisitInvocationExpression`:

```csharp
public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
{
    var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
    if (!_staticCallSites.TryGetValue(node, out var callSite)) return visited;
    var nameText = node.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax id => id.Identifier.ValueText,
        _ => null,
    };
    if (nameText == null) return visited;
    var genericName = SyntaxFactory.GenericName(
        SyntaxFactory.Identifier(nameText),
        SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList<TypeSyntax>(
            callSite.TypeArgumentTexts.Select(SyntaxFactory.ParseTypeName))));
    var expression = visited.Expression switch
    {
        MemberAccessExpressionSyntax m => m.WithName(genericName.WithTriviaFrom(m.Name)),
        IdentifierNameSyntax id => genericName.WithTriviaFrom(id),
        _ => visited.Expression,
    };
    _stats.CallSitesUpdated++;
    return visited.WithExpression(expression);
}
```

- [ ] **Step 4: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepenTests"`
Expected: PASS.

```bash
git add src tests
git commit -m "feat(preprocessing): GD-STATIC hoist class type parameters into static methods with explicit call-site type args"
```

---

### Task 7 (M4): Residual gate, expression-tree guard, fixture solution, project tests

**Files:**
- Create: `src/CSharpToJava.Core/GenericPreprocess/GenericDeepenResidualScanner.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs` (residual scan in `AfterFilesAsync`, strict handling)
- Create: `tests/SampleGenericDeepenSolution/**` (Lib + App + Tests + sln)
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` (Content wiring)
- Test: `tests/CSharpToJava.ProjectTests/GenericDeepenProjectTests.cs`

- [ ] **Step 1: Residual scanner**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Post-rewrite gate (spec §4.5): counts shapes that must be gone after C02.</summary>
public static class GenericDeepenResidualScanner
{
    public static Dictionary<string, int> Scan(Compilation rewritten)
    {
        var residuals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tree in rewritten.SyntaxTrees)
        {
            var model = rewritten.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case FieldDeclarationSyntax field when field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                            && ReferencesVisibleTypeParameter(model, field.Declaration.Type):
                        Add(residuals, "static-field");
                        break;
                    case CatchDeclarationSyntax catchDeclaration when IsTypeParameter(model, catchDeclaration.Type):
                        Add(residuals, "catch-type-param");
                        break;
                    case DeclarationPatternSyntax declaration when IsTypeParameter(model, declaration.Type):
                        Add(residuals, "type-param-pattern");
                        break;
                }
            }
        }

        return residuals;
    }

    private static void Add(Dictionary<string, int> residuals, string rule)
    {
        residuals.TryGetValue(rule, out var current);
        residuals[rule] = current + 1;
    }

    private static bool IsTypeParameter(SemanticModel model, TypeSyntax? type)
        => model.GetSymbolInfo(type ?? SyntaxFactory.ParseTypeName("int")).Symbol is ITypeParameterSymbol;

    private static bool ReferencesVisibleTypeParameter(SemanticModel model, TypeSyntax type)
    {
        foreach (var identifier in type.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier).Symbol is ITypeParameterSymbol) return true;
        }

        return false;
    }
}
```

In the adapter's `AfterFilesAsync`, after holder distribution and when `_options.RunResidualScan`: reparse every rewritten `.cs` under `tree.IntermediateRoot` (skip `obj/`/`bin/`), build a `CSharpCompilation` with `ProjectUnsignedTypePreprocessor.GetBasicReferences()` and the adapter's parse options, run the scanner, merge into `_statistics.ResidualsByRule`, and add one CS2J1120 diagnostic per rule with severity `Strict ? Error : Warning`.

- [ ] **Step 2: Create the fixture solution**

From the worktree root:

```bash
cd tests
dotnet new sln -n SampleGenericDeepenSolution -o SampleGenericDeepenSolution
dotnet new classlib -n SampleGenericDeepenLib -o SampleGenericDeepenSolution/SampleGenericGenericDeepenLib 2>/dev/null || dotnet new classlib -n SampleGenericDeepenLib -o SampleGenericDeepenSolution/SampleGenericDeepenLib
dotnet new console -n SampleGenericDeepenApp -o SampleGenericDeepenSolution/SampleGenericDeepenApp
dotnet new xunit -n SampleGenericDeepenTests -o SampleGenericDeepenSolution/SampleGenericDeepenTests
dotnet sln SampleGenericDeepenSolution/SampleGenericDeepenSolution.sln add SampleGenericDeepenSolution/SampleGenericDeepenLib SampleGenericDeepenSolution/SampleGenericDeepenApp SampleGenericDeepenSolution/SampleGenericDeepenTests
```

(Use the correctly spelled paths; align the xunit package versions with `CSharpToJava.ProjectTests.csproj` — 2.9.3 / 17.14.1 — and target `net10.0` like the other fixtures. Delete the template `Class1.cs`/`UnitTest1.cs`.)

`SampleGenericDeepenLib/GenericSamples.cs`:

```csharp
namespace SampleGenericDeepenLib;

public sealed class TypeTokenCache<T> where T : class
{
    private readonly global::System.Collections.Generic.Dictionary<global::System.Type, string> _names = new();

    public string NameOf()
    {
        var t = typeof(T);
        if (!_names.TryGetValue(t, out var n))
        {
            _names[t] = n = t.Name;
        }
        return n;
    }

    public bool IsMine(object? value) => value is T;

    public T? AsMine(object? value) => value as T;

    public string Describe(object? value)
    {
        if (value is T typed)
        {
            return "typed:" + typed.ToString();
        }
        return "other";
    }
}

public static class Guard
{
    public static string DescribeError<TException>(global::System.Action action)
        where TException : global::System.Exception
    {
        try
        {
            action();
            return "ok";
        }
        catch (TException ex)
        {
            return "caught:" + ex.Message;
        }
    }
}
```

`SampleGenericDeepenLib/StaticSamples.cs`:

```csharp
namespace SampleGenericDeepenLib;

public sealed class Latest<T> where T : class
{
    public static T? Value;

    public T Remember(T value)
    {
        Value = value;
        return value;
    }
}

public sealed class Node<T> where T : class
{
    public static readonly Node<T> Empty = new();
    public Node<T>? Next;
    public T? Payload;
}

public sealed class Echo<T> where T : class
{
    public static T Identity(T value) => value;
}
```

`SampleGenericDeepenLib/FamilySamples.cs`:

```csharp
namespace SampleGenericDeepenLib;

public abstract class Processor<T>
{
    public abstract string Key(T input);
}

public interface ICodec
{
    string Encode<T>(T value);
}
```

`SampleGenericDeepenLib/FamilySamples2.cs`:

```csharp
namespace SampleGenericDeepenLib;

public sealed class HashingProcessor<T> : Processor<T>
{
    public override string Key(T input) => typeof(T).GetHashCode().ToString();
}

public sealed class PlainCodec : ICodec
{
    public string Encode<T>(T value) => typeof(T).Name + ":" + value;
}

public sealed class Outer<T>
{
    public sealed class Inner
    {
        public static string Describe() => typeof(T).Name;
        public string InstanceDescribe() => typeof(T).Name;
    }
}
```

`SampleGenericDeepenApp/Program.cs` (App references Lib):

```csharp
using SampleGenericDeepenLib;

var latest = new Latest<string>().Remember("a");
if (Latest<string>.Value != latest) return 1;
if (Node<int>.Empty.Next is not null) return 2;
if (Echo<string>.Identity("x") != "x") return 3;
if (new TypeTokenCache<string>().Describe("a") != "typed:a") return 4;
if (Guard.DescribeError<InvalidOperationException>(() => throw new InvalidOperationException("b")) != "caught:b") return 5;
if (new HashingProcessor<int>().Key(1) != typeof(int).GetHashCode().ToString()) return 6;
if (Outer<int>.Inner.Describe() != "Int32") return 7;
return 0;
```

`SampleGenericDeepenTests/RuntimeBehaviorTests.cs` (Tests references Lib):

```csharp
using SampleGenericDeepenLib;
using Xunit;

namespace SampleGenericDeepenTests;

public class RuntimeBehaviorTests
{
    [Fact]
    public void Latest_IsolatedPerClosedType()
    {
        Latest<string>.Value = "hello";
        Latest<object>.Value = new object();
        Assert.Equal("hello", Latest<string>.Value);
        Latest<string>.Value = "second";
        Assert.Equal("second", Latest<string>.Value);
    }

    [Fact]
    public void NodeEmpty_SharedPerClosedType()
    {
        Assert.Same(Node<int>.Empty, Node<int>.Empty);
        Assert.NotSame(Node<int>.Empty, Node<string>.Empty);
        Assert.Null(Node<int>.Empty.Next);
    }

    [Fact]
    public void TypeTokenCache_DescribeAndCatch()
    {
        var cache = new TypeTokenCache<string>();
        Assert.Equal("typed:a", cache.Describe("a"));
        Assert.Equal("other", cache.Describe(5));
        Assert.Equal("ok", Guard.DescribeError<InvalidOperationException>(() => { }));
        Assert.StartsWith("caught:", Guard.DescribeError<InvalidOperationException>(
            () => throw new InvalidOperationException("boom")));
        Assert.Equal("ok", Guard.DescribeError<ArgumentException>(
            () => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void Families_AndNested()
    {
        Assert.Equal(typeof(int).GetHashCode().ToString(), new HashingProcessor<int>().Key(1));
        Assert.Equal("Int32:1", new PlainCodec().Encode(1));
        Assert.Equal("Int32", Outer<int>.Inner.Describe());
        Assert.Equal("String", new Outer<string>.Inner().InstanceDescribe());
        Assert.Equal("x", Echo<string>.Identity("x"));
    }
}
```

Also set `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` in all three fixture csprojs (mirror `SampleRecordLib.csproj`).

Wire the fixture into `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` next to the other Content blocks:

```xml
  <ItemGroup>
    <!-- Include SampleGenericDeepenSolution as content so it's available at test runtime -->
    <Content Include="..\SampleGenericDeepenSolution\**\*.*">
      <Link>SampleGenericDeepenSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 3: Project test**

`tests/CSharpToJava.ProjectTests/GenericDeepenProjectTests.cs` — invoke the CLI the same way `PreprocessVerbCLITests` does (reuse its exe-resolution helper; if that file uses in-proc dispatch, mirror that instead). Full-chain variant:

```csharp
using System.Diagnostics;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class GenericDeepenProjectTests
{
    [Fact]
    public async Task Solution_EnableGenericDeepen_RewritesBuildsAndTests()
    {
        var root = FindRepoRoot();
        var fixture = Path.Combine(root, "tests", "SampleGenericDeepenSolution");
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-gd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        var exitCode = await RunCliAsync($"preprocess -s \"{Path.Combine(fixture, "SampleGenericDeepenSolution.sln")}\" -d \"{destination}\" --enable-pass generic-deepen", root);
        Assert.True(exitCode == 0, "preprocess with --enable-pass generic-deepen must succeed");

        var deepenRoot = Path.Combine(destination, ".cs2j-generic-deepen-src");
        Assert.True(Directory.Exists(deepenRoot), "generic-deepen intermediate tree must exist");

        var holderFiles = Directory.GetFiles(deepenRoot, "_Cs2jGenericHolders.*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(holderFiles);

        var rewrittenLib = Directory.GetFiles(deepenRoot, "GenericSamples.cs", SearchOption.AllDirectories).Single();
        var libText = await File.ReadAllTextAsync(rewrittenLib);
        Assert.Contains("__cs2j_token_", libText);
        Assert.Contains("__Cs2jGenericHolder_Latest", Directory.GetFiles(deepenRoot, "StaticSamples.cs", SearchOption.AllDirectories).Select(File.ReadAllText).Single());
        Assert.DoesNotContain("public static T? Value;", libText is { } _ ? libText : string.Empty);

        var libCsproj = Directory.GetFiles(deepenRoot, "SampleGenericDeepenLib.csproj", SearchOption.AllDirectories).Single();
        var csprojText = await File.ReadAllTextAsync(libCsproj);
        Assert.Contains("_Cs2jGenericHolders.SampleGenericDeepenLib.cs", csprojText);
    }
}
```

(Transcribe with the repo's actual CLI-invocation and repo-root helpers — copy them from an existing ProjectTests class such as `CrossProjectGenericClassTokenTests` or the CLI tests; assertions above are the contract. The pass's `VerifyTestsByDefault` makes the runner execute `dotnet test` on the intermediate tree containing `RuntimeBehaviorTests`, which is the semantics proof; a non-zero exit fails the CLI run and thus this test.)

- [ ] **Step 4: Run everything and commit**

Run:
```bash
dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~GenericDeepen"
dotnet test tests/CSharpToJava.ProjectTests -c Release --filter "FullyQualifiedName~GenericDeepenProject"
dotnet test tests/CSharpToJava.ProjectTests -c Release --filter "FullyQualifiedName~CrossProjectGenericClassTokenTests"
```
Expected: first two green; third stays at the 2-failed baseline (no worse).

```bash
git add src tests
git commit -m "feat(preprocessing): generic-deepen residual gate, fixture solution and project-level end-to-end tests"
```

---

### Task 8: Full validation and wrap-up

- [ ] **Step 1: Full repo build + tests**

```bash
dotnet build CSharpToJavaConverter.slnx -c Release
dotnet test tests/CSharpToJava.Tests -c Release
dotnet test tests/CSharpToJava.ProjectTests -c Release
dotnet test tests/CSharpToJava.ConversionTests -c Release
```

Expected: all green except the registered pre-existing failures (`CrossProjectGenericClassTokenTests` ≤ 2 failed). Any NEW failure must be fixed or the offending rule gated before finishing.

- [ ] **Step 2: CLI surface check**

```bash
dotnet run --project src/CSharpToJava.CLI -c Release -- preprocess --list-passes
```

Expected: `generic-deepen | Abi | Implemented | off | .cs2j-generic-deepen-src` between `generic` and `record-readonly`; `closed-metadata`/`variance-audit` stubs listed.

- [ ] **Step 3 (optional, time permitting): sdk measurement round**

```bash
dotnet run --project src/CSharpToJava.CLI -c Release -- preprocess -s D:\csharp-sdk -d %TEMP%\cs2j-sdk-gd --enable-pass generic-deepen
```

Record the E10 counters (`TypeOf/Is/As`) from the `generic` summary plus this pass's summary; `D:\csharp-sdk` is read-only input. If the run is too slow for the session, record that the measurement round is pending — it gates only the default-on flip (M5), not this delivery.

- [ ] **Step 4: Final commit + status note**

```bash
git add -A
git commit -m "test(preprocessing): generic-deepen full validation pass"
```

Then report: milestones delivered (M0–M4), default stays OFF (M5 gated on spec §8: sdk triage, converter smoke of anchor consumption, docs finalization), and the converter-side follow-up list from spec §9 M5 (L2 prototype, FieldTransformer fail-fast removal, E10 sentinel retirement, CrossProjectGenericClassTokenTests repair).

---

## Self-Review (per writing-plans skill)

1. **Spec coverage check:**
   - §2 decision table: anchors (Tasks 3), is/as keep (Task 3 tests), pattern lowering (Task 4), catch lowering (Task 4), static fields F1/F2 (Task 5), GD-STATIC (Task 6, v1-narrowed per spec §13 amendment: same-name shadowing, no family surgery), nested propagation (Task 3 forwarding + Task 6 cascade), expression trees (Task 1 shared facts + Task 5 usage exclusion + Task 7 gate), support library = none (Task 2 descriptor), E10 relationship (Task 3 dedup + helper delegation).
   - §3 registry contract: Task 2 (descriptor, stubs, C30/C33, tests).
   - §4 Core components: all files created across Tasks 1, 3, 4, 5, 6, 7 (`GenericDeepenOptions/Diagnostics/Statistics/Result/SyntaxFacts/Analyzer/Plan(inline in Analyzer file)/Rewriter/HolderGenerator/ResidualScanner/Preprocessor`, `Analysis/ExpressionTreeFacts`).
   - §5 CLI adapter: Tasks 2 and 7 (AfterFilesAsync, verify, CS2J1130/1131).
   - §6 diagnostics: codes defined in Task 1; 1100 (whole-tree rollback) is delegated to the runner's standard rollback spine — the adapter reports build failure as CS2J1130 and the chain reverts, matching the foundation contract; 1190 single-file limitation: the standalone verb always operates on whole directories via the chain, and unit tests exercise single trees directly, so 1190 is reserved (documented, no false promise).
   - §7 tests: Core (Tasks 3–6), fixture (Task 7), registry/CLI (Tasks 2, 8), residual gate (Task 7), CrossProject baseline (Task 7 Step 4, Task 8).
   - §8 default-on gate: NOT flipped — by design (Task 8 Step 4).
   - §9 M0–M4 mapped; M5+ out of scope.
2. **Placeholder scan:** the scratch-parse trick in Task 6 and the `.Replace` readability trick in Task 5 are explicitly called out as transcription notes, not deferred work. The CLI-invocation helper reuse in Task 7 Step 3 is a concrete pointer to existing helpers (the repo already has them; copying is mechanical).
3. **Type consistency:** `GenericDeepenParamRef`/`TokenAnchorPlan` defined in Task 3 and reused in Tasks 4–6 plans; `GenericDeepenFileResult` fields match adapter usage; `StaticFieldPlan` fields match generator consumption; `PassIds.GenericDeepen` consistent across Tasks 2 and 8.


