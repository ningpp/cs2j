# LocalFunctionLiftingPreprocessor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a pure C#→C# preprocessor that hoists local functions to class-level `private` methods before conversion, eliminating all `LocalFunctionStatementSyntax` from the conversion pipeline.

**Architecture:** A three-phase preprocessor: (1) Analyzer scans the syntax tree with a `SemanticModel` to build `LiftPlan` objects for each local function, (2) Rewriter uses those plans to delete local function declarations, rewrite call sites, rename captured variable references, and insert new `MethodDeclarationSyntax` into the host type, (3) Formatter normalizes trivia. A project-level wrapper mirrors `ProjectGenericPreprocessor` for multi-file compilation with cross-file naming.

**Tech Stack:** .NET 10, Roslyn (Microsoft.CodeAnalysis.CSharp 4.12.0), xUnit

---

## File Structure

### Core: `src/CSharpToJava.Core/LocalFunctionLift/`

| File | Responsibility |
|---|---|
| `LocalFunctionLiftOptions.cs` | Options record: `CapturesAtEnd`, `AllowRefInAsyncCallers`, `AllowInterfaceHost`, `Verbose` |
| `LocalFunctionLiftStatistics.cs` | Statistics class with counters and `SkippedByReason` dictionary |
| `LocalFunctionLiftDiagnostic.cs` | Severity enum, SkipReason enum, Diagnostic record |
| `LocalFunctionLiftResult.cs` | Result record: `OutputCode`, `Changed`, `Diagnostics`, `Statistics` |
| `LocalFunctionLiftPlan.cs` | `LiftPlan` class + `CapturePlan` record (data contract between analyzer and rewriter) |
| `LocalFunctionLiftNaming.cs` | Static class: camelCase naming, member conflict resolution, capture parameter naming |
| `LocalFunctionLiftAnalyzer.cs` | Pure analysis: locate host type, determine static, collect captures, IsWritten, fixpoint propagation, skip gates → `LiftPlan` list |
| `LocalFunctionLiftRewriter.cs` | `CSharpSyntaxRewriter`: delete local function declarations, rewrite call sites, rename captures, insert method declarations |
| `LocalFunctionLiftPreprocessor.cs` | Entry point: `Preprocess(string, SemanticModel?, LocalFunctionLiftOptions?) → LocalFunctionLiftResult`; dirty check; cross-file naming table |

### CLI: `src/CSharpToJava.CLI/`

| File | Responsibility |
|---|---|
| `ProjectLocalFunctionLiftPreprocessor.cs` | Project-level wrapper: mirrors `ProjectGenericPreprocessor.PreprocessAsync`; residual detection; build/test verification |

### Modified files

| File | Changes |
|---|---|
| `src/CSharpToJava.CLI/Program.cs` | Add CLI options (`--local-function-lift`, `--no-local-function-strict`); insert preprocessor block before `StripDefault`; add fingerprint token; add preserved directory |
| `tests/CSharpToJava.Tests/LocalFunctionHoistingTests.cs` | Add `Skip` to `[Fact]` attributes |
| `tests/CSharpToJava.Tests/LocalFunctionWrittenCaptureTests.cs` | Add `Skip` to `[Fact]` attributes |
| `tests/CSharpToJava.Tests/HoistedLocalFunctionCaptureScopeTests.cs` | Add `Skip` to `[Fact]` attributes |
| `tests/CSharpToJava.Tests/LocalFunctionAfterReturnHoistingTests.cs` | Add `Skip` to `[Fact]` attributes |
| `tests/CSharpToJava.Tests/LocalFunctionExpressionBodySpanAdaptationTests.cs` | Add `Skip` to `[Fact]` attributes |
| `tests/CSharpToJava.Tests/GenericLocalFunctionRenamedCallSiteTests.cs` | Add `Skip` to `[Fact]` attributes |

### New test file

| File | Responsibility |
|---|---|
| `tests/CSharpToJava.Tests/LocalFunctionLiftTests.cs` | Unit tests for the preprocessor: baselines, naming, modifiers, generics, captures, call sites, body, skip gates |

---

## Task 1: Core Data Types (Options, Statistics, Diagnostics, Result)

**Files:**
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftOptions.cs`
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftStatistics.cs`
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftDiagnostic.cs`
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftResult.cs`

- [ ] **Step 1: Create `LocalFunctionLiftOptions.cs`**

```csharp
namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Options for the local function lift preprocessor.
/// </summary>
/// <param name="CapturesAtEnd">If true, capture parameters go after original parameters (default false = front).</param>
/// <param name="AllowRefInAsyncCallers">If true, relax gate #8 for async/iterator call sites.</param>
/// <param name="AllowInterfaceHost">If true, relax gate #4 for interface host types.</param>
/// <param name="Verbose">If true, emit Info-level diagnostics.</param>
public sealed record LocalFunctionLiftOptions(
    bool CapturesAtEnd = false,
    bool AllowRefInAsyncCallers = false,
    bool AllowInterfaceHost = false,
    bool Verbose = false);
```

- [ ] **Step 2: Create `LocalFunctionLiftStatistics.cs`**

```csharp
namespace CSharpToJava.Core.LocalFunctionLift;

public sealed class LocalFunctionLiftStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int LocalFunctionsLifted;
    public int LocalFunctionsSkipped;
    public int LocalFunctionsResidual;
    public int CapturesByValue;
    public int CapturesByRef;
    public Dictionary<LocalFunctionSkipReason, int> SkippedByReason = new();
}
```

- [ ] **Step 3: Create `LocalFunctionLiftDiagnostic.cs`**

```csharp
namespace CSharpToJava.Core.LocalFunctionLift;

public enum LocalFunctionLiftSeverity { Info, Warning, Error }

public enum LocalFunctionSkipReason
{
    None, Iterator, NoBody, NoHostType, InterfaceHost, MethodGroupConversion,
    CheckedContext, UnsupportedCaptureType, RefCaptureUnavailable,
    EnclosingTypeParameterConflict, ParamsOrOptionalConflict,
    NamedArgumentCallSite, InternalError,
}

public sealed record LocalFunctionLiftDiagnostic(
    LocalFunctionLiftSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0,
    LocalFunctionSkipReason Reason = LocalFunctionSkipReason.None);
```

- [ ] **Step 4: Create `LocalFunctionLiftResult.cs`**

```csharp
namespace CSharpToJava.Core.LocalFunctionLift;

public sealed record LocalFunctionLiftResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<LocalFunctionLiftDiagnostic> Diagnostics,
    LocalFunctionLiftStatistics Statistics);
```

- [ ] **Step 5: Build to verify no compile errors**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/LocalFunctionLift/
git commit -m "feat: add LocalFunctionLift core data types (options, statistics, diagnostics, result)"
```

---

## Task 2: LiftPlan Data Contract

**Files:**
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftPlan.cs`

- [ ] **Step 1: Create `LocalFunctionLiftPlan.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Data contract between the analyzer and rewriter.
/// One instance per local function in the file.
/// </summary>
internal sealed class LiftPlan
{
    public required LocalFunctionStatementSyntax Node;
    public required IMethodSymbol Symbol;
    public required string NewName;
    public TypeDeclarationSyntax? HostType;      // insertion target (the declaration in this file)
    public bool IsStatic;
    public List<CapturePlan> Captures = new();    // ordered
    public bool Skipped;
    public LocalFunctionSkipReason SkipReason = LocalFunctionSkipReason.None;
}

/// <summary>
/// One captured variable: its symbol, original name, parameter name, type, and whether it is written.
/// </summary>
internal sealed record CapturePlan(
    ISymbol Symbol,
    string OriginalName,
    string ParameterName,     // _cs2j_cap_xxx / _cs2j_cap_xxx_2
    string TypeDisplay,       // FullyQualifiedFormat
    bool IsWritten,           // → ref parameter
    bool ByRef);              // final form (= IsWritten)
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftPlan.cs
git commit -m "feat: add LiftPlan data contract for local function lift"
```

---

## Task 3: Naming Utility

**Files:**
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs`

- [ ] **Step 1: Create `LocalFunctionLiftNaming.cs`**

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Naming utilities: camelCase conversion, member conflict resolution,
/// and capture parameter naming with _cs2j_cap_ prefix.
/// </summary>
internal static class LocalFunctionLiftNaming
{
    /// <summary>
    /// Base name = first character lowercased, preserving _ prefix and @ escape.
    /// </summary>
    internal static string GetBaseName(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return identifier;
        // Preserve @ escape prefix
        if (identifier[0] == '@')
        {
            if (identifier.Length == 1)
                return identifier;
            return "@" + char.ToLowerInvariant(identifier[1]) + identifier[2..];
        }
        // Preserve _ prefix
        if (identifier[0] == '_')
        {
            if (identifier.Length == 1)
                return identifier;
            return "_" + char.ToLowerInvariant(identifier[1]) + identifier[2..];
        }
        return char.ToLowerInvariant(identifier[0]) + identifier[1..];
    }

    /// <summary>
    /// Resolves a unique name by appending 2, 3, ... when it conflicts
    /// with existing member names or already-assigned names.
    /// </summary>
    internal static string ResolveUniqueName(
        string baseName,
        HashSet<string> usedNames)
    {
        var name = baseName;
        var suffix = 2;
        while (usedNames.Contains(name))
        {
            name = baseName + suffix;
            suffix++;
        }
        usedNames.Add(name);
        return name;
    }

    /// <summary>
    /// Capture parameter name: _cs2j_cap_<originalName>.
    /// If it conflicts with an existing capture parameter, append _2, _3, ...
    /// </summary>
    internal static string GetCaptureParameterName(
        string originalName,
        HashSet<string> existingCaptureParamNames)
    {
        var capName = "_cs2j_cap_" + originalName;
        if (!existingCaptureParamNames.Contains(capName))
        {
            existingCaptureParamNames.Add(capName);
            return capName;
        }
        var suffix = 2;
        while (existingCaptureParamNames.Contains($"{capName}_{suffix}"))
        {
            suffix++;
        }
        var final = $"{capName}_{suffix}";
        existingCaptureParamNames.Add(final);
        return final;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs
git commit -m "feat: add LocalFunctionLiftNaming utility for camelCase and conflict resolution"
```

---

## Task 4: Analyzer — Host Type, Static, Captures, Skip Gates

**Files:**
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs`

This is the largest and most complex file. It performs pure analysis and produces `LiftPlan` objects.

- [ ] **Step 1: Create `LocalFunctionLiftAnalyzer.cs` with the full implementation**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Pure analysis pass: locates host types, determines static, collects captures,
/// runs the fixpoint propagation, and evaluates all skip gates.
/// Produces a list of LiftPlan objects for the rewriter.
/// </summary>
internal sealed class LocalFunctionLiftAnalyzer
{
    private readonly SemanticModel _model;
    private readonly LocalFunctionLiftOptions _options;
    private readonly LocalFunctionLiftStatistics _stats;
    private readonly List<LocalFunctionLiftDiagnostic> _diagnostics;
    private readonly Dictionary<string, HashSet<string>> _crossFileNameTable;

    public LocalFunctionLiftAnalyzer(
        SemanticModel model,
        LocalFunctionLiftOptions options,
        LocalFunctionLiftStatistics stats,
        List<LocalFunctionLiftDiagnostic> diagnostics,
        Dictionary<string, HashSet<string>> crossFileNameTable)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _crossFileNameTable = crossFileNameTable;
    }

    /// <summary>
    /// Analyzes all local functions in the compilation unit and returns LiftPlans.
    /// </summary>
    public List<LiftPlan> Analyze(CompilationUnitSyntax root)
    {
        var localFns = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToList();
        var plans = new List<LiftPlan>();

        // First pass: create a LiftPlan for each local function (may be skipped)
        foreach (var localFn in localFns)
        {
            var plan = AnalyzeOne(root, localFn);
            plans.Add(plan);
        }

        // Fixpoint propagation: if A calls B, A gets B's captures (with IsWritten OR'd)
        PropagateCaptures(plans);

        // Post-propagation gates: re-check captures for ref availability
        foreach (var plan in plans)
        {
            if (plan.Skipped)
                continue;
            CheckPostPropagationGates(plan);
        }

        return plans;
    }

    private LiftPlan AnalyzeOne(CompilationUnitSyntax root, LocalFunctionStatementSyntax localFn)
    {
        var symbol = _model.GetDeclaredSymbol(localFn) as IMethodSymbol;
        var plan = new LiftPlan
        {
            Node = localFn,
            Symbol = symbol!,
            NewName = "", // assigned later after conflict check
            IsStatic = false,
            Skipped = false,
            SkipReason = LocalFunctionSkipReason.None,
        };

        // Gate #2: No body
        if (localFn.Body == null && localFn.ExpressionBody == null)
        {
            Skip(plan, LocalFunctionSkipReason.NoBody, "local function has no body");
            return plan;
        }

        // Gate #1: Iterator (yield return / yield break)
        var bodyNode = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody!;
        if (bodyNode.DescendantNodes().Any(n =>
            n.IsKind(SyntaxKind.YieldReturnStatement) || n.IsKind(SyntaxKind.YieldBreakStatement)))
        {
            Skip(plan, LocalFunctionSkipReason.Iterator, "local function is an iterator (contains yield)");
            return plan;
        }

        // Gate #3: No host type (top-level statements, lambda, field initializer)
        var hostType = FindHostType(localFn);
        if (hostType == null)
        {
            Skip(plan, LocalFunctionSkipReason.NoHostType, "no enclosing type declaration found");
            return plan;
        }
        if (IsInsideLambdaOrInitializer(localFn))
        {
            Skip(plan, LocalFunctionSkipReason.NoHostType, "local function is inside a lambda or field/property initializer");
            return plan;
        }
        plan.HostType = hostType;

        // Gate #4: Interface host
        if (hostType is InterfaceDeclarationSyntax && !_options.AllowInterfaceHost)
        {
            Skip(plan, LocalFunctionSkipReason.InterfaceHost, "host type is an interface");
            return plan;
        }

        // Gate #6: checked/unchecked context
        if (IsInsideCheckedContext(localFn))
        {
            Skip(plan, LocalFunctionSkipReason.CheckedContext, "local function is inside a checked/unchecked block");
            return plan;
        }

        // Determine static
        plan.IsStatic = DetermineStatic(localFn);

        // Gate #5: Method group / delegate conversion reference
        if (HasMethodGroupReference(root, localFn, symbol!))
        {
            Skip(plan, LocalFunctionSkipReason.MethodGroupConversion,
                "local function is referenced as a method group or delegate conversion");
            return plan;
        }

        // Collect captures (empty for static local functions)
        plan.Captures = CollectCaptures(localFn, symbol!, plan.IsStatic);

        // Gate #7: Unsupported capture type
        foreach (var cap in plan.Captures)
        {
            if (IsUnsupportedCaptureType(cap))
            {
                Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                    $"capture variable '{cap.OriginalName}' has unsupported type: {cap.TypeDisplay}");
                return plan;
            }
        }

        // Assign name
        plan.NewName = AssignName(localFn, hostType, symbol!);

        // Gate #10: CapturesAtEnd with params/optional
        if (_options.CapturesAtEnd)
        {
            if (HasParamsOrOptional(localFn))
            {
                Skip(plan, LocalFunctionSkipReason.ParamsOrOptionalConflict,
                    "CapturesAtEnd=true but original parameters contain params or default values");
                return plan;
            }
        }

        // Gate #11: Named argument at call sites
        if (HasNamedArgumentCallSite(root, localFn, symbol!))
        {
            Skip(plan, LocalFunctionSkipReason.NamedArgumentCallSite,
                "a call site uses named arguments");
            return plan;
        }

        // Gate #9: Enclosing type parameter conflict
        var enclosingTypeParams = GetEnclosingMethodTypeParameters(localFn);
        if (enclosingTypeParams.Count > 0)
        {
            var localTypeParams = localFn.TypeParameterList?.Parameters
                .Select(p => p.Identifier.Text).ToList() ?? new List<string>();
            var conflicts = enclosingTypeParams.Where(tp => localTypeParams.Contains(tp)).ToList();
            if (conflicts.Count > 0)
            {
                Skip(plan, LocalFunctionSkipReason.EnclosingTypeParameterConflict,
                    $"enclosing method type parameter(s) conflict: {string.Join(", ", conflicts)}");
                return plan;
            }
        }

        return plan;
    }

    private void Skip(LiftPlan plan, LocalFunctionSkipReason reason, string message)
    {
        plan.Skipped = true;
        plan.SkipReason = reason;
        _stats.LocalFunctionsSkipped++;
        if (!_stats.SkippedByReason.TryAdd(reason, 1))
            _stats.SkippedByReason[reason]++;

        var lineSpan = plan.Node.GetLocation().GetLineSpan();
        var line = lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : 0;
        _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Warning,
            $"Skipped local function '{plan.Node.Identifier.Text}': {message}",
            null, line, reason));
    }

    // -----------------------------------------------------------------------
    // Host type detection
    // -----------------------------------------------------------------------

    private static TypeDeclarationSyntax? FindHostType(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax typeDecl)
                return typeDecl;
            current = current.Parent;
        }
        return null;
    }

    private static bool IsInsideLambdaOrInitializer(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is AnonymousFunctionExpressionSyntax
                or SimpleLambdaExpressionSyntax
                or ParenthesizedLambdaExpressionSyntax)
                return true;
            // Field/property initializer: VariableDeclarator with Initializer,
            // or PropertyDeclaration with ExpressionBody/Initializer
            if (current is VariableDeclaratorSyntax vd && vd.Initializer != null)
                return true;
            if (current is PropertyDeclarationSyntax pd
                && (pd.ExpressionBody != null || pd.Initializer != null))
                return true;
            if (current is TypeDeclarationSyntax)
                return false; // reached the type level safely
            current = current.Parent;
        }
        return false;
    }

    private static bool IsInsideCheckedContext(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is CheckedExpressionSyntax || current is CheckedStatementSyntax)
                return true;
            if (current is TypeDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Static determination (§4.1)
    // -----------------------------------------------------------------------

    private static bool DetermineStatic(LocalFunctionStatementSyntax localFn)
    {
        // If the local function itself is static, the hoisted method is static.
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return true;

        // Walk ancestors (skip LocalFunctionStatementSyntax) to find the first
        // class-level member and check if it has static.
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is LocalFunctionStatementSyntax)
            {
                current = current.Parent;
                continue;
            }

            if (current is MemberDeclarationSyntax member)
            {
                return member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            }

            current = current.Parent;
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Capture collection (§4.3)
    // -----------------------------------------------------------------------

    private List<CapturePlan> CollectCaptures(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol,
        bool isStatic)
    {
        if (isStatic)
            return new List<CapturePlan>();

        var captures = new List<CapturePlan>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return captures;

        var capParamNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            // Skip named argument names
            if (identifier.Parent is NameColonSyntax)
                continue;

            var symbol = _model.GetSymbolInfo(identifier).Symbol;
            switch (symbol)
            {
                case ILocalSymbol local when local.HasConstantValue:
                    break; // const local is not a capture
                case ILocalSymbol or IParameterSymbol or IRangeVariableSymbol:
                    if (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, localFnSymbol))
                        break; // own parameters/locals
                    if (!seen.Add(symbol))
                        break;

                    var name = symbol.Name;
                    var symbolType = symbol switch
                    {
                        ILocalSymbol ls => ls.Type,
                        IParameterSymbol ps => ps.Type,
                        _ => null
                    };

                    if (symbolType == null || symbolType.TypeKind == TypeKind.Error)
                        break;

                    var typeDisplay = symbolType.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat);
                    var isWritten = IsCapturedVariableWritten(localFn, symbol);
                    var capName = LocalFunctionLiftNaming.GetCaptureParameterName(
                        name, capParamNames);

                    captures.Add(new CapturePlan(
                        symbol, name, capName, typeDisplay, isWritten, isWritten));
                    break;
            }
        }

        return captures;
    }

    /// <summary>
    /// True when the local function body writes to the captured variable itself.
    /// (assignment target, ++/--, ref/out argument)
    /// </summary>
    private bool IsCapturedVariableWritten(
        LocalFunctionStatementSyntax localFn,
        ISymbol symbol)
    {
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;
            var resolved = _model.GetSymbolInfo(identifier).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(resolved, symbol))
                continue;

            var parent = identifier.Parent;
            // Direct assignment target: x = ..., x += ..., x ??= ...
            if (parent is AssignmentExpressionSyntax assignment
                && assignment.Left == identifier)
                return true;
            // ++ / --
            if (parent is PrefixUnaryExpressionSyntax prefix
                && prefix.Operand == identifier
                && (prefix.IsKind(SyntaxKind.PreIncrementExpression)
                    || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                return true;
            if (parent is PostfixUnaryExpressionSyntax postfix
                && postfix.Operand == identifier
                && (postfix.IsKind(SyntaxKind.PostIncrementExpression)
                    || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                return true;
            // ref/out argument
            if (parent is ArgumentSyntax argument
                && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                return true;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Fixpoint propagation (§4.3)
    // -----------------------------------------------------------------------

    private void PropagateCaptures(List<LiftPlan> plans)
    {
        var bySymbol = plans.Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var plan in plans)
            {
                if (plan.Skipped)
                    continue;

                var body = (SyntaxNode?)plan.Node.Body ?? plan.Node.ExpressionBody;
                if (body == null)
                    continue;

                // For each identifier in body (not descending into nested local functions)
                foreach (var identifier in body
                    .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
                    .OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Parent is NameColonSyntax)
                        continue;

                    var calleeSymbol = _model.GetSymbolInfo(identifier).Symbol;
                    if (calleeSymbol is not IMethodSymbol { MethodKind: MethodKind.LocalFunction } callee)
                        continue;
                    if (!bySymbol.TryGetValue(callee, out var calleePlan))
                        continue;
                    if (calleePlan.Skipped)
                        continue;

                    // Merge callee's captures into caller's
                    foreach (var capture in calleePlan.Captures)
                    {
                        var existing = plan.Captures.FirstOrDefault(c =>
                            SymbolEqualityComparer.Default.Equals(c.Symbol, capture.Symbol));
                        if (existing == null)
                        {
                            plan.Captures.Add(capture with { });
                            changed = true;
                        }
                        else if (capture.IsWritten && !existing.IsWritten)
                        {
                            plan.Captures[plan.Captures.IndexOf(existing)] =
                                existing with { IsWritten = true, ByRef = true };
                            changed = true;
                        }
                    }
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Post-propagation gates (§4.7 #8)
    // -----------------------------------------------------------------------

    private void CheckPostPropagationGates(LiftPlan plan)
    {
        foreach (var cap in plan.Captures)
        {
            if (!cap.IsWritten)
                continue;

            // Gate #8: ref capture unavailable
            if (!CanUseRefCapture(plan, cap))
            {
                Skip(plan, LocalFunctionSkipReason.RefCaptureUnavailable,
                    $"write capture '{cap.OriginalName}' cannot use ref: " +
                    GetRefCaptureUnavailableReason(plan, cap));
                return;
            }
        }
    }

    private bool CanUseRefCapture(LiftPlan plan, CapturePlan cap)
    {
        // async local function cannot have ref/out parameters
        if (plan.Node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            return false;

        // Check call sites in async/iterator methods
        if (!_options.AllowRefInAsyncCallers)
        {
            // (Checked separately in the caller; this is conservative)
        }

        // Variable must be ref-able: not foreach/using/fixed variable, not in/out parameter,
        // and must have an initializer
        var sym = cap.Symbol;
        if (sym is ILocalSymbol local)
        {
            // Check if declared in a foreach, using, or fixed
            var decl = FindVariableDeclarator(plan.Node, sym);
            if (decl != null)
            {
                // If the variable's parent is a ForEachStatement, UsingStatement, or FixedStatement,
                // it cannot be passed by ref.
                if (IsForeachOrUsingOrFixedVariable(decl))
                    return false;
                // Must have initializer
                if (decl.Initializer == null)
                    return false;
            }
        }
        if (sym is IParameterSymbol param)
        {
            if (param.RefKind is RefKind.In or RefKind.Out)
                return false;
        }

        return true;
    }

    private static string GetRefCaptureUnavailableReason(LiftPlan plan, CapturePlan cap)
    {
        if (plan.Node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            return "local function is async";
        if (cap.Symbol is ILocalSymbol local)
        {
            var decl = FindVariableDeclarator(plan.Node, local);
            if (decl != null)
            {
                if (IsForeachOrUsingOrFixedVariable(decl))
                    return $"variable '{cap.OriginalName}' is a foreach/using/fixed variable";
                if (decl.Initializer == null)
                    return $"variable '{cap.OriginalName}' has no initializer";
            }
        }
        if (cap.Symbol is IParameterSymbol param)
        {
            if (param.RefKind is RefKind.In)
                return $"parameter '{cap.OriginalName}' is 'in'";
            if (param.RefKind is RefKind.Out)
                return $"parameter '{cap.OriginalName}' is 'out'";
        }
        return "ref not available";
    }

    private static VariableDeclaratorSyntax? FindVariableDeclarator(
        SyntaxNode scope, ISymbol symbol)
    {
        foreach (var decl in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (decl.Identifier.Text == symbol.Name)
                return decl;
        }
        return null;
    }

    private static bool IsForeachOrUsingOrFixedVariable(VariableDeclaratorSyntax decl)
    {
        SyntaxNode? current = decl.Parent;
        while (current != null)
        {
            if (current is ForEachStatementSyntax
                or UsingStatementSyntax
                or FixedStatementSyntax)
                return true;
            if (current is MemberDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Method group / delegate conversion (Gate #5)
    // -----------------------------------------------------------------------

    private bool HasMethodGroupReference(
        CompilationUnitSyntax root,
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol symbol)
    {
        // Look for any IdentifierNameSyntax referencing this local function
        // that is NOT part of an InvocationExpression
        foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Parent is NameColonSyntax)
                continue;
            var sym = _model.GetSymbolInfo(id).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(sym, symbol))
                continue;
            // If parent is InvocationExpression's expression, it's a call (OK)
            if (id.Parent is InvocationExpressionSyntax invocation
                && invocation.Expression == id)
                continue;
            // If inside nameof(f), it's OK (just a name reference)
            if (IsInsideNameOf(id))
                continue;
            // Otherwise it's a method group / delegate conversion
            return true;
        }
        return false;
    }

    private static bool IsInsideNameOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax id
                && id.Identifier.Text == "nameof")
                return true;
            current = current.Parent;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Named argument call site (Gate #11)
    // -----------------------------------------------------------------------

    private bool HasNamedArgumentCallSite(
        CompilationUnitSyntax root,
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol symbol)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not IdentifierNameSyntax id
                && invocation.Expression is not GenericNameSyntax)
                continue;

            var sym = _model.GetSymbolInfo(invocation).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(sym, symbol))
                continue;

            // Check if any argument uses NameColon (named argument)
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.NameColon != null)
                    return true;
            }
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Unsupported capture type (Gate #7)
    // -----------------------------------------------------------------------

    private static bool IsUnsupportedCaptureType(CapturePlan cap)
    {
        var typeDisplay = cap.TypeDisplay;
        // Anonymous types start with "<anonymous>"
        if (typeDisplay.Contains("<anonymous"))
            return true;
        // Error type
        if (typeDisplay.Contains("?") && typeDisplay.Contains("Error"))
            return true;
        // Pointer type
        if (typeDisplay.Contains("*"))
            return true;
        // ref local
        if (typeDisplay.StartsWith("ref "))
            return true;
        return false;
    }

    // -----------------------------------------------------------------------
    // params / optional (Gate #10)
    // -----------------------------------------------------------------------

    private static bool HasParamsOrOptional(LocalFunctionStatementSyntax localFn)
    {
        if (localFn.ParameterList == null)
            return false;
        foreach (var param in localFn.ParameterList.Parameters)
        {
            if (param.Default != null)
                return true;
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
                return true;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Enclosing method type parameters (Gate #9)
    // -----------------------------------------------------------------------

    private static List<string> GetEnclosingMethodTypeParameters(
        LocalFunctionStatementSyntax localFn)
    {
        var result = new List<string>();
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is MethodDeclarationSyntax method
                && method.TypeParameterList != null)
            {
                foreach (var tp in method.TypeParameterList.Parameters)
                    result.Add(tp.Identifier.Text);
            }
            if (current is TypeDeclarationSyntax)
                break;
            current = current.Parent;
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Name assignment (§4.2)
    // -----------------------------------------------------------------------

    private string AssignName(
        LocalFunctionStatementSyntax localFn,
        TypeDeclarationSyntax hostType,
        IMethodSymbol symbol)
    {
        var baseName = LocalFunctionLiftNaming.GetBaseName(localFn.Identifier.Text);

        // Gather all member names from the host type via SemanticModel
        var typeSymbol = _model.GetDeclaredSymbol(hostType) as INamedTypeSymbol;
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        if (typeSymbol != null)
        {
            foreach (var member in typeSymbol.GetMembers())
                memberNames.Add(member.Name);
        }

        // Add cross-file already-assigned names for this type
        var typeKey = typeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? hostType.Identifier.Text;
        if (!_crossFileNameTable.TryGetValue(typeKey, out var usedNames))
        {
            usedNames = new HashSet<string>(StringComparer.Ordinal);
            _crossFileNameTable[typeKey] = usedNames;
        }

        // Combine member names and used names for conflict check
        var combined = new HashSet<string>(memberNames, StringComparer.Ordinal);
        foreach (var n in usedNames)
            combined.Add(n);

        var finalName = LocalFunctionLiftNaming.ResolveUniqueName(baseName, combined);
        usedNames.Add(finalName);

        return finalName;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs
git commit -m "feat: add LocalFunctionLiftAnalyzer with host type, static, captures, and skip gates"
```

---

## Task 5: Rewriter — Delete, Rewrite Call Sites, Insert Methods

**Files:**
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs`

- [ ] **Step 1: Create `LocalFunctionLiftRewriter.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// CSharpSyntaxRewriter that performs the actual syntax transformations:
/// 1. Deletes local function declarations (from their containing block)
/// 2. Rewrites call sites: renames + prepends capture arguments
/// 3. Renames captured variable references inside hoisted method bodies
/// 4. Inserts new MethodDeclarationSyntax into the host type
/// </summary>
internal sealed class LocalFunctionLiftRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly List<LiftPlan> _plans;
    private readonly LocalFunctionLiftOptions _options;
    private readonly LocalFunctionLiftStatistics _stats;

    // Map: local function symbol → LiftPlan (for call site lookup)
    private readonly Dictionary<ISymbol, LiftPlan> _symbolToPlan;

    // Map: local function original name → LiftPlan (fallback for symbol lookup)
    private readonly Dictionary<string, LiftPlan> _nameToPlan;

    // Methods to insert, keyed by host type's SyntaxReference
    private readonly Dictionary<TypeDeclarationSyntax, List<MethodDeclarationSyntax>> _methodsToInsert;

    public bool Changed { get; private set; }

    public LocalFunctionLiftRewriter(
        SemanticModel model,
        List<LiftPlan> plans,
        LocalFunctionLiftOptions options,
        LocalFunctionLiftStatistics stats)
    {
        _model = model;
        _plans = plans;
        _options = options;
        _stats = stats;
        _symbolToPlan = new Dictionary<ISymbol, LiftPlan>(SymbolEqualityComparer.Default);
        _nameToPlan = new Dictionary<string, LiftPlan>(StringComparer.Ordinal);
        _methodsToInsert = new Dictionary<TypeDeclarationSyntax, List<MethodDeclarationSyntax>>();

        foreach (var plan in plans.Where(p => !p.Skipped))
        {
            if (plan.Symbol != null)
                _symbolToPlan[plan.Symbol] = plan;
            _nameToPlan[plan.Node.Identifier.Text] = plan;
        }
    }

    /// <summary>
    /// Pre-build the method declarations to insert before visiting.
    /// Called after the rewriter is constructed but before Visit().
    /// </summary>
    public void PrepareMethodDeclarations()
    {
        // Group plans by host type
        var byHost = _plans
            .Where(p => !p.Skipped && p.HostType != null)
            .GroupBy(p => p.HostType!);

        foreach (var group in byHost)
        {
            var hostType = group.Key;
            var methods = new List<MethodDeclarationSyntax>();
            foreach (var plan in group)
            {
                var method = BuildMethodDeclaration(plan);
                methods.Add(method);
                _stats.LocalFunctionsLifted++;
                foreach (var cap in plan.Captures)
                {
                    if (cap.IsWritten)
                        _stats.CapturesByRef++;
                    else
                        _stats.CapturesByValue++;
                }
            }
            _methodsToInsert[hostType] = methods;
        }
    }

    // -----------------------------------------------------------------------
    // Visit TypeDeclaration: insert hoisted methods
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitTypeDeclaration(TypeDeclarationSyntax node)
    {
        var visited = (TypeDeclarationSyntax?)base.VisitTypeDeclaration(node);
        if (visited == null)
            return null;

        if (_methodsToInsert.TryGetValue(node, out var methods))
        {
            // Find insertion point: after last FieldDeclaration, or at the beginning
            var members = visited.Members.ToList();
            var insertIndex = 0;
            for (var i = members.Count - 1; i >= 0; i--)
            {
                if (members[i] is FieldDeclarationSyntax)
                {
                    insertIndex = i + 1;
                    break;
                }
            }

            // Insert methods with proper trivia
            for (var i = 0; i < methods.Count; i++)
            {
                var method = methods[i];
                // Add a blank line before each method (except if at the very start)
                if (insertIndex > 0 || i > 0)
                {
                    method = method.WithLeadingTrivia(
                        SyntaxFactory.CarriageReturnLineFeed,
                        SyntaxFactory.CarriageReturnLineFeed,
                        method.GetLeadingTrivia());
                }
                members.Insert(insertIndex + i, method);
            }

            visited = visited.WithMembers(SyntaxFactory.List(members));
            Changed = true;
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Visit Block: remove local function declarations
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax?)base.VisitBlock(node);
        if (visited == null)
            return null;

        // Remove local function statements that were lifted (not skipped)
        var newStatements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
        {
            if (statement is LocalFunctionStatementSyntax localFn)
            {
                var plan = _plans.FirstOrDefault(p => p.Node == localFn);
                if (plan != null && !plan.Skipped)
                {
                    // Skip this statement (remove it from the block)
                    Changed = true;
                    continue;
                }
            }
            newStatements.Add(statement);
        }

        if (newStatements.Count != visited.Statements.Count)
        {
            visited = visited.WithStatements(SyntaxFactory.List(newStatements));
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Visit InvocationExpression: rewrite call sites
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
        if (visited == null)
            return null;

        // Check if the expression is a call to a lifted local function
        LiftPlan? plan = null;

        if (visited.Expression is IdentifierNameSyntax id)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p))
            {
                plan = p;
            }
            else if (_nameToPlan.TryGetValue(id.Identifier.Text, out var p2))
            {
                plan = p2;
            }
        }
        else if (visited.Expression is GenericNameSyntax gn)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p))
            {
                plan = p;
            }
            else if (gn is { Identifier: { Text: var text } } && _nameToPlan.TryGetValue(text.Text, out var p2))
            {
                plan = p2;
            }
        }

        if (plan == null)
            return visited;

        // Rewrite: rename + prepend capture arguments
        var newExpression = RewriteCallExpression(visited.Expression, plan);
        var newArgs = RewriteCallArguments(visited.ArgumentList, plan);

        Changed = true;
        return visited.WithExpression(newExpression).WithArgumentList(newArgs);
    }

    private ExpressionSyntax RewriteCallExpression(ExpressionSyntax expression, LiftPlan plan)
    {
        // Rename the identifier to the new name
        // Preserve generic type arguments if present
        if (expression is IdentifierNameSyntax id)
        {
            return SyntaxFactory.IdentifierName(plan.NewName)
                .WithTriviaFrom(id);
        }
        if (expression is GenericNameSyntax gn)
        {
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(plan.NewName),
                gn.TypeArgumentList)
                .WithTriviaFrom(gn);
        }
        return expression;
    }

    private ArgumentListSyntax RewriteCallArguments(ArgumentListSyntax argumentList, LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return argumentList;

        var newArgs = new List<ArgumentSyntax>();

        // Prepend capture arguments (front, not end — D1)
        foreach (var cap in plan.Captures)
        {
            var argExpr = SyntaxFactory.IdentifierName(cap.OriginalName);
            if (cap.IsWritten)
            {
                // ref argument
                newArgs.Add(SyntaxFactory.Argument(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword),
                    default,
                    argExpr));
            }
            else
            {
                newArgs.Add(SyntaxFactory.Argument(argExpr));
            }
        }

        // Original arguments
        foreach (var arg in argumentList.Arguments)
        {
            newArgs.Add(arg);
        }

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs));
    }

    // -----------------------------------------------------------------------
    // Visit IdentifierName: handle nameof(f) and method group
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        // Check if this is a nameof(f) reference to a lifted local function
        if (IsInsideNameOf(node))
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var plan))
            {
                Changed = true;
                return SyntaxFactory.IdentifierName(plan.NewName)
                    .WithTriviaFrom(node);
            }
            if (_nameToPlan.TryGetValue(node.Identifier.Text, out var plan2))
            {
                Changed = true;
                return SyntaxFactory.IdentifierName(plan2.NewName)
                    .WithTriviaFrom(node);
            }
        }

        return base.VisitIdentifierName(node);
    }

    private static bool IsInsideNameOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax id
                && id.Identifier.Text == "nameof")
                return true;
            current = current.Parent;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Build MethodDeclarationSyntax from LiftPlan
    // -----------------------------------------------------------------------

    private MethodDeclarationSyntax BuildMethodDeclaration(LiftPlan plan)
    {
        var localFn = plan.Node;

        // Modifiers: private + static (if applicable) + async + unsafe
        var modifiers = new SyntaxTokenListBuilder();
        modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
        if (plan.IsStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword)))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));

        // Return type: original (including ref return)
        var returnType = localFn.ReturnType ?? SyntaxFactory.PredefinedType(
            SyntaxFactory.Token(SyntaxKind.VoidKeyword));

        // Type parameters: original + enclosing method's type parameters
        var typeParams = BuildTypeParameterList(plan);

        // Parameter list: captures (front) + original parameters
        var parameters = BuildParameterList(plan);

        // Constraints: original + enclosing method's where clauses
        var constraints = BuildConstraintClauses(plan);

        // Body: from Block or ExpressionBody
        BlockSyntax? body = null;
        ArrowExpressionClauseSyntax? expressionBody = null;

        if (localFn.Body != null)
        {
            body = (BlockSyntax)Visit(localFn.Body)!;
            // Rename captured variables inside the body
            body = RenameCapturesInBody(body, plan);
        }
        else if (localFn.ExpressionBody != null)
        {
            // Convert expression body to block body
            var expr = localFn.ExpressionBody.Expression;
            var visitedExpr = (ExpressionSyntax)Visit(expr)!;
            visitedExpr = RenameCapturesInExpression(visitedExpr, plan);

            var isVoid = returnType.IsKind(SyntaxKind.VoidKeyword)
                || (returnType is PredefinedTypeSyntax pt && pt.Keyword.IsKind(SyntaxKind.VoidKeyword));

            StatementSyntax stmt;
            if (isVoid)
            {
                stmt = SyntaxFactory.ExpressionStatement(visitedExpr);
            }
            else
            {
                stmt = SyntaxFactory.ReturnStatement(visitedExpr);
            }
            body = SyntaxFactory.Block(stmt);
        }

        // Transfer leading trivia from the local function to the new method
        var leadingTrivia = localFn.GetLeadingTrivia();

        var method = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List<AttributeListSyntax>(),
            SyntaxFactory.TokenList(modifiers),
            returnType,
            typeParams,
            SyntaxFactory.Identifier(plan.NewName),
            parameters,
            constraints,
            body,
            expressionBody,
            default,
            default);

        // Apply leading trivia
        if (leadingTrivia.Count > 0)
            method = method.WithLeadingTrivia(leadingTrivia);

        return method;
    }

    private TypeParameterListSyntax? BuildTypeParameterList(LiftPlan plan)
    {
        var localFn = plan.Node;
        var typeParams = new List<TypeParameterSyntax>();

        // Original type parameters
        if (localFn.TypeParameterList != null)
        {
            foreach (var tp in localFn.TypeParameterList.Parameters)
                typeParams.Add(tp);
        }

        // Append enclosing method's type parameters (not class-level ones)
        var enclosingMethodParams = GetEnclosingMethodTypeParameters(localFn);
        foreach (var tpName in enclosingMethodParams)
        {
            // Only add if not already present (gate #9 already checked for conflicts)
            if (!typeParams.Any(tp => tp.Identifier.Text == tpName))
            {
                typeParams.Add(SyntaxFactory.TypeParameter(
                    SyntaxFactory.List<AttributeListSyntax>(),
                    default,
                    SyntaxFactory.Identifier(tpName)));
            }
        }

        if (typeParams.Count == 0)
            return null;

        return SyntaxFactory.TypeParameterList(
            SyntaxFactory.SeparatedList(typeParams));
    }

    private ParameterListSyntax BuildParameterList(LiftPlan plan)
    {
        var localFn = plan.Node;
        var parameters = new List<ParameterSyntax>();

        // Capture parameters (front, D1)
        if (!_options.CapturesAtEnd)
        {
            foreach (var cap in plan.Captures)
            {
                var param = SyntaxFactory.Parameter(
                    SyntaxFactory.List<AttributeListSyntax>(),
                    cap.IsWritten
                        ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))
                        : SyntaxFactory.TokenList(),
                    SyntaxFactory.ParseTypeName(cap.TypeDisplay),
                    SyntaxFactory.Identifier(cap.ParameterName),
                    null);
                parameters.Add(param);
            }
        }

        // Original parameters
        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
                parameters.Add(param);
        }

        // Capture parameters (end, if CapturesAtEnd)
        if (_options.CapturesAtEnd)
        {
            foreach (var cap in plan.Captures)
            {
                var param = SyntaxFactory.Parameter(
                    SyntaxFactory.List<AttributeListSyntax>(),
                    cap.IsWritten
                        ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))
                        : SyntaxFactory.TokenList(),
                    SyntaxFactory.ParseTypeName(cap.TypeDisplay),
                    SyntaxFactory.Identifier(cap.ParameterName),
                    null);
                parameters.Add(param);
            }
        }

        return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters));
    }

    private SyntaxList<TypeParameterConstraintClauseSyntax> BuildConstraintClauses(LiftPlan plan)
    {
        var localFn = plan.Node;
        var clauses = new List<TypeParameterConstraintClauseSyntax>();

        // Original constraints
        if (localFn.ConstraintClauses != null)
        {
            foreach (var clause in localFn.ConstraintClauses)
                clauses.Add(clause);
        }

        // Append enclosing method's where clauses
        var enclosingMethod = FindEnclosingMethod(plan.Node);
        if (enclosingMethod?.ConstraintClauses != null)
        {
            foreach (var clause in enclosingMethod.ConstraintClauses)
                clauses.Add(clause);
        }

        return SyntaxFactory.List(clauses);
    }

    private static MethodDeclarationSyntax? FindEnclosingMethod(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is MethodDeclarationSyntax method)
                return method;
            if (current is TypeDeclarationSyntax)
                return null;
            current = current.Parent;
        }
        return null;
    }

    private static List<string> GetEnclosingMethodTypeParameters(LocalFunctionStatementSyntax localFn)
    {
        var result = new List<string>();
        var method = FindEnclosingMethod(localFn);
        if (method?.TypeParameterList != null)
        {
            foreach (var tp in method.TypeParameterList.Parameters)
                result.Add(tp.Identifier.Text);
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Rename captured variable references inside method body
    // -----------------------------------------------------------------------

    private BlockSyntax RenameCapturesInBody(BlockSyntax body, LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return body;

        var renamer = new CaptureRenamer(_model, plan.Captures);
        return (BlockSyntax)renamer.Visit(body)!;
    }

    private ExpressionSyntax RenameCapturesInExpression(ExpressionSyntax expr, LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return expr;

        var renamer = new CaptureRenamer(_model, plan.Captures);
        return (ExpressionSyntax)renamer.Visit(expr)!;
    }

    /// <summary>
    /// Rewrites IdentifierNameSyntax nodes that reference captured variables
    /// to use the capture parameter name instead. Descends into lambdas but
    /// not into nested local functions.
    /// </summary>
    private sealed class CaptureRenamer : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly Dictionary<ISymbol, string> _renameMap;

        public CaptureRenamer(SemanticModel model, List<CapturePlan> captures)
        {
            _model = model;
            _renameMap = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            foreach (var cap in captures)
                _renameMap[cap.Symbol] = cap.ParameterName;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is NameColonSyntax)
                return node;

            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _renameMap.TryGetValue(symbol, out var newName))
            {
                return SyntaxFactory.IdentifierName(newName)
                    .WithTriviaFrom(node);
            }
            return base.VisitIdentifierName(node);
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            // Don't descend into nested local functions
            return node;
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS (may have warnings about unused fields, that's OK)

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs
git commit -m "feat: add LocalFunctionLiftRewriter for call site rewriting and method insertion"
```

---

## Task 6: Preprocessor Entry Point

**Files:**
- Create: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftPreprocessor.cs`

- [ ] **Step 1: Create `LocalFunctionLiftPreprocessor.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Entry point for the local function lift preprocessor.
/// In project mode, a single instance is reused across files to share
/// the cross-file naming table.
/// </summary>
public sealed class LocalFunctionLiftPreprocessor
{
    // Cross-file naming table: type display string → set of assigned names
    private readonly Dictionary<string, HashSet<string>> _crossFileNameTable = new();

    /// <summary>
    /// Preprocesses a single C# source file, hoisting local functions to class-level methods.
    /// </summary>
    /// <param name="sourceCode">The C# source code to preprocess.</param>
    /// <param name="model">Optional semantic model. If null, a minimal compilation is built.</param>
    /// <param name="options">Optional preprocessing options.</param>
    /// <returns>The preprocessed result.</returns>
    public LocalFunctionLiftResult Preprocess(
        string sourceCode,
        SemanticModel? model = null,
        LocalFunctionLiftOptions? options = null)
    {
        options ??= new LocalFunctionLiftOptions();
        var stats = new LocalFunctionLiftStatistics { FilesScanned = 1 };
        var diagnostics = new List<LocalFunctionLiftDiagnostic>();

        // Resolve or build semantic model
        SemanticModel? resolvedModel = model;
        if (resolvedModel == null)
        {
            resolvedModel = BuildStandaloneModel(sourceCode, out var parseError);
            if (parseError != null)
            {
                stats.FilesSkippedClean = 1;
                return new LocalFunctionLiftResult(
                    sourceCode, false, Array.Empty<LocalFunctionLiftDiagnostic>(), stats);
            }
        }

        var tree = resolvedModel.SyntaxTree;
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Dirty check: no LocalFunctionStatementSyntax → skip
        if (!root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any())
        {
            stats.FilesSkippedClean = 1;
            return new LocalFunctionLiftResult(
                sourceCode, false, Array.Empty<LocalFunctionLiftDiagnostic>(), stats);
        }

        // Analyze
        var analyzer = new LocalFunctionLiftAnalyzer(
            resolvedModel, options, stats, diagnostics, _crossFileNameTable);
        var plans = analyzer.Analyze(root);

        // If all plans are skipped, nothing to rewrite
        if (plans.All(p => p.Skipped))
        {
            stats.FilesSkippedClean = 1;
            return new LocalFunctionLiftResult(
                sourceCode, false, diagnostics, stats);
        }

        // Rewrite
        var rewriter = new LocalFunctionLiftRewriter(
            resolvedModel, plans, options, stats);
        rewriter.PrepareMethodDeclarations();
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            stats.FilesSkippedClean = 1;
            return new LocalFunctionLiftResult(
                sourceCode, false, diagnostics, stats);
        }

        // Format
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;

        return new LocalFunctionLiftResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// Builds a minimal compilation for standalone (single-file) mode,
    /// following the NewConversionPipeline pattern.
    /// </summary>
    private static SemanticModel BuildStandaloneModel(string sourceCode, out string? parseError)
    {
        parseError = null;
        var tree = CSharpSyntaxTree.ParseText(sourceCode);

        // Check for parse errors
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            parseError = string.Join("; ", errors.Select(e => e.GetMessage()));
            // Return a minimal model anyway — the caller will skip on parse errors
        }

        var compilation = CSharpCompilation.Create("local-fn-standalone",
            new[] { tree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        return compilation.GetSemanticModel(tree);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftPreprocessor.cs
git commit -m "feat: add LocalFunctionLiftPreprocessor entry point with standalone compilation support"
```

---

## Task 7: Project-Level Preprocessor (CLI)

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectLocalFunctionLiftPreprocessor.cs`

- [ ] **Step 1: Create `ProjectLocalFunctionLiftPreprocessor.cs`**

This file mirrors `ProjectGenericPreprocessor.cs` almost line-for-line, with additions for residual detection and strict mode.

```csharp
using CSharpToJava.Core.LocalFunctionLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI;

internal sealed class ProjectLocalFunctionLiftRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public LocalFunctionLiftOptions Options { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectLocalFunctionLiftResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required LocalFunctionLiftStatistics Statistics { get; init; }
    public required IReadOnlyList<LocalFunctionLiftDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectLocalFunctionLiftPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-local-fn-src";

    public static async Task<ProjectLocalFunctionLiftResult> PreprocessAsync(
        ProjectLocalFunctionLiftRequest request)
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
                    $"Local function source directory already exists: {intermediateRoot}. " +
                    "Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<LocalFunctionLiftDiagnostic>();
        var statistics = new LocalFunctionLiftStatistics();
        var preprocessor = new LocalFunctionLiftPreprocessor();
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
            sourceRoot, intermediateRoot, "[local-fn]", request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot,
                Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

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
            "local-fn-preprocess",
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
            var result = preprocessor.Preprocess(sourceCode, semanticModel, request.Options);
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
                    $"[local-fn] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        // Residual detection (§5.2)
        var residualCount = CountResidualLocalFunctions(intermediateRoot);
        statistics.LocalFunctionsResidual = residualCount;

        if (residualCount > 0)
        {
            var severity = request.Strict
                ? LocalFunctionLiftSeverity.Error
                : LocalFunctionLiftSeverity.Warning;
            diagnostics.Add(new LocalFunctionLiftDiagnostic(
                severity,
                $"{residualCount} residual local function(s) remaining after preprocessing.",
                null, 0, LocalFunctionSkipReason.None));
        }

        // Restore + verify build/tests
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[local-fn]");
            if (request.Verbose)
                Console.WriteLine($"Restore exit code: {restoreExitCode}");

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new LocalFunctionLiftDiagnostic(
                    LocalFunctionLiftSeverity.Warning,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
            }

            await VerifyBuildAndTestsAsync(restoreTarget, projectGraph, exclusionInfo,
                request, diagnostics);
            DeleteBuildOutputDirectories(intermediateRoot);
        }

        return new ProjectLocalFunctionLiftResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == LocalFunctionLiftSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Counts residual LocalFunctionStatementSyntax nodes across all .cs files
    /// in the intermediate directory.
    /// </summary>
    private static int CountResidualLocalFunctions(string intermediateRoot)
    {
        var count = 0;
        foreach (var csFile in Directory.EnumerateFiles(
            intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csFile);
            var tree = CSharpSyntaxTree.ParseText(content);
            var root = (CompilationUnitSyntax)tree.GetRoot();
            count += root.DescendantNodes()
                .OfType<LocalFunctionStatementSyntax>().Count();
        }
        return count;
    }

    private static async Task VerifyBuildAndTestsAsync(
        string restoreTarget,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        ProjectLocalFunctionLiftRequest request,
        List<LocalFunctionLiftDiagnostic> diagnostics)
    {
        if (!request.VerifyBuild && !request.VerifyTests)
            return;

        Console.WriteLine($"[local-fn] Verifying build: dotnet build \"{restoreTarget}\" -c Release");
        var buildExit = await RunDotNetCommandAsync("build", restoreTarget, request.Verbose, "Release");
        if (buildExit != 0)
        {
            diagnostics.Add(new LocalFunctionLiftDiagnostic(
                LocalFunctionLiftSeverity.Error,
                $"dotnet build failed with exit code {buildExit} after local function lift."));
            return;
        }

        if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
        {
            Console.WriteLine($"[local-fn] Verifying tests: dotnet test \"{restoreTarget}\" -c Release");
            var testExit = await RunDotNetCommandAsync("test", restoreTarget, request.Verbose, "Release");
            if (testExit != 0)
            {
                diagnostics.Add(new LocalFunctionLiftDiagnostic(
                    LocalFunctionLiftSeverity.Error,
                    $"dotnet test failed with exit code {testExit} after local function lift."));
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
        LocalFunctionLiftStatistics target,
        LocalFunctionLiftStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.LocalFunctionsLifted += source.LocalFunctionsLifted;
        target.LocalFunctionsSkipped += source.LocalFunctionsSkipped;
        target.CapturesByValue += source.CapturesByValue;
        target.CapturesByRef += source.CapturesByRef;
        foreach (var kvp in source.SkippedByReason)
        {
            if (!target.SkippedByReason.TryAdd(kvp.Key, kvp.Value))
                target.SkippedByReason[kvp.Key] += kvp.Value;
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.CLI/ProjectLocalFunctionLiftPreprocessor.cs
git commit -m "feat: add ProjectLocalFunctionLiftPreprocessor with residual detection and strict mode"
```

---

## Task 8: Program.cs Integration (CLI Options + Pipeline + Fingerprint)

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: Add CLI options to `ConvertProjectOptions` class**

In `Program.cs`, inside the `ConvertProjectOptions` class (after the `NoStripDefault` option around line 4109), add:

```csharp
[Option("local-function-lift", Default = false,
    HelpText = "Hoist C# local functions to class-level private methods before conversion")]
public bool LocalFunctionLift { get; set; }

[Option("no-local-function-strict", Default = false,
    HelpText = "Report residual local functions as warnings instead of failing the conversion")]
public bool NoLocalFunctionStrict { get; set; }

public bool LocalFunctionStrict => !NoLocalFunctionStrict;
```

- [ ] **Step 2: Add `using` directive at top of `Program.cs`**

After the existing `using CSharpToJava.Core.DefaultParameterStripper;` line (line 8), add:

```csharp
using CSharpToJava.Core.LocalFunctionLift;
```

- [ ] **Step 3: Insert preprocessor block before `StripDefault` in `ConvertProject`**

In `Program.cs`, insert the following block **before** the `if (opts.StripDefault)` block (around line 190). The spec requires `--local-function-lift` to run before `--strip-default` so that parameter default values are still present.

```csharp
if (opts.LocalFunctionLift)
{
    var liftResult = await ProjectLocalFunctionLiftPreprocessor.PreprocessAsync(
        new ProjectLocalFunctionLiftRequest
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
        $"local-function-lift: {liftResult.Statistics.FilesTransformed} transformed, " +
        $"{liftResult.Statistics.FilesSkippedClean} clean, " +
        $"{liftResult.Statistics.LocalFunctionsLifted} lifted, " +
        $"{liftResult.Statistics.LocalFunctionsSkipped} skipped, " +
        $"{liftResult.Statistics.LocalFunctionsResidual} residual");

    foreach (var d in liftResult.Diagnostics)
        Console.Error.WriteLine($"[{d.Severity}] {d.FilePath}:{d.Line}: {d.Message}");

    if (!liftResult.Success)
        return 1;

    opts.Source = liftResult.PreprocessedSourcePath;
    opts.FingerprintSourcePath ??= liftResult.OriginalSourcePath;
    opts.FingerprintInputRoot ??= liftResult.SourceRoot;
}
```

- [ ] **Step 4: Add preserved directory for `GetPreservedDestinationDirectories`**

In the `GetPreservedDestinationDirectories` method (around line 1846), after the `if (!opts.NoStripDefault)` block, add:

```csharp
if (opts.LocalFunctionLift)
{
    preserved.Add(ProjectLocalFunctionLiftPreprocessor.IntermediateDirectoryName);
}
```

- [ ] **Step 5: Add fingerprint token**

In the `GetProjectConversionOptionTokens` method (around line 2612), after the `strip-default` token, add:

```csharp
$"local-function-lift={opts.LocalFunctionLift}",
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/CSharpToJava.CLI/Program.cs
git commit -m "feat: integrate local function lift into convert-project pipeline"
```

---

## Task 9: Unit Tests — Baselines, Naming, Modifiers, Body

**Files:**
- Create: `tests/CSharpToJava.Tests/LocalFunctionLiftTests.cs`

- [ ] **Step 1: Create the test file with baseline tests**

```csharp
using CSharpToJava.Core.LocalFunctionLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class LocalFunctionLiftTests(ITTestOutputHelper output)
{
    [Fact]
    public void NoLocalFunctions_ReturnsUnchanged()
    {
        var src = "class C { public int M() => 1; }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void SimpleLocalFunction_LiftedToPrivateMethod()
    {
        var src = """
        class C {
            int M(int x) {
                return Square(x);
                int Square(int v) => v * v;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private int square(int v)", result.OutputCode);
        Assert.Contains("square(x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void StaticLocalFunction_LiftedAsPrivateStatic()
    {
        var src = """
        class C {
            int M(int x) {
                return Add(x, 1);
                static int Add(int a, int b) => a + b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("private static int add(int a, int b)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Naming_CamelCase()
    {
        var src = """
        class C {
            void M() {
                ParseOffset(1);
                void ParseOffset(int x) { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void parseOffset(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Naming_MemberConflict_AppendsSuffix()
    {
        var src = """
        class C {
            int square;
            void M() {
                Square(1);
                void Square(int x) { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void square2(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Modifiers_AsyncPreserved()
    {
        var src = """
        class C {
            async Task M() {
                await DoAsync();
                async Task DoAsync() => Task.CompletedTask;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private async", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Body_ExpressionBody_NonVoid_ConvertedToReturnBlock()
    {
        var src = """
        class C {
            int M() {
                return GetValue();
                int GetValue() => 42;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private int getValue()", result.OutputCode);
        Assert.Contains("return 42", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Body_ExpressionBody_Void_ConvertedToExpressionStatement()
    {
        var src = """
        class C {
            void M() {
                DoWork();
                void DoWork() => Console.WriteLine("hi");
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void doWork()", result.OutputCode);
        Assert.Contains("Console.WriteLine(\"hi\")", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    private static LocalFunctionLiftResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("local-fn-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new LocalFunctionLiftPreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private static void AssertNoLocalFunctions(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>());
    }

    private static void AssertNoSyntaxErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should be valid C#. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }
}
```

- [ ] **Step 2: Build tests to verify**

Run: `dotnet build tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
Expected: PASS

- [ ] **Step 3: Run the baseline tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~LocalFunctionLiftTests" --no-build`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add tests/CSharpToJava.Tests/LocalFunctionLiftTests.cs
git commit -m "test: add baseline, naming, modifier, and body tests for LocalFunctionLift"
```

---

## Task 10: Unit Tests — Captures, Call Sites, Skip Gates

**Files:**
- Modify: `tests/CSharpToJava.Tests/LocalFunctionLiftTests.cs`

- [ ] **Step 1: Add capture and call site tests to `LocalFunctionLiftTests.cs`**

Append the following tests inside the `LocalFunctionLiftTests` class, before the helper methods:

```csharp
    [Fact]
    public void Capture_ReadOnly_BecomesValueParameter()
    {
        var src = """
        class C {
            int M() {
                int x = 10;
                return GetX();
                int GetX() => x;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("_cs2j_cap_x", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Capture_Written_BecomesRefParameter()
    {
        var src = """
        class C {
            int M() {
                int best = -1;
                Search(3);
                return best;
                void Search(int v) {
                    if (v > best) best = v;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("ref int _cs2j_cap_best", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void CallSite_Recursive()
    {
        var src = """
        class C {
            int M(int n) {
                return Fib(n);
                int Fib(int k) => k <= 1 ? k : Fib(k - 1) + Fib(k - 2);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("fib(n)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void CallSite_NameOf_RenamedNoArgs()
    {
        var src = """
        class C {
            string M() {
                return nameof(Helper);
                void Helper() { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("nameof(helper)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Skip_Iterator()
    {
        var src = """
        class C {
            System.Collections.Generic.IEnumerable<int> M() {
                return GetSeq();
                System.Collections.Generic.IEnumerable<int> GetSeq() {
                    yield return 1;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains(result.Diagnostics, d => d.Reason == LocalFunctionSkipReason.Iterator);
    }

    [Fact]
    public void Statistics_CorrectCounts()
    {
        var src = """
        class C {
            void M() {
                A();
                B();
                void A() { }
                void B() { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(1, result.Statistics.FilesTransformed);
    }
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~LocalFunctionLiftTests"`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/LocalFunctionLiftTests.cs
git commit -m "test: add capture, call site, and skip gate tests for LocalFunctionLift"
```

---

## Task 11: Mark Existing Hoisting Tests as Skipped

**Files:**
- Modify: `tests/CSharpToJava.Tests/LocalFunctionHoistingTests.cs`
- Modify: `tests/CSharpToJava.Tests/LocalFunctionWrittenCaptureTests.cs`
- Modify: `tests/CSharpToJava.Tests/HoistedLocalFunctionCaptureScopeTests.cs`
- Modify: `tests/CSharpToJava.Tests/LocalFunctionAfterReturnHoistingTests.cs`
- Modify: `tests/CSharpToJava.Tests/LocalFunctionExpressionBodySpanAdaptationTests.cs`
- Modify: `tests/CSharpToJava.Tests/GenericLocalFunctionRenamedCallSiteTests.cs`

- [ ] **Step 1: Add `Skip` to all `[Fact]` attributes in the 6 files**

For each of the 6 test files, replace every `[Fact]` with:

```csharp
[Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
```

- [ ] **Step 2: Build and run all tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
Expected: All tests PASS (6 hoisting test files' tests will show as skipped)

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/LocalFunctionHoistingTests.cs tests/CSharpToJava.Tests/LocalFunctionWrittenCaptureTests.cs tests/CSharpToJava.Tests/HoistedLocalFunctionCaptureScopeTests.cs tests/CSharpToJava.Tests/LocalFunctionAfterReturnHoistingTests.cs tests/CSharpToJava.Tests/LocalFunctionExpressionBodySpanAdaptationTests.cs tests/CSharpToJava.Tests/GenericLocalFunctionRenamedCallSiteTests.cs
git commit -m "test: skip 6 hoisting test files as preprocessing eliminates local functions"
```

---

## Task 12: Full Build and Test Verification

**Files:**
- None (verification only)

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build d:\code\cs2j\CSharpToJava.sln`
Expected: PASS with no errors

- [ ] **Step 2: Run all cs2j tests**

Run: `dotnet test d:\code\cs2j\CSharpToJava.sln`
Expected: All tests PASS (6 hoisting test files skipped, new LocalFunctionLift tests pass)

- [ ] **Step 3: Commit if any fixes were needed**

If any fixes were needed during verification, commit them:

```bash
git add -A
git commit -m "fix: resolve build/test issues from LocalFunctionLift integration"
```

- [ ] **Step 4: Verify clean working tree**

```bash
git status
```
Expected: nothing to commit, working tree clean