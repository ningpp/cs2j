# C03 Pattern Statement Desugar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the `pattern-desugar` statement-side C#→C# preprocessor (pattern switch → if-chains, non-integer governing switch → `==` chains, catch-when/catch-T → guarded catch bodies/merged chains), flip the registry stub to `Implemented`, and update all affected tests per the reviewed spec `docs/superpowers/specs/2026-09-28-c03-pattern-statement-desugar-design.md`.

**Architecture:** An `EnumPreprocessor`-shaped semantic pass: syntax dirty check → per-component plan building (Analyzer role) → top-down `CSharpSyntaxRewriter` (Rewriter role) → Roslyn `Formatter` → semantic residual recheck. A shared `PatternReducer` turns patterns into boolean expressions (the C37 extension point). A CLI `ProjectPassAdapter` wires it into the pass registry; the stub descriptor moves to `PassCatalog.Implemented`.

**Tech Stack:** C# / net10.0, Microsoft.CodeAnalysis (Roslyn) syntax + semantic APIs, xUnit. No new packages.

**Conventions used in this plan:**
- All work happens in the worktree `D:\code\cs2j\.worktrees\preproc-c03-pattern-desugar` (Task 1), branch `feature/preproc-c03-pattern-desugar`. Paths below are relative to that worktree root.
- Shell is Git Bash on Windows. Run all `dotnet` commands from the worktree root.
- "Normalize" in test assertions means `Regex.Replace(text, @"\s+", " ").Trim()`.

---

### Task 1: Worktree setup and baseline verification

**Files:** none created.

- [ ] **Step 1: Create the worktree and branch**

```bash
cd /d/code/cs2j
git worktree add .worktrees/preproc-c03-pattern-desugar -b feature/preproc-c03-pattern-desugar 953293b9
cd .worktrees/preproc-c03-pattern-desugar
```

- [ ] **Step 2: Verify baseline builds**

```bash
dotnet build -c Release
```

Expected: `Build succeeded` (0 errors). If the baseline is red, stop and investigate — do not build on a red base.

- [ ] **Step 3: Confirm the two red test groups exist before changes**

```bash
dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~PatternMatchingTests|FullyQualifiedName~CatchFilterRethrowTests|FullyQualifiedName~SwitchCaseWhenClausePrecedenceTests" 2>&1 | tail -3
```

Expected: `失败: 6，通过: 10` (6 failed / 10 passed).

---

### Task 2: Core scaffolding (Options / Statistics / Diagnostic / Result)

**Files:**
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternDesugarOptions.cs`
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternDesugarStatistics.cs`
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternDesugarDiagnostic.cs`
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternDesugarResult.cs`

- [ ] **Step 1: Write the four scaffolding files**

`src/CSharpToJava.Core/PatternDesugar/PatternDesugarOptions.cs`:

```csharp
namespace CSharpToJava.Core.PatternDesugar;

/// <summary>
/// Options for the pattern statement desugar pass. Strict=true treats
/// unreducible targets (PD010/PD011) and residuals (PD020) as errors;
/// Strict=false downgrades them to warnings (never silently off).
/// </summary>
public sealed record PatternDesugarOptions(
    bool Strict = true,
    bool Verbose = false,
    IReadOnlyList<string>? ResidualWhitelist = null);
```

`src/CSharpToJava.Core/PatternDesugar/PatternDesugarStatistics.cs`:

```csharp
namespace CSharpToJava.Core.PatternDesugar;

/// <summary>Per-file counters, merged by the CLI adapter across the tree.</summary>
public sealed class PatternDesugarStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int FilesDirtyNoRewrite { get; set; }
    public int PatternSwitchesRewritten { get; set; }
    public int NonIntegerSwitchesRewritten { get; set; }
    public int CatchFiltersRewritten { get; set; }
    public int CatchTypeParamRewritten { get; set; }
    public int GoverningTempsIntroduced { get; set; }
    public int EndLabelsIntroduced { get; set; }
    public int UnsupportedSkipped { get; set; }
    public int ResidualCount { get; set; }
}
```

`src/CSharpToJava.Core/PatternDesugar/PatternDesugarDiagnostic.cs`:

```csharp
namespace CSharpToJava.Core.PatternDesugar;

public enum PatternDesugarSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Diagnostic codes: PD001 verbose file status (adapter-printed), PD010
/// unreducible pattern, PD011 goto case/default in a lowered switch,
/// PD020 residual target after rewrite, PD030 restore/build/test failure.
/// </summary>
public static class PatternDesugarCodes
{
    public const string Info = "PD001";
    public const string UnsupportedPattern = "PD010";
    public const string GotoCaseInLoweredSwitch = "PD011";
    public const string ResidualTargets = "PD020";
    public const string RestoreOrBuildFailed = "PD030";
}

public sealed record PatternDesugarDiagnostic(
    string Code,
    PatternDesugarSeverity Severity,
    string Message,
    string? FilePath = null);
```

`src/CSharpToJava.Core/PatternDesugar/PatternDesugarResult.cs`:

```csharp
namespace CSharpToJava.Core.PatternDesugar;

public sealed record PatternDesugarResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<PatternDesugarDiagnostic> Diagnostics,
    PatternDesugarStatistics Statistics);
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core -c Release 2>&1 | tail -3
```

Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/PatternDesugar/
git commit -m "feat(c03): pattern desugar scaffolding (options/stats/diagnostic/result)"
```

---

### Task 3: PatternReducer — patterns to boolean expressions

**Files:**
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternReducer.cs`

- [ ] **Step 1: Write the reducer**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PatternDesugar;

/// <summary>
/// Result of reducing one pattern against one subject expression. Condition is
/// null exactly when Supported is false (explicit PD010 was reported instead —
/// never emit a placeholder/always-true fallback).
/// </summary>
internal sealed record PatternReduction(
    ExpressionSyntax? Condition,
    IReadOnlyList<StatementSyntax> Prologue,
    bool Supported);

/// <summary>Shared state for one reduction sweep (C03 and C37 use the same shape).</summary>
internal sealed record PatternReductionContext(
    SemanticModel? Model,
    PatternDesugarOptions Options,
    List<PatternDesugarDiagnostic> Diagnostics,
    string? FilePath,
    Func<string, string> NewName)
{
    public void Report(string code, PatternDesugarSeverity severity, string message, SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var where = span.IsValid
            ? $" ({FilePath ?? span.Path}:{span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})"
            : "";
        Diagnostics.Add(new PatternDesugarDiagnostic(code, severity, message + where, FilePath));
    }
}

/// <summary>
/// Reduces pattern syntax into plain boolean C# expressions. C03 registers the
/// statement-side kinds; C37 extends the same switch with positional/list
/// reducers. The output form set is deliberately narrow: ==/!=, `is T [v]`,
/// &&/||, !, member access — everything the main converter already accepts.
/// </summary>
internal static class PatternReducer
{
    private static readonly PatternReduction True = new(
        SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
        Array.Empty<StatementSyntax>(), true);

    private static PatternReduction SupportedTrue() => True;

    private static PatternReduction NotSupported() =>
        new(null, Array.Empty<StatementSyntax>(), false);

    private static ExpressionSyntax TrueLiteral() =>
        SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);

    private static ExpressionSyntax NullLiteral() =>
        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

    private static ExpressionSyntax NotNull(ExpressionSyntax subject) =>
        SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression,
            subject.WithoutTrivia(), NullLiteral());

    private static ExpressionSyntax Binary(SyntaxKind kind, ExpressionSyntax left, ExpressionSyntax right) =>
        SyntaxFactory.BinaryExpression(kind, left.WithoutTrivia(), right.WithoutTrivia());

    private static ExpressionSyntax IsPattern(ExpressionSyntax subject, TypeSyntax type, VariableDesignationSyntax? designation)
    {
        PatternSyntax pattern = designation == null
            ? SyntaxFactory.TypePattern(type.WithoutTrivia())
            : SyntaxFactory.DeclarationPattern(type.WithoutTrivia(), designation);
        return SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(), pattern);
    }

    /// <summary>
    /// governing 静态类型为 object/dynamic/类型参数时常量不能直接 ==（装箱引用比较会静默恒假），
    /// 改走 `gov is CT v && v == c`。null 常量不受此限（`== null` 对引用/可空/类型参数都安全）。
    /// </summary>
    private static bool RequiresTypeTest(ITypeSymbol? subjectType)
        => subjectType is { TypeKind: TypeKind.Dynamic or TypeKind.TypeParameter }
           || (subjectType is INamedTypeSymbol named && named.SpecialType == SpecialType.System_Object);

    private static bool IsNullable(ITypeSymbol? subjectType)
        => subjectType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static ITypeSymbol? GetMemberType(ITypeSymbol? type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers(name))
            {
                if (member is IPropertySymbol { Parameters.Length: 0 } property) return property.Type;
                if (member is IFieldSymbol field) return field.Type;
            }
        }
        return null;
    }

    public static PatternReduction Reduce(
        PatternSyntax pattern,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx)
    {
        return pattern switch
        {
            DiscardPatternSyntax => SupportedTrue(),
            VarPatternSyntax varPattern => ReduceVar(varPattern, subject),
            ConstantPatternSyntax constant => ReduceConstant(constant, subject, subjectType, ctx),
            TypePatternSyntax typePattern => new PatternReduction(
                IsPattern(subject, typePattern.Type, null), Array.Empty<StatementSyntax>(), true),
            DeclarationPatternSyntax declaration => new PatternReduction(
                IsPattern(subject, declaration.Type, declaration.Designation), Array.Empty<StatementSyntax>(), true),
            RelationalPatternSyntax relational => ReduceRelational(relational, subject, subjectType, ctx),
            BinaryPatternSyntax binary => ReduceBinary(binary, subject, subjectType, ctx),
            UnaryPatternSyntax unary when unary.OperatorToken.IsKind(SyntaxKind.NotKeyword)
                => ReduceNot(unary, subject, subjectType, ctx),
            ParenthesizedPatternSyntax parenthesized => Reduce(parenthesized.Pattern, subject, subjectType, ctx),
            RecursivePatternSyntax recursive => ReduceRecursive(recursive, subject, subjectType, ctx),
            _ => Unsupported(pattern, ctx),
        };
    }

    private static PatternReduction Unsupported(PatternSyntax pattern, PatternReductionContext ctx)
    {
        ctx.Report(PatternDesugarCodes.UnsupportedPattern,
            ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
            $"PD010: pattern '{pattern.GetType().Name}' cannot be reduced by C03 (positional/list/slice patterns are supported by C37)",
            pattern);
        return NotSupported();
    }

    private static PatternReduction ReduceVar(VarPatternSyntax varPattern, ExpressionSyntax subject)
    {
        return varPattern.Designation switch
        {
            SingleVariableDesignationSyntax single => new PatternReduction(
                SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(),
                    SyntaxFactory.VarPattern(single.WithoutTrivia())),
                Array.Empty<StatementSyntax>(), true),
            _ => SupportedTrue(),
        };
    }

    private static PatternReduction ReduceConstant(
        ConstantPatternSyntax constant,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx)
    {
        if (constant.Expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            if (subjectType is INamedTypeSymbol { SpecialType: not SpecialType.System_Nullable_T } valueType
                && valueType.IsValueType)
            {
                ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                    ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                    "PD010: null pattern on a non-nullable value-type governing is unreachable",
                    constant);
                return NotSupported();
            }
            return new PatternReduction(
                Binary(SyntaxKind.EqualsExpression, subject, NullLiteral()),
                Array.Empty<StatementSyntax>(), true);
        }

        if (RequiresTypeTest(subjectType))
        {
            var constantType = ctx.Model?.GetTypeInfo(constant.Expression).Type;
            if (constantType == null)
            {
                ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                    ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                    "PD010: constant pattern on object/dynamic governing requires a semantic model to pick the type-test form",
                    constant);
                return NotSupported();
            }

            var bound = SyntaxFactory.Identifier(ctx.NewName("__cs2jPat"));
            var typeTest = SyntaxFactory.IsPatternExpression(
                subject.WithoutTrivia(),
                SyntaxFactory.DeclarationPattern(
                    SyntaxFactory.ParseTypeName(constantType.ToDisplayString()),
                    SyntaxFactory.SingleVariableDesignation(bound)));
            var comparison = Binary(SyntaxKind.EqualsExpression,
                SyntaxFactory.IdentifierName(bound), constant.Expression);
            return new PatternReduction(
                SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, typeTest, comparison),
                Array.Empty<StatementSyntax>(), true);
        }

        return new PatternReduction(
            Binary(SyntaxKind.EqualsExpression, subject, constant.Expression),
            Array.Empty<StatementSyntax>(), true);
    }

    private static PatternReduction ReduceRelational(
        RelationalPatternSyntax relational,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx)
    {
        var kind = relational.OperatorToken.Kind() switch
        {
            SyntaxKind.LessThanToken => SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanEqualsToken => SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.GreaterThanToken => SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanEqualsToken => SyntaxKind.GreaterThanOrEqualExpression,
            _ => SyntaxKind.None,
        };
        if (kind == SyntaxKind.None)
            return Unsupported(relational, ctx);

        ExpressionSyntax comparison;
        if (IsNullable(subjectType))
        {
            // Lifted relational produces bool?, which cannot drive an if — guard with
            // HasValue and compare the .Value (mirrors the C# compiler's own lowering).
            var value = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                subject.WithoutTrivia(),
                SyntaxFactory.IdentifierName("Value"));
            comparison = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                NotNull(subject),
                SyntaxFactory.BinaryExpression(kind, value, relational.Expression.WithoutTrivia()));
        }
        else if (RequiresTypeTest(subjectType))
        {
            var operandType = ctx.Model?.GetTypeInfo(relational.Expression).Type;
            if (operandType == null)
                return Unsupported(relational, ctx);
            var bound = SyntaxFactory.Identifier(ctx.NewName("__cs2jPat"));
            var typeTest = SyntaxFactory.IsPatternExpression(
                subject.WithoutTrivia(),
                SyntaxFactory.DeclarationPattern(
                    SyntaxFactory.ParseTypeName(operandType.ToDisplayString()),
                    SyntaxFactory.SingleVariableDesignation(bound)));
            comparison = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                typeTest,
                SyntaxFactory.BinaryExpression(kind,
                    SyntaxFactory.IdentifierName(bound), relational.Expression.WithoutTrivia()));
        }
        else
        {
            comparison = SyntaxFactory.BinaryExpression(kind,
                subject.WithoutTrivia(), relational.Expression.WithoutTrivia());
        }

        return new PatternReduction(comparison, Array.Empty<StatementSyntax>(), true);
    }

    private static PatternReduction ReduceBinary(
        BinaryPatternSyntax binary,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx)
    {
        var kind = binary.IsKind(SyntaxKind.AndPattern)
            ? SyntaxKind.LogicalAndExpression
            : SyntaxKind.LogicalOrExpression;
        var left = Reduce(binary.Left, subject, subjectType, ctx);
        var right = Reduce(binary.Right, subject, subjectType, ctx);
        if (!left.Supported || !right.Supported)
            return NotSupported();
        return new PatternReduction(
            SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.BinaryExpression(kind, left.Condition!, right.Condition!)),
            Array.Empty<StatementSyntax>(), true);
    }

    private static PatternReduction ReduceNot(
        UnaryPatternSyntax unary,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx)
    {
        var inner = Reduce(unary.Pattern, subject, subjectType, ctx);
        if (!inner.Supported)
            return NotSupported();
        return new PatternReduction(
            SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(inner.Condition!)),
            Array.Empty<StatementSyntax>(), true);
    }

    private static PatternReduction ReduceRecursive(
        RecursivePatternSyntax recursive,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx)
    {
        if (recursive.PositionalPatternClause != null)
        {
            ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                "PD010: positional patterns are reduced by C37", recursive);
            return NotSupported();
        }

        var conditions = new List<ExpressionSyntax>();
        ExpressionSyntax baseExpression = subject.WithoutTrivia();

        if (recursive.Type != null)
        {
            conditions.Add(IsPattern(subject, recursive.Type, recursive.Designation));
            if (recursive.Designation is SingleVariableDesignationSyntax userBinding)
            {
                baseExpression = SyntaxFactory.IdentifierName(userBinding.Identifier.WithoutTrivia());
            }
            else if (recursive.PropertyPatternClause != null)
            {
                // No user designation but member access needs a name — bind a synthetic one.
                var synthetic = SyntaxFactory.Identifier(ctx.NewName("__cs2jPat"));
                conditions[0] = IsPattern(subject, recursive.Type,
                    SyntaxFactory.SingleVariableDesignation(synthetic));
                baseExpression = SyntaxFactory.IdentifierName(synthetic);
            }
        }
        else if (recursive.Designation is SingleVariableDesignationSyntax varBinding)
        {
            // Type-less recursive pattern with a designation: bind the subject itself.
            conditions.Add(SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(),
                SyntaxFactory.VarPattern(varBinding.WithoutTrivia())));
            baseExpression = SyntaxFactory.IdentifierName(varBinding.Identifier.WithoutTrivia());
        }
        else if (IsReferenceLike(subjectType))
        {
            conditions.Add(NotNull(subject));
        }

        if (recursive.PropertyPatternClause != null)
        {
            foreach (var sub in recursive.PropertyPatternClause.Subpatterns)
            {
                var memberName = sub.NameColon?.Name.Identifier.ValueText
                    ?? (sub.ExpressionColon?.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
                if (memberName == null)
                {
                    ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                        ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                        "PD010: property subpattern without an explicit member name", sub);
                    return NotSupported();
                }

                var memberType = GetMemberType(subjectType, memberName);
                var memberExpression = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    baseExpression,
                    SyntaxFactory.IdentifierName(memberName));

                var subReduction = Reduce(sub.Pattern, memberExpression, memberType, ctx);
                if (!subReduction.Supported)
                    return NotSupported();

                if (IsReferenceLike(memberType))
                    conditions.Add(NotNull(memberExpression));
                conditions.Add(subReduction.Condition!);
            }
        }

        if (conditions.Count == 0)
            return SupportedTrue();

        var joined = conditions.Aggregate((l, r) =>
            (ExpressionSyntax)SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, l, r));
        return new PatternReduction(
            SyntaxFactory.ParenthesizedExpression(joined), Array.Empty<StatementSyntax>(), true);
    }

    private static bool IsReferenceLike(ITypeSymbol? type)
        => type != null
           && (type.IsReferenceType || type.TypeKind == TypeKind.TypeParameter);
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core -c Release 2>&1 | tail -3
```

Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/PatternDesugar/PatternReducer.cs
git commit -m "feat(c03): shared pattern→boolean reducer with statement-side kinds"
```

---

### Task 4: Components — plan builder, plans, switch/catch components

**Files:**
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternDesugarComponents.cs`

- [ ] **Step 1: Write the components file**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PatternDesugar;

/// <summary>
/// Deterministic per-file name scope: prefix → prefix_0, prefix_1, … Same input
/// always produces the same names, independent of traversal order randomness.
/// Shared by C03 and C37 components to avoid name collisions on merge.
/// </summary>
internal sealed class NameScope
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    public string Next(string prefix)
    {
        var n = _counters.TryGetValue(prefix, out var current) ? current : 0;
        _counters[prefix] = n + 1;
        return $"{prefix}_{n}";
    }
}

/// <summary>Collects lowering plans and explicit (PD010/PD011) node-level failures.</summary>
internal sealed class PatternDesugarPlanBuilder
{
    public PatternDesugarPlanBuilder(PatternDesugarOptions options, string? filePath)
    {
        Options = options;
        FilePath = filePath;
    }

    public PatternDesugarOptions Options { get; }
    public string? FilePath { get; }
    public NameScope Names { get; } = new();
    public Dictionary<SwitchStatementSyntax, SwitchLoweringPlan> SwitchPlans { get; } = new();
    public Dictionary<TryStatementSyntax, CatchLoweringPlan> CatchPlans { get; } = new();
    public List<PatternDesugarDiagnostic> Diagnostics { get; } = new();
    public int UnsupportedSkipped { get; private set; }

    /// <summary>True once any node reported PD010/PD011 — the file's residual recheck is then skipped.</summary>
    public bool HasExplicitNodeErrors { get; private set; }

    public void ReportUnsupported(string code, string message, SyntaxNode node)
    {
        UnsupportedSkipped++;
        HasExplicitNodeErrors = true;
        var severity = code == PatternDesugarCodes.GotoCaseInLoweredSwitch || Options.Strict
            ? PatternDesugarSeverity.Error
            : PatternDesugarSeverity.Warning;
        var span = node.GetLocation().GetLineSpan();
        var where = span.IsValid
            ? $" ({FilePath ?? span.Path}:{span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})"
            : "";
        Diagnostics.Add(new PatternDesugarDiagnostic(code, severity, message + where, FilePath));
    }
}

/// <summary>
/// One statement-side component (C03: switch statements + try/catch; C37 adds
/// is-expressions and switch-expression arms with the same interface).
/// </summary>
internal interface IPatternDesugarComponent
{
    void Analyze(CompilationUnitSyntax root, SemanticModel? model, PatternDesugarPlanBuilder builder);

    /// <summary>null = this component does not handle the node.</summary>
    SyntaxNode? Rewrite(SyntaxNode node, PatternDesugarRewriteContext ctx);
}

internal sealed class SwitchLoweringPlan
{
    public required SwitchStatementSyntax Switch { get; init; }
    public required bool HasPatternLabels { get; init; }
    public required string? GoverningTempName { get; init; }
    public required ITypeSymbol? GoverningType { get; init; }
    public required bool NeedsEndLabel { get; init; }
    public required string EndLabelName { get; init; }
    public required IReadOnlyList<SectionPlan> Sections { get; init; }

    internal sealed class SectionPlan
    {
        public required bool IsDefault { get; init; }
        public required IReadOnlyList<LabelPlan> Labels { get; init; }
        public required IReadOnlyList<StatementSyntax> Statements { get; init; }
    }

    internal sealed class LabelPlan
    {
        /// <summary>Pattern label source; null for default labels (the section carries IsDefault).</summary>
        public required PatternSyntax? Pattern { get; init; }
    }
}

internal sealed class CatchLoweringPlan
{
    public enum LowerStrategy
    {
        /// <summary>Single catch clause with a filter (concrete type): keep the clause, wrap the body.</summary>
        PreserveBody,

        /// <summary>Multiple clauses / type-parameter catch: merge into one Exception catch chain.</summary>
        MergeChain,
    }

    public required TryStatementSyntax Try { get; init; }
    public required LowerStrategy Strategy { get; init; }
    public required IReadOnlyList<CatchClausePlan> Clauses { get; init; }
    public required string? SynthExceptionName { get; init; }

    internal sealed class CatchClausePlan
    {
        public required CatchClauseSyntax Clause { get; init; }
        public required string? OriginalVariableName { get; init; }
        public required bool IsTypeParameter { get; init; }
    }
}

/// <summary>Shared break-ownership rule: a break belongs to the switch when the
/// switch is its nearest enclosing switch/loop along the original ancestor chain.</summary>
internal static class PatternDesugarAnalysis
{
    public static bool BreakBelongsToSwitch(BreakStatementSyntax brk, SwitchStatementSyntax owner)
    {
        foreach (var ancestor in brk.Ancestors())
        {
            if (ReferenceEquals(ancestor, owner)) return true;
            if (ancestor is SwitchStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                or ForStatementSyntax or ForEachStatementSyntax)
                return false;
        }
        return false;
    }

    public static bool IsNonIntegerGoverning(ITypeSymbol? type)
        => type?.SpecialType is SpecialType.System_Boolean
           or SpecialType.System_Int64
           or SpecialType.System_UInt64
           or SpecialType.System_Single
           or SpecialType.System_Double
           or SpecialType.System_Decimal;
}

public sealed class SwitchStatementComponent : IPatternDesugarComponent
{
    public void Analyze(CompilationUnitSyntax root, SemanticModel? model, PatternDesugarPlanBuilder builder)
    {
        foreach (var sw in root.DescendantNodes().OfType<SwitchStatementSyntax>())
            AnalyzeSwitch(sw, model, builder);
    }

    public SyntaxNode? Rewrite(SyntaxNode node, PatternDesugarRewriteContext ctx)
    {
        if (node is not SwitchStatementSyntax sw || !ctx.SwitchPlans.TryGetValue(sw, out var plan))
            return null;

        var rewriter = ctx.Rewriter;
        ExpressionSyntax Governing() => plan.GoverningTempName != null
            ? SyntaxFactory.IdentifierName(plan.GoverningTempName)
            : plan.Switch.Expression.WithoutTrivia();

        var statements = new List<StatementSyntax>();
        if (plan.GoverningTempName != null)
        {
            statements.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(plan.GoverningTempName),
                        null,
                        SyntaxFactory.EqualsValueClause(plan.Switch.Expression.WithoutTrivia()))))));
        }

        StatementSyntax? chain = null;
        for (var i = plan.Sections.Count - 1; i >= 0; i--)
        {
            var section = plan.Sections[i];
            var body = SyntaxFactory.Block(section.Statements
                .Select(s => (StatementSyntax)rewriter.Visit(s)!)
                .ToList());

            if (section.IsDefault)
            {
                chain = body;
                continue;
            }

            ExpressionSyntax? condition = null;
            var labelConditions = new List<ExpressionSyntax>();
            foreach (var label in section.Labels)
            {
                if (label.Pattern == null) continue;
                var reduction = PatternReducer.Reduce(
                    label.Pattern, Governing(), plan.GoverningType, ctx.ReductionContext());
                if (!reduction.Supported)
                    return null; // analyzer pre-checked; defensive only
                labelConditions.Add(reduction.Condition!);
            }

            if (labelConditions.Count == 1)
            {
                condition = labelConditions[0];
            }
            else
            {
                // Multi-label section: parenthesized disjunction (D3).
                condition = SyntaxFactory.ParenthesizedExpression(labelConditions.Aggregate(
                    (l, r) => (ExpressionSyntax)SyntaxFactory.BinaryExpression(
                        SyntaxKind.LogicalOrExpression, l, r)));
            }

            chain = SyntaxFactory.IfStatement(condition, body,
                chain != null ? SyntaxFactory.ElseClause(chain) : null);
        }

        if (chain == null)
            return null;

        statements.Add(chain);

        if (plan.NeedsEndLabel)
        {
            statements.Add(SyntaxFactory.LabeledStatement(
                SyntaxFactory.Identifier(plan.EndLabelName), SyntaxFactory.EmptyStatement()));
            ctx.Statistics.EndLabelsIntroduced++;
        }

        if (plan.HasPatternLabels)
            ctx.Statistics.PatternSwitchesRewritten++;
        else
            ctx.Statistics.NonIntegerSwitchesRewritten++;
        if (plan.GoverningTempName != null)
            ctx.Statistics.GoverningTempsIntroduced++;

        var block = SyntaxFactory.Block(statements);
        return block
            .WithOpenBraceToken(block.OpenBraceToken.WithLeadingTrivia(sw.GetLeadingTrivia()))
            .WithCloseBraceToken(block.CloseBraceToken.WithTrailingTrivia(sw.GetTrailingTrivia()));
    }

    private static void AnalyzeSwitch(SwitchStatementSyntax sw, SemanticModel? model, PatternDesugarPlanBuilder builder)
    {
        var sections = sw.Sections.ToList();
        if (sections.Count == 0)
            return;

        var hasPatternLabels = sections.Any(s => s.Labels.Any(l => l is CasePatternSwitchLabelSyntax));

        // A default label in a non-final section would need an unreachable else-if —
        // degenerate (CS0162) input; refuse explicitly instead of dropping code.
        for (var i = 0; i < sections.Count - 1; i++)
        {
            if (sections[i].Labels.Any(l => l is DefaultSwitchLabelSyntax))
            {
                builder.ReportUnsupported(PatternDesugarCodes.UnsupportedPattern,
                    "PD010: default label in a non-final switch section is not lowered", sw);
                return;
            }
        }

        ITypeSymbol? governingType = null;
        if (model != null)
        {
            governingType = model.GetTypeInfo(sw.Expression).Type;
            if (governingType is IErrorTypeSymbol)
                governingType = null;
        }

        var isNonIntegerGoverning = !hasPatternLabels
            && governingType != null
            && PatternDesugarAnalysis.IsNonIntegerGoverning(governingType);
        if (!hasPatternLabels && !isNonIntegerGoverning)
            return; // int/char/string/enum switch with plain labels: converter handles 1:1 (D15)

        // goto case/default cannot survive if-chain lowering (D13).
        if (sw.Sections.SelectMany(s => s.Statements)
            .SelectMany(st => st.DescendantNodesAndSelf())
            .OfType<GotoStatementSyntax>()
            .Any(g => g.Kind() is SyntaxKind.GotoCaseStatement or SyntaxKind.GotoDefaultStatement))
        {
            builder.ReportUnsupported(PatternDesugarCodes.GotoCaseInLoweredSwitch,
                "PD011: goto case/default inside a pattern switch must stay on a real switch (E14 territory)", sw);
            return;
        }

        // D7: guards are preserved verbatim; any is-pattern inside a guard would
        // reach the converter's expression-side fallbacks — refuse until C37.
        foreach (var label in sections.SelectMany(s => s.Labels).OfType<CasePatternSwitchLabelSyntax>())
        {
            if (label.WhenClause != null
                && label.WhenClause.Condition.DescendantNodesAndSelf().Any(n => n is IsPatternExpressionSyntax))
            {
                builder.ReportUnsupported(PatternDesugarCodes.UnsupportedPattern,
                    "PD010: is-pattern inside a when guard is reduced by C37", label.WhenClause);
                return;
            }
        }

        string? governingTemp = null;
        var inlineable = sw.Expression is IdentifierNameSyntax identifier
            && (model == null
                || model.GetSymbolInfo(identifier).Symbol is ILocalSymbol or IParameterSymbol);
        if (!inlineable)
            governingTemp = builder.Names.Next("__cs2jGov");

        var subject = (ExpressionSyntax)(governingTemp != null
            ? SyntaxFactory.IdentifierName(governingTemp)
            : sw.Expression.WithoutTrivia());

        // Trial reduction: any unsupported label refuses the whole switch (D6) before rewriting.
        var trialContext = new PatternReductionContext(
            model, builder.Options, builder.Diagnostics, builder.FilePath, builder.Names.Next);
        foreach (var section in sections)
        {
            foreach (var label in section.Labels)
            {
                PatternSyntax? pattern = label switch
                {
                    CasePatternSwitchLabelSyntax patternLabel => patternLabel.Pattern,
                    CaseSwitchLabelSyntax constantLabel =>
                        SyntaxFactory.ConstantPattern(constantLabel.Value.WithoutTrivia()),
                    _ => null,
                };
                if (pattern == null)
                    continue;
                var reduction = PatternReducer.Reduce(pattern, subject, governingType, trialContext);
                if (!reduction.Supported)
                    return;
            }
        }

        var needsEndLabel = sections.Any(s => s.Statements.Any(st =>
            st.DescendantNodesAndSelf().OfType<BreakStatementSyntax>()
                .Any(b => PatternDesugarAnalysis.BreakBelongsToSwitch(b, sw))));

        builder.SwitchPlans[sw] = new SwitchLoweringPlan
        {
            Switch = sw,
            HasPatternLabels = hasPatternLabels,
            GoverningTempName = governingTemp,
            GoverningType = governingType,
            NeedsEndLabel = needsEndLabel,
            EndLabelName = builder.Names.Next("__cs2jSwitchEnd"),
            Sections = sections.Select(s => new SwitchLoweringPlan.SectionPlan
            {
                IsDefault = s.Labels.Any(l => l is DefaultSwitchLabelSyntax),
                Labels = s.Labels.Select(l => new SwitchLoweringPlan.LabelPlan
                {
                    Pattern = l switch
                    {
                        CasePatternSwitchLabelSyntax patternLabel => patternLabel.Pattern,
                        CaseSwitchLabelSyntax constantLabel =>
                            SyntaxFactory.ConstantPattern(constantLabel.Value.WithoutTrivia()),
                        _ => null,
                    },
                }).ToList(),
                Statements = s.Statements.ToList(),
            }).ToList(),
        };
    }
}

public sealed class CatchClauseComponent : IPatternDesugarComponent
{
    public void Analyze(CompilationUnitSyntax root, SemanticModel? model, PatternDesugarPlanBuilder builder)
    {
        foreach (var tryNode in root.DescendantNodes().OfType<TryStatementSyntax>())
            AnalyzeTry(tryNode, model, builder);
    }

    public SyntaxNode? Rewrite(SyntaxNode node, PatternDesugarRewriteContext ctx)
    {
        if (node is not TryStatementSyntax tryNode || !ctx.CatchPlans.TryGetValue(tryNode, out var plan))
            return null;

        var rewriter = ctx.Rewriter;

        if (plan.Strategy == CatchLoweringPlan.LowerStrategy.PreserveBody)
        {
            // D8 strategy ①: keep the catch clause (type + variable name), wrap the
            // body in `if (filter) { … } else throw;` — the else is a bare statement.
            var clause = plan.Clauses[0].Clause;
            var guarded = SyntaxFactory.IfStatement(
                clause.Filter!.Condition.WithoutTrivia(),
                SyntaxFactory.Block(clause.Block.Statements
                    .Select(s => (StatementSyntax)rewriter.Visit(s)!).ToList()),
                SyntaxFactory.ElseClause(SyntaxFactory.ThrowStatement()));
            ctx.Statistics.CatchFiltersRewritten++;
            return tryNode.WithCatches(SyntaxFactory.SingletonSeparatedList(
                clause.WithFilter(null).WithBlock(SyntaxFactory.Block(guarded))));
        }

        // D8 strategy ②: merged chain over one synthesized Exception parameter; each
        // branch re-binds the original variable name via `is T <name>`.
        StatementSyntax chain = SyntaxFactory.ThrowStatement();
        var filtersLowered = 0;
        var typeParamsLowered = 0;
        for (var i = plan.Clauses.Count - 1; i >= 0; i--)
        {
            var clausePlan = plan.Clauses[i];
            var clause = clausePlan.Clause;
            var body = SyntaxFactory.Block(clause.Block.Statements
                .Select(s => (StatementSyntax)rewriter.Visit(s)!).ToList());

            if (clause.Declaration == null)
            {
                chain = body; // typeless catch: final else, no condition
                continue;
            }

            VariableDesignationSyntax designation = clausePlan.OriginalVariableName != null
                ? SyntaxFactory.SingleVariableDesignation(
                    SyntaxFactory.Identifier(clausePlan.OriginalVariableName))
                : SyntaxFactory.DiscardDesignation();
            var typeTest = SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName(plan.SynthExceptionName!),
                SyntaxFactory.DeclarationPattern(clause.Declaration.Type.WithoutTrivia(), designation));
            var condition = typeTest;
            if (clause.Filter != null)
            {
                condition = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                    typeTest,
                    SyntaxFactory.ParenthesizedExpression(clause.Filter.Condition.WithoutTrivia()));
                filtersLowered++;
            }
            if (clausePlan.IsTypeParameter)
                typeParamsLowered++;

            chain = SyntaxFactory.IfStatement(condition, body, SyntaxFactory.ElseClause(chain));
        }

        ctx.Statistics.CatchFiltersRewritten += filtersLowered;
        ctx.Statistics.CatchTypeParamRewritten += typeParamsLowered;

        var mergedCatch = SyntaxFactory.CatchClause(
            SyntaxFactory.CatchDeclaration(
                SyntaxFactory.ParseTypeName("Exception"),
                SyntaxFactory.Identifier(plan.SynthExceptionName!)),
            null,
            SyntaxFactory.Block(chain));
        return tryNode.WithCatches(SyntaxFactory.SingletonSeparatedList(mergedCatch));
    }

    private static void AnalyzeTry(TryStatementSyntax tryNode, SemanticModel? model, PatternDesugarPlanBuilder builder)
    {
        var clauses = tryNode.Catches.ToList();
        if (clauses.Count == 0)
            return;

        var hasFilter = clauses.Any(c => c.Filter != null);
        var typeParamClauses = model == null
            ? new List<CatchClauseSyntax>()
            : clauses
                .Where(c => c.Declaration != null
                            && model.GetTypeInfo(c.Declaration.Type).Type is ITypeParameterSymbol)
                .ToList();

        if (!hasFilter && typeParamClauses.Count == 0)
            return; // plain concrete catches: converter handles 1:1

        // D7: filters are preserved verbatim — refuse is-patterns inside them until C37.
        foreach (var filter in clauses.Where(c => c.Filter != null).Select(c => c.Filter))
        {
            if (filter!.Condition.DescendantNodesAndSelf().Any(n => n is IsPatternExpressionSyntax))
            {
                builder.ReportUnsupported(PatternDesugarCodes.UnsupportedPattern,
                    "PD010: is-pattern inside a catch filter is reduced by C37", filter);
                return;
            }
        }

        if (clauses.Count == 1 && typeParamClauses.Count == 0)
        {
            builder.CatchPlans[tryNode] = new CatchLoweringPlan
            {
                Try = tryNode,
                Strategy = CatchLoweringPlan.LowerStrategy.PreserveBody,
                Clauses = new[]
                {
                    new CatchLoweringPlan.CatchClausePlan
                    {
                        Clause = clauses[0],
                        OriginalVariableName = OriginalVariableName(clauses[0]),
                        IsTypeParameter = false,
                    },
                },
                SynthExceptionName = null,
            };
            return;
        }

        builder.CatchPlans[tryNode] = new CatchLoweringPlan
        {
            Try = tryNode,
            Strategy = CatchLoweringPlan.LowerStrategy.MergeChain,
            Clauses = clauses.Select(c => new CatchLoweringPlan.CatchClausePlan
            {
                Clause = c,
                OriginalVariableName = OriginalVariableName(c),
                IsTypeParameter = typeParamClauses.Contains(c),
            }).ToList(),
            SynthExceptionName = builder.Names.Next("__cs2jEx"),
        };
    }

    private static string? OriginalVariableName(CatchClauseSyntax clause)
        => clause.Declaration == null
           || clause.Declaration.Identifier.IsKind(SyntaxKind.None)
           || string.IsNullOrEmpty(clause.Declaration.Identifier.ValueText)
            ? null
            : clause.Declaration.Identifier.ValueText;
}
```

Note: `SwitchStatementComponent` and `CatchClauseComponent` are declared `public` only because C# file-scoped-namespace internal classes referenced from `public` signatures would be fine as internal — keep them `public` in namespace `CSharpToJava.Core.PatternDesugar` to mirror `EnumPreprocessor`'s public surface (the Core assembly is consumed only by the CLI).

- [ ] **Step 2: Build** — expected to FAIL on the missing `PatternDesugarRewriteContext` / `PatternDesugarRewriter` (next task). The compiler errors must be exactly about those two missing types and nothing else. If other errors appear, fix them now.

```bash
dotnet build src/CSharpToJava.Core -c Release 2>&1 | grep -E "error CS" | sort -u | head
```

Expected: only `CS0246 PatternDesugarRewriteContext` / `PatternDesugarRewriter` errors.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/PatternDesugar/PatternDesugarComponents.cs
git commit -m "feat(c03): switch/catch desugar components with plan models (D2/D6-D10/D13)"
```

---

### Task 5: Rewriter — top-down plan rewriting with break ownership stack

**Files:**
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternDesugarRewriter.cs`

- [ ] **Step 1: Write the rewriter**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PatternDesugar;

internal sealed class PatternDesugarRewriteContext
{
    public required IReadOnlyDictionary<SwitchStatementSyntax, SwitchLoweringPlan> SwitchPlans { get; init; }
    public required IReadOnlyDictionary<TryStatementSyntax, CatchLoweringPlan> CatchPlans { get; init; }
    public required PatternDesugarOptions Options { get; init; }
    public required PatternDesugarStatistics Statistics { get; init; }
    public required PatternDesugarPlanBuilder Builder { get; init; }
    public required SemanticModel? Model { get; init; }
    public required string? FilePath { get; init; }

    /// <summary>Back-reference set by the rewriter so components can recurse into nested nodes.</summary>
    public required PatternDesugarRewriter Rewriter { get; set; }

    public PatternReductionContext ReductionContext() =>
        new(Model, Options, Builder.Diagnostics, FilePath, Builder.Names.Next);
}

/// <summary>
/// Top-down rewriter over the ORIGINAL tree: plans are keyed by original nodes,
/// so ancestor walks for break ownership stay intact. Components intercept
/// planned nodes; everything else descends normally.
/// </summary>
internal sealed class PatternDesugarRewriter : CSharpSyntaxRewriter
{
    private readonly PatternDesugarRewriteContext _ctx;
    private readonly IReadOnlyList<IPatternDesugarComponent> _components;
    private readonly Stack<(SwitchStatementSyntax Owner, string EndLabel)> _breakOwners = new();

    public PatternDesugarRewriter(
        PatternDesugarRewriteContext ctx,
        IReadOnlyList<IPatternDesugarComponent> components)
    {
        _ctx = ctx;
        _components = components;
        _ctx.Rewriter = this;
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (node is SwitchStatementSyntax sw && _ctx.SwitchPlans.ContainsKey(sw))
        {
            _breakOwners.Push((sw, _ctx.SwitchPlans[sw].EndLabelName));
            try
            {
                var replacement = Dispatch(node);
                if (replacement != null)
                    return replacement;
            }
            finally
            {
                _breakOwners.Pop();
            }
        }
        else if (node is TryStatementSyntax tryNode && _ctx.CatchPlans.ContainsKey(tryNode))
        {
            var replacement = Dispatch(node);
            if (replacement != null)
                return replacement;
        }

        return base.Visit(node);
    }

    public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
    {
        // Only breaks whose nearest enclosing switch/loop is the lowered switch
        // being rewritten become gotos (D12) — nested constructs keep their own.
        if (_breakOwners.Count > 0)
        {
            var (owner, endLabel) = _breakOwners.Peek();
            if (PatternDesugarAnalysis.BreakBelongsToSwitch(node, owner))
                return SyntaxFactory.GotoStatement(
                    SyntaxKind.GotoStatement, SyntaxFactory.IdentifierName(endLabel));
        }
        return node;
    }

    private SyntaxNode? Dispatch(SyntaxNode node)
    {
        foreach (var component in _components)
        {
            var replacement = component.Rewrite(node, _ctx);
            if (replacement != null)
                return replacement;
        }
        return null;
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.Core -c Release 2>&1 | tail -3
```

Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/PatternDesugar/PatternDesugarRewriter.cs
git commit -m "feat(c03): top-down pattern desugar rewriter with break-ownership stack (D12)"
```

---

### Task 6: Analyzer (dirty check + residual count) and Preprocessor entry

**Files:**
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternDesugarAnalyzer.cs`
- Create: `src/CSharpToJava.Core/PatternDesugar/PatternDesugarPreprocessor.cs`

- [ ] **Step 1: Write the analyzer**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PatternDesugar;

/// <summary>Syntax dirty check (deliberate superset) and the residual gate.</summary>
internal static class PatternDesugarAnalyzer
{
    /// <summary>
    /// Superset dirty check: any switch, any catch clause with a declaration
    /// (type-parameter catches need semantics), any catch filter, any pattern
    /// label. Files without matches are byte-level copies.
    /// </summary>
    public static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            n is SwitchStatementSyntax
            || n is CasePatternSwitchLabelSyntax
            || (n is CatchClauseSyntax catchClause
                && (catchClause.Filter != null || catchClause.Declaration != null)));

    /// <summary>Semantic residual gate: after rewriting, no target nodes may remain.</summary>
    public static int CountResidualTargets(string code, SemanticModel originalModel)
    {
        var newTree = CSharpSyntaxTree.ParseText(code, path: "pattern-desugar-output.cs");
        var newCompilation = originalModel.Compilation.ReplaceSyntaxTree(
            originalModel.SyntaxTree, newTree);
        var newModel = newCompilation.GetSemanticModel(newTree);
        var newRoot = (CompilationUnitSyntax)newTree.GetRoot();

        var count = 0;
        foreach (var node in newRoot.DescendantNodes())
        {
            if (node is CasePatternSwitchLabelSyntax)
            {
                count++;
            }
            else if (node is CatchClauseSyntax { Filter: not null })
            {
                count++;
            }
            else if (node is CatchDeclarationSyntax declaration
                     && newModel.GetTypeInfo(declaration.Type).Type is ITypeParameterSymbol)
            {
                count++;
            }
            else if (node is SwitchStatementSyntax switchStatement
                     && PatternDesugarAnalysis.IsNonIntegerGoverning(
                         newModel.GetTypeInfo(switchStatement.Expression).Type))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Syntax-only residual for the no-semantic-model (single file) path.</summary>
    public static int CountSyntaxResidualTargets(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Count(n =>
            n is CasePatternSwitchLabelSyntax
            || (n is CatchClauseSyntax { Filter: not null }));
    }
}
```

- [ ] **Step 2: Write the preprocessor entry**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.PatternDesugar;

/// <summary>
/// C03 statement-side pattern desugaring public entry point. Lowers pattern
/// switches, non-integer governing switches, catch filters and type-parameter
/// catches into compilable C# (if-chains / guarded catch bodies). Clean files
/// are byte-level preserved. No C#→Java converter code is involved.
/// </summary>
public sealed class PatternDesugarPreprocessor
{
    /// <summary>Ordered component registry; C37 appends expression-side components here.</summary>
    public static IReadOnlyList<IPatternDesugarComponent> Components { get; } =
        new IPatternDesugarComponent[]
        {
            new SwitchStatementComponent(),
            new CatchClauseComponent(),
        };

    public PatternDesugarResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        PatternDesugarOptions? options = null)
    {
        options ??= new PatternDesugarOptions();
        var stats = new PatternDesugarStatistics { FilesScanned = 1 };
        var diagnostics = new List<PatternDesugarDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!PatternDesugarAnalyzer.IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new PatternDesugarResult(sourceCode, false, diagnostics, stats);
        }

        var builder = new PatternDesugarPlanBuilder(options, semanticModel?.SyntaxTree.FilePath);
        foreach (var component in Components)
            component.Analyze(root, semanticModel, builder);
        diagnostics.AddRange(builder.Diagnostics);
        stats.UnsupportedSkipped = builder.UnsupportedSkipped;

        if (builder.SwitchPlans.Count == 0 && builder.CatchPlans.Count == 0)
        {
            // Dirty syntax but nothing lowerable (e.g. plain int switch, plain catch).
            stats.FilesDirtyNoRewrite = 1;
            return new PatternDesugarResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new PatternDesugarRewriter(
            new PatternDesugarRewriteContext
            {
                SwitchPlans = builder.SwitchPlans,
                CatchPlans = builder.CatchPlans,
                Options = options,
                Statistics = stats,
                Builder = builder,
                Model = semanticModel,
                FilePath = semanticModel?.SyntaxTree.FilePath,
                Rewriter = null!,
            },
            Components);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        // Residual gate: explicit PD010/PD011 files are already failed loudly, so
        // they skip the residual recheck (no double reporting).
        if (!builder.HasExplicitNodeErrors)
        {
            var residual = semanticModel != null
                ? PatternDesugarAnalyzer.CountResidualTargets(code, semanticModel)
                : PatternDesugarAnalyzer.CountSyntaxResidualTargets(code);
            stats.ResidualCount = residual;
            if (residual > 0)
            {
                diagnostics.Add(new PatternDesugarDiagnostic(
                    PatternDesugarCodes.ResidualTargets,
                    options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                    $"PD020: {residual} residual target node(s) after pattern desugaring.",
                    semanticModel?.SyntaxTree.FilePath));
            }
        }

        return new PatternDesugarResult(code, true, diagnostics, stats);
    }
}
```

Wait — `Rewriter = null!` then the rewriter constructor sets `_ctx.Rewriter = this`. The required property needs `set` (it has it). OK.

- [ ] **Step 3: Build the full solution**

```bash
dotnet build src/CSharpToJava.Core -c Release 2>&1 | tail -3
```

Expected: `Build succeeded`.

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/PatternDesugar/
git commit -m "feat(c03): pattern desugar preprocessor entry with residual gate"
```

---

### Task 7: Core unit tests — switch family (red → green)

**Files:**
- Create: `tests/CSharpToJava.Tests/PatternDesugarCoreTests.cs`

- [ ] **Step 1: Write the switch-family tests**

```csharp
using System.Text.RegularExpressions;
using CSharpToJava.Core.PatternDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C03 statement-side pattern desugaring: pattern switch → if-chain, non-integer
/// governing → == chain, catch filters/type-params → guarded bodies/merged chains.
/// Positive cases assert normalized text AND that the output is compilable C#
/// with zero residual target syntax.
/// </summary>
public class PatternDesugarCoreTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
    };

    private static PatternDesugarResult Desugar(
        string source, bool withModel = true, bool strict = true)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("pattern-desugar-test",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = withModel ? compilation.GetSemanticModel(tree) : null;
        return new PatternDesugarPreprocessor().Preprocess(
            source, model, new PatternDesugarOptions(Strict: strict));
    }

    private static string Normalize(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    private static void AssertCompilesAndClean(PatternDesugarResult result)
    {
        Assert.True(result.Changed);
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var compilation = CSharpCompilation.Create("pd-output",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "output should compile:\n" + result.OutputCode + "\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
        var root = tree.GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.CasePatternSwitchLabelSyntax>());
        Assert.Empty(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.CatchClauseSyntax>()
            .Where(c => c.Filter != null));
    }

    private const string ClassP = "class P { public int X; }";

    // ── pattern switch → if chain ──────────────────────────────────────────

    [Fact]
    public void PatternSwitch_DeclarationAndTypeLabels_BecomeIfChain()
    {
        var result = Desugar("""
class C {
    int M(object o) {
        switch (o) {
            case string s: return 1;
            case int i: return 2;
            default: return 0;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("o is string s", text);
        Assert.Contains("o is int i", text);
        Assert.DoesNotContain("switch", text);
        Assert.Contains("else", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_WhenClauses_SameVariableName_IsolatesPerBranch()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case int i when i > 0: System.Console.WriteLine("pos"); break;
            case int i when i < 0: System.Console.WriteLine("neg"); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("o is int i && (i > 0)", text);
        Assert.Contains("o is int i && (i < 0)", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_ConstantNull_OnObjectGoverning()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case null: System.Console.WriteLine("null"); break;
            case string s: System.Console.WriteLine(s); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("o == null", text);
        Assert.Contains("o is string s", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_MultipleLabels_ParenthesizedDisjunction()
    {
        var result = Desugar("""
class C {
    void M(int x) {
        switch (x) {
            case 1: case 2: System.Console.WriteLine("low"); break;
            case int i when i > 100: System.Console.WriteLine("big"); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("(x == 1 || x == 2)", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_WhenOrClause_GuardParenthesizedAsWhole()
    {
        // Precedence regression guard (SwitchCaseWhenClausePrecedenceTests invariant).
        var result = Desugar("""
class C {
    bool M(int type, string str) {
        switch (type) {
            case 1 when str == "A" || str == "B": return true;
            default: return false;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("type == 1 && (str == \"A\" || str == \"B\")", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_WhenAndClause_KeepsSingleParens()
    {
        var result = Desugar("""
class C {
    bool M(int type, int m, int ch) {
        switch (type) {
            case 1 when m > 0 && ch != 5: return true;
            default: return false;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("type == 1 && (m > 0 && ch != 5)", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_NoDefault_NoElse()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
            case int i: System.Console.WriteLine(i); break;
        }
    }
}
""");
        Assert.DoesNotContain("else", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_Default_Else()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
            default: System.Console.WriteLine("other"); break;
        }
    }
}
""");
        Assert.Contains("else", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_TerminatorsMixed_NoEndLabelWithoutBreaks()
    {
        var result = Desugar("""
class C {
    int M(object o, bool flag) {
        switch (o) {
            case string s: return 1;
            case int i: throw new System.Exception();
            case bool b when flag: return 2;
        }
    }
}
""");
        Assert.DoesNotContain("__cs2jSwitchEnd", result.OutputCode);
        Assert.DoesNotContain("goto", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_Break_BecomesGotoEndLabel()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
            case int i: System.Console.WriteLine(i); break;
        }
        System.Console.WriteLine("after");
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("goto __cs2jSwitchEnd_0;", text);
        Assert.Contains("__cs2jSwitchEnd_0: ;", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_NestedLoopBreak_NotHijacked()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s:
                while (true) { break; }
                break;
            case int i: break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        // The loop's break stays a break; the section's own break becomes goto.
        Assert.Contains("while (true) { break; }", text);
        Assert.Contains("goto __cs2jSwitchEnd_0;", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_NestedPatternSwitch_BothLowered()
    {
        var result = Desugar("""
class C {
    void M(object o, object p) {
        switch (o) {
            case string s:
                switch (p) {
                    case int j: System.Console.WriteLine(j); break;
                }
                break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("o is string s", text);
        Assert.Contains("p is int j", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_PropertyPattern_FieldAndProperty()
    {
        var result = Desugar($$"""
class C {
    int M(P p, Q q) {
        switch (p) {
            case P { X: 1 }: return 1;
            case { X: 2 }: return 2;
        }
        switch (q) {
            case Q { Name: "a" }: return 3;
        }
        return 0;
    }
}
{{ClassP}}
class Q { public string Name { get; set; } }
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("p is P", text);
        Assert.Contains(".X == 1", text);
        Assert.Contains("p != null && p.X == 2", text);
        Assert.Contains("q is Q", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_ObjectGoverning_ConstantUsesTypeTestForm()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case 1: System.Console.WriteLine("one"); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        // `o == 1` would box and compare by reference (always false) — must be is-form.
        Assert.Contains("o is int", text);
        Assert.Contains("== 1", text);
        Assert.DoesNotContain("o == 1", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_RelationalOrAndNotVarDiscard_Labels()
    {
        var result = Desugar("""
class C {
    int M(int x, object o) {
        switch (x) {
            case > 0: return 1;
            case >= 0 and <= 10: return 2;
        }
        switch (o) {
            case not null: return 3;
            case var v: return 4;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("x > 0", text);
        Assert.Contains("(x >= 0 && x <= 10)", text);
        Assert.Contains("!(o == null)", text);
        Assert.Contains("o is var v", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_DiscardLabel_TrueCondition()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
            case _: break;
        }
    }
}
""");
        Assert.Contains("true", Normalize(result.OutputCode));
        AssertCompilesAndClean(result);
    }

    // ── non-integer governing (D4) ─────────────────────────────────────────

    [Theory]
    [InlineData("bool flag", "true", "flag == true")]
    [InlineData("long l", "5L", "l == 5L")]
    [InlineData("ulong ul", "6UL", "ul == 6UL")]
    [InlineData("float f", "1.5f", "f == 1.5f")]
    [InlineData("double d", "2.5", "d == 2.5")]
    [InlineData("decimal m", "3.5m", "m == 3.5m")]
    public void NonIntegerGoverning_ConstantLabels_BecomeEqualityChain(
        string decl, string constant, string expected)
    {
        var result = Desugar($$"""
class C {
    void M({{decl}}) {
        switch ({{decl.Split(' ')[1]}}) {
            case {{constant}}: System.Console.WriteLine("hit"); break;
            default: break;
        }
    }
}
""");
        Assert.Contains(expected, Normalize(result.OutputCode));
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PlainSwitches_IntCharStringEnum_AreByteLevelPreserved()
    {
        const string source = """
enum E { A, B }
class C {
    void M(int i, char ch, string s, E e) {
        switch (i) { case 1: break; }
        switch (ch) { case 'a': break; }
        switch (s) { case "x": break; }
        switch (e) { case E.A: break; }
    }
    void N(Exception ex) { try { M(1, 'a', "", E.A); } catch (System.IO.IOException) { } }
}
""";
        var result = Desugar(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.True(result.Statistics.FilesDirtyNoRewrite > 0);
    }

    // ── governing single-evaluation (D2/D11) ───────────────────────────────

    [Fact]
    public void Governing_MethodCall_HoistedExactlyOnce()
    {
        var result = Desugar("""
class C {
    int counter;
    int Get() { counter++; return counter; }
    void M() {
        switch (Get()) {
            case int i: System.Console.WriteLine(i); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("var __cs2jGov_0 = Get();", text);
        Assert.Equal(1, Regex.Matches(text, "Get\\(\\)").Count);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void Governing_SimpleIdentifier_NotHoisted()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
        }
    }
}
""");
        Assert.DoesNotContain("__cs2jGov", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    // ── dirty/no-op paths ───────────────────────────────────────────────────

    [Fact]
    public void CleanFile_IsByteLevelPreserved()
    {
        const string source = "class C { int M() { return 1; } }";
        var result = Desugar(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.True(result.Statistics.FilesSkippedClean > 0);
    }

    [Fact]
    public void NoModel_Path_StillLowersPatternLabels()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
        }
    }
}
""", withModel: false);
        Assert.True(result.Changed);
        Assert.Contains("is string s", result.OutputCode);
    }

    [Fact]
    public void NoModel_Path_DoesNotLowerNonIntegerGoverning()
    {
        const string source = """
class C {
    void M(long l) {
        switch (l) { case 5L: break; }
    }
}
""";
        var result = Desugar(source, withModel: false);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }
}
```

- [ ] **Step 2: Run the tests**

```bash
dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~PatternDesugarCoreTests" 2>&1 | tail -5
```

Expected: all pass. If a `Normalize`d assertion misses, print `result.OutputCode` from the failure message and adjust the test to the actual formatted text ONLY if the semantics are right (parenthesization, naming, ordering must match the design).

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/PatternDesugarCoreTests.cs
git commit -m "test(c03): core switch-family desugar unit tests"
```

---

### Task 8: Core unit tests — catch family + diagnostics

**Files:**
- Modify: `tests/CSharpToJava.Tests/PatternDesugarCoreTests.cs` (append)

- [ ] **Step 1: Append the catch-family and diagnostic tests** (inside the same class)

```csharp
    // ── catch-when strategy ①: preserve body (D8①/D10) ─────────────────────

    [Fact]
    public void CatchFilter_SingleClause_PreservesDeclarationAndWrapsBody()
    {
        var result = Desugar("""
class C {
    bool M() {
        try {
            throw new System.InvalidOperationException("boom");
        }
        catch (System.InvalidOperationException ex) when (ex.Message == "expected") {
            System.Console.WriteLine("caught");
            return true;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("catch (System.InvalidOperationException ex)", text);
        Assert.DoesNotContain("when", text);
        Assert.Contains("if (ex.Message == \"expected\")", text);
        Assert.Contains("else throw;", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchFilter_SingleClause_FinallyPreserved()
    {
        var result = Desugar("""
class C {
    bool M() {
        try {
            return true;
        }
        catch (System.InvalidOperationException ex) when (ex.Message == "x") {
            return false;
        }
        finally {
            System.Console.WriteLine("cleanup");
        }
    }
}
""");
        Assert.Contains("finally", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    // ── catch-when strategy ②: merged chain (D8②) ──────────────────────────

    [Fact]
    public void CatchFilter_SameTypeTwoClauses_FilterFalseFallsToNext()
    {
        var result = Desugar("""
class C {
    void M() {
        try { System.Console.WriteLine(); }
        catch (System.Exception ex) when (ex.Message == "a") { System.Console.WriteLine("first"); }
        catch (System.Exception ex) { System.Console.WriteLine("second"); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("catch (Exception __cs2jEx_0)", text);
        var firstIndex = text.IndexOf("__cs2jEx_0 is System.Exception ex && (ex.Message == \"a\")", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("__cs2jEx_0 is System.Exception ex)", firstIndex + 1, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, text);
        Assert.True(secondIndex > firstIndex, "filter-less clause must come after the filtered one:\n" + text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchFilter_MultiType_MultiFilter_OrderPreserved()
    {
        var result = Desugar("""
class C {
    void M() {
        try { System.Console.WriteLine(); }
        catch (System.FormatException f) when (f.Message != null) { System.Console.WriteLine("f"); }
        catch (System.IO.IOException io) when (io.HResult > 0) { System.Console.WriteLine("io"); }
        catch (System.Exception e) { System.Console.WriteLine("e"); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        var i1 = text.IndexOf("__cs2jEx_0 is System.FormatException f && (f.Message != null)", StringComparison.Ordinal);
        var i2 = text.IndexOf("__cs2jEx_0 is System.IO.IOException io && (io.HResult > 0)", StringComparison.Ordinal);
        var i3 = text.IndexOf("__cs2jEx_0 is System.Exception e)", StringComparison.Ordinal);
        Assert.True(i1 >= 0 && i2 > i1 && i3 > i2, text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchFilter_TypelessCatch_IsFinalElseWithoutThrow()
    {
        var result = Desugar("""
class C {
    void M() {
        try { System.Console.WriteLine(); }
        catch (System.FormatException f) when (f.Message != null) { System.Console.WriteLine("f"); }
        catch { System.Console.WriteLine("catch-all"); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("is System.FormatException f", text);
        Assert.Contains("catch-all", text);
        // The typeless clause terminates the chain: no trailing rethrow after it.
        Assert.True(text.IndexOf("catch-all", StringComparison.Ordinal)
                    < text.IndexOf("else throw;", StringComparison.Ordinal),
            "typeless catch body must be the terminal else:\n" + text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchBody_BareThrow_RethrowsMergedVariable()
    {
        var result = Desugar("""
class C {
    void M() {
        try { System.Console.WriteLine(); }
        catch (System.FormatException f) when (f.Message != null) { throw; }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("else throw;", text);
        AssertCompilesAndClean(result);
    }

    // ── catch T (D9) ────────────────────────────────────────────────────────

    [Fact]
    public void CatchTypeParameter_Constrained_LowersToIsTest()
    {
        var result = Desugar("""
class C {
    void M<T>(T arg) where T : System.Exception {
        try { System.Console.WriteLine(arg); }
        catch (T ex) { System.Console.WriteLine(ex); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("catch (Exception __cs2jEx_0)", text);
        Assert.Contains("__cs2jEx_0 is T ex", text);
        Assert.DoesNotContain("catch (T", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchTypeParameter_WithFilter_CombinesTypeTestAndGuard()
    {
        var result = Desugar("""
class C {
    void M<T>(T arg) {
        try { System.Console.WriteLine(arg); }
        catch (T ex) when (ex != null) { System.Console.WriteLine(ex); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("__cs2jEx_0 is T ex && (ex != null)", text);
        AssertCompilesAndClean(result);
    }

    // ── diagnostics ────────────────────────────────────────────────────────

    [Fact]
    public void ListPatternLabel_ReportsPD010_AndLeavesSwitchUntouched()
    {
        const string source = """
class C {
    void M(object o) {
        switch (o) {
            case [1, ..]: System.Console.WriteLine("list"); break;
        }
    }
}
""";
        var result = Desugar(source);
        Assert.False(result.Changed, "the switch must not be lowered");
        Assert.Equal(source, result.OutputCode);
        var pd010 = Assert.Single(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern);
        Assert.Equal(PatternDesugarSeverity.Error, pd010.Severity);
        Assert.True(result.Statistics.UnsupportedSkipped > 0);
    }

    [Fact]
    public void PositionalPatternLabel_ReportsPD010()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case (1, 2): System.Console.WriteLine("tuple"); break;
        }
    }
}
""");
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern
            && d.Message.Contains("positional"));
    }

    [Fact]
    public void GuardWithIsPattern_ReportsPD010()
    {
        var result = Desugar("""
class C {
    void M(object o, object p) {
        switch (o) {
            case string s when p is int: System.Console.WriteLine(s); break;
        }
    }
}
""");
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern
            && d.Message.Contains("when guard"));
    }

    [Fact]
    public void GotoCaseInsidePatternSwitch_ReportsPD011_AndLeavesSwitch()
    {
        const string source = """
class C {
    void M(int x, bool y) {
        switch (x) {
            case int i when y: goto default;
            case int i when !y: break;
            default: break;
        }
    }
}
""";
        var result = Desugar(source);
        Assert.False(result.Changed);
        var pd011 = Assert.Single(result.Diagnostics, d => d.Code == PatternDesugarCodes.GotoCaseInLoweredSwitch);
        Assert.Equal(PatternDesugarSeverity.Error, pd011.Severity);
    }

    [Fact]
    public void NullConstantOnValueTypeGoverning_ReportsPD010()
    {
        var result = Desugar("""
class C {
    void M(int x) {
        switch (x) {
            case null: break;
        }
    }
}
""");
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern
            && d.Message.Contains("non-nullable value-type"));
    }

    [Fact]
    public void ResidualCounter_FindsTargetNodes_InRawCode()
    {
        const string source = """
class C {
    void M(object o) {
        switch (o) { case string s: break; }
    }
    void N(System.Exception ex) { try { } catch (System.Exception e) when (e.Message != null) { } }
}
""";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("residual",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var count = PatternDesugarAnalyzer_Raw.CountResidualTargets(source, model);
        Assert.Equal(2, count); // one pattern label + one catch filter
    }

    [Fact]
    public void NonStrict_DowngradesPD010ToWarning()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) { case (1, 2): break; }
    }
}
""", strict: false);
        Assert.False(result.Changed);
        var pd010 = Assert.Single(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern);
        Assert.Equal(PatternDesugarSeverity.Warning, pd010.Severity);
    }
}
```

The `PatternDesugarAnalyzer_Raw` reference: the analyzer is `internal`. Add an `InternalsVisibleTo` — check whether `CSharpToJava.Core` already exposes internals to the test project. If `tests/CSharpToJava.Tests` already sees internals (likely — check `src/CSharpToJava.Core/CSharpToJava.Core.csproj` or `Properties/AssemblyInfo.cs` for `InternalsVisibleTo("CSharpToJava.Tests")`), use `PatternDesugarAnalyzer` directly; otherwise add the attribute. Simpler alternative used below: make `CountResidualTargets` exercise via a public facade — no: prefer InternalsVisibleTo if present, else change the test to assert through a real preprocess whose output is hand-corrupted:

```csharp
    // If internals are not visible to the test assembly, replace the
    // ResidualCounter test with this behavioral variant: preprocess a file, then
    // re-run residual counting through a second Preprocess call on a crafted
    // source containing a pattern label (same API surface, no internals needed).
```

Decide at implementation time by grepping `InternalsVisibleTo` in `src/CSharpToJava.Core/`; if absent, add `<InternalsVisibleTo Include="CSharpToJava.Tests" />` to the Core csproj (net10 SDK supports this ItemGroup natively).

- [ ] **Step 2: Run the tests**

```bash
dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~PatternDesugarCoreTests" 2>&1 | tail -5
```

Expected: all pass.

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/PatternDesugarCoreTests.cs src/CSharpToJava.Core/CSharpToJava.Core.csproj
git commit -m "test(c03): catch-family + diagnostics desugar unit tests"
```

---

### Task 9: CLI adapter

**Files:**
- Create: `src/CSharpToJava.CLI/Preprocessing/Adapters/PatternDesugarPassAdapter.cs`

- [ ] **Step 1: Write the adapter** (mirrors `EnumPassAdapter` exactly in shape)

```csharp
using CSharpToJava.Core.PatternDesugar;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C03 pattern statement desugar pass. Mirrors the enum
/// adapter's statistics merge, residual gate and diagnostic handling.
/// </summary>
internal sealed class PatternDesugarPassAdapter : ProjectPassAdapter
{
    private readonly PatternDesugarOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly PatternDesugarPreprocessor _preprocessor = new();
    private readonly List<PatternDesugarDiagnostic> _diagnostics = new();
    private readonly PatternDesugarStatistics _statistics = new();

    public PatternDesugarPassAdapter(
        PatternDesugarOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool strict)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _strict = strict;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.PatternDesugar);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public PatternDesugarStatistics Statistics => _statistics;
    public IReadOnlyList<PatternDesugarDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic.Code == PatternDesugarCodes.UnsupportedPattern
                && _options.ResidualWhitelist != null
                && _options.ResidualWhitelist.Any(w => context.RelativePath.Contains(w, StringComparison.Ordinal)))
            {
                continue; // whitelisted file: PD010 downgraded to informational skip
            }
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            var status = result.Changed ? "transformed"
                : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                : "dirty-no-rewrite (copy)";
            Console.WriteLine($"[pattern-desugar] {status}: {context.RelativePath}");
        }

        var rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(rewrite);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_statistics.ResidualCount > 0)
        {
            _diagnostics.Add(new PatternDesugarDiagnostic(
                PatternDesugarCodes.ResidualTargets,
                _strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                $"{_statistics.ResidualCount} residual pattern/catch target node(s) remaining after pattern desugaring."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new PatternDesugarDiagnostic(
            PatternDesugarCodes.RestoreOrBuildFailed,
            PatternDesugarSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new PatternDesugarDiagnostic(
            PatternDesugarCodes.RestoreOrBuildFailed,
            PatternDesugarSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after pattern desugaring."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new PatternDesugarDiagnostic(
            PatternDesugarCodes.RestoreOrBuildFailed,
            PatternDesugarSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after pattern desugaring."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == PatternDesugarSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"pattern-desugar: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.FilesDirtyNoRewrite} dirty-no-rewrite, " +
           $"{_statistics.PatternSwitchesRewritten} pattern-switch, " +
           $"{_statistics.NonIntegerSwitchesRewritten} non-int-switch, " +
           $"{_statistics.CatchFiltersRewritten} catch-filter, " +
           $"{_statistics.CatchTypeParamRewritten} catch-T, " +
           $"{_statistics.GoverningTempsIntroduced} govs, " +
           $"{_statistics.EndLabelsIntroduced} end-labels, " +
           $"{_statistics.UnsupportedSkipped} skipped, " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[{d.Severity}] {prefix}{d.Message}";
            })
            .ToList();

    private void MergeStatistics(PatternDesugarStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.PatternSwitchesRewritten += source.PatternSwitchesRewritten;
        _statistics.NonIntegerSwitchesRewritten += source.NonIntegerSwitchesRewritten;
        _statistics.CatchFiltersRewritten += source.CatchFiltersRewritten;
        _statistics.CatchTypeParamRewritten += source.CatchTypeParamRewritten;
        _statistics.GoverningTempsIntroduced += source.GoverningTempsIntroduced;
        _statistics.EndLabelsIntroduced += source.EndLabelsIntroduced;
        _statistics.UnsupportedSkipped += source.UnsupportedSkipped;
        _statistics.ResidualCount += source.ResidualCount;
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/CSharpToJava.CLI -c Release 2>&1 | tail -3
```

Expected: `Build succeeded` (the adapter compiles standalone; registry wiring comes next task).

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.CLI/Preprocessing/Adapters/PatternDesugarPassAdapter.cs
git commit -m "feat(c03): pattern-desugar CLI pass adapter"
```

---

### Task 10: Registry flip

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs` (add `PassIds.PatternDesugar`, insert descriptor)
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs` (remove entry + constant)
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs` (adapter branch)
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs` (23→22 + assertions)
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs` (LockedChain + BuildLegacyToggles exclusion)

- [ ] **Step 1: Add the PassIds constant** — in `PassCatalog.cs`, inside `internal static class PassIds`, after `ForeachDeconstruction`:

```csharp
    public const string PatternDesugar = "pattern-desugar";
```

- [ ] **Step 2: Insert the descriptor** — in `PassCatalog.Implemented`, between the `ForeachDeconstruction` entry and the `StripDefault` entry:

```csharp
        new(
            PassIds.PatternDesugar,
            "Pattern desugaring",
            ".cs2j-pattern-desugar-src",
            PassStage.ControlFlow,
            PassStatus.Implemented,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.NullCoalesce, PassIds.Tuple, PassIds.ForeachDeconstruction },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Reduce pattern switches, non-integer governing switches, catch filters and type-parameter catches into if-chains and boolean expressions.",
            catalogItems: "C03, C37"),
```

- [ ] **Step 3: Remove the stub** — in `PassStubCatalog.cs`, delete the whole `new(Ids.PatternDesugar, …)` entry (lines ~72-81) AND the `public const string PatternDesugar = "pattern-desugar";` line from `Ids`.

- [ ] **Step 4: Add the factory branch** — in `PassChainFactory.CreateAdapter`, after the `PassIds.ForeachDeconstruction` case:

```csharp
            PassIds.PatternDesugar => new PatternDesugarPassAdapter(
                new PatternDesugarOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),
```

And add `using CSharpToJava.Core.PatternDesugar;` to the usings at the top.

- [ ] **Step 5: Update `PassStubCatalogTests`** — change `Assert.Equal(23, ids.Length);` to `Assert.Equal(22, ids.Length);` and add inside `All_merged_families_are_registered_as_stubs`:

```csharp
        Assert.DoesNotContain("pattern-desugar", ids);
```

Also add a new test to the same class:

```csharp
    [Fact]
    public void PatternDesugar_IsImplemented_AndNotAStub()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.PatternDesugar);

        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Equal("C03, C37", descriptor.CatalogItems);
        Assert.Equal(".cs2j-pattern-desugar-src", descriptor.IntermediateDirectoryName);
        Assert.DoesNotContain(Stubs, stub => stub.Id == PassIds.PatternDesugar);
    }
```

- [ ] **Step 6: Update `PassChainOrderTests`** — in `LockedChain`, insert `PassIds.PatternDesugar,` right after `PassIds.ForeachDeconstruction,` (before `PassIds.StripDefault,`). In `BuildLegacyToggles_covers_every_registered_pass`, change the exclusion to include the new pass:

```csharp
            LockedChain.Where(id => id != PassIds.GenericDeepen && id != PassIds.PatternDesugar)
```

- [ ] **Step 7: Build + run registry tests**

```bash
dotnet build tests/CSharpToJava.Tests -c Release 2>&1 | tail -3
dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~PassStubCatalogTests|FullyQualifiedName~PassChainOrderTests" 2>&1 | tail -3
```

Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/CSharpToJava.CLI/Preprocessing/ tests/CSharpToJava.Tests/Preprocessing/
git commit -m "feat(c03): flip pattern-desugar stub to Implemented (D17)"
```

---

### Task 11: Existing tests — DesugarThenConvert fixtures (D16)

**Files:**
- Modify: `tests/CSharpToJava.Tests/PatternMatchingTests.cs`
- Modify: `tests/CSharpToJava.Tests/CatchFilterRethrowTests.cs`
- Modify: `tests/CSharpToJava.Tests/SwitchCaseWhenClausePrecedenceTests.cs`
- Modify: `tests/CSharpToJava.ConversionTests/Categories/PatternMatchingTests.cs`

- [ ] **Step 1: Add the shared helper and fix the 3 statement-side tests** — in `tests/CSharpToJava.Tests/PatternMatchingTests.cs`, add using `CSharpToJava.Core.PatternDesugar;` and change the private `Convert` helper to:

```csharp
    private static ConversionResult Convert(string csharpCode)
    {
        var desugared = new PatternDesugarPreprocessor().Preprocess(csharpCode);
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = desugared.OutputCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
```

(The expression-side tests in this file are unaffected: they don't contain C03 targets, so `Preprocess` returns them unchanged. This keeps a single helper for the whole file.)

- [ ] **Step 2: Fix CatchFilterRethrowTests** — same helper change (`FileName = "Sample.cs"`), and update the assertion per D16②:

```csharp
        Assert.Contains("throw ex;", code, StringComparison.Ordinal);
        Assert.Contains("else", code, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new AssertionError(\"unreachable\")", code, StringComparison.Ordinal);
```

(Remove the exact-text `Assert.Contains("else { throw ex; }", …)` line — the main if/else renderer always wraps else bodies in multiline blocks.)

- [ ] **Step 3: Fix SwitchCaseWhenClausePrecedenceTests** — same helper change, and update the two assertions per D16③:

```csharp
        Assert.Contains("type == 1 && (Objects.equals(str, \"A\") || Objects.equals(str, \"B\"))", code);
```

```csharp
        Assert.Contains("type == 1 && (m > 0 && ch != 5)", code);
```

- [ ] **Step 4: Run the three test classes**

```bash
dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~PatternMatchingTests|FullyQualifiedName~CatchFilterRethrowTests|FullyQualifiedName~SwitchCaseWhenClausePrecedenceTests" 2>&1 | tail -5
```

Expected: 16/16 pass (was 6 failed). If `SwitchCaseWhenClausePrecedenceTests` misses the assertion text, print the Java output (temporarily add `output.Write(code)` or run with the failure message) — the desugared C# is `if (type == 1 && (str == "A" || str == "B"))`, and the Java side renders string equality via `Objects.equals`; adjust only if the Java form differs from the spec'd text, and record the actual form.

- [ ] **Step 5: Skip the C37 property-pattern ConversionTest** — in `tests/CSharpToJava.ConversionTests/Categories/PatternMatchingTests.cs`:

```csharp
    [Fact(Skip = "C37: expression-side recursive pattern reducer")]
    public void PropertyPattern_ConvertsToTodoMarker()
```

with the body replaced by the C37 target draft (no stale TODO text):

```csharp
    [Fact(Skip = "C37: expression-side recursive pattern reducer")]
    public void PropertyPattern_ConvertsToMemberComparison()
    {
        // C37 will lower `p is { X: 1 }` to `p != null && p.X == 1` on the Java side.
        // The old expectation asserted a `/* TODO: recursive pattern */` marker that
        // no longer exists anywhere in src — the test stays skipped until C37 lands.
        var result = Convert("class C { public void M(P p) { if (p is { X: 1 }) { } } } public class P { public int X; }");
        AssertConversion(result, "p != null", "p.X == 1");
    }
```

- [ ] **Step 6: Run ConversionTests**

```bash
dotnet test tests/CSharpToJava.ConversionTests -c Release 2>&1 | tail -3
```

Expected: all pass except exactly 1 skipped (`PropertyPattern_ConvertsToMemberComparison`).

- [ ] **Step 7: Commit**

```bash
git add tests/CSharpToJava.Tests/ tests/CSharpToJava.ConversionTests/
git commit -m "test(c03): desugar-then-convert fixtures turn statement-side pattern tests green (D16)"
```

---

### Task 12: CLI reachability tests

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/PreprocessVerbCLITests.cs` (append)

- [ ] **Step 1: Append CLI tests** (reuse the existing `RunCliAsync` / `CreateTempWorkspace` helpers):

```csharp
    [Fact]
    public async Task Preprocess_PatternDesugarPass_LowersPatternSwitchIntoIntermediateTree()
    {
        var workspace = CreateTempWorkspace("PatternSample.csproj", """
namespace PatternSample;

public static class Switches
{
    public static int Pick(object o)
    {
        switch (o)
        {
            case string s: return s.Length;
            case int i: return i;
            default: return 0;
        }
    }
}
""");
        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", workspace, "--pass", "pattern-desugar");

            Assert.True(exit == 0, stdout + stderr);
            var intermediate = Path.Combine(workspace, ".cs2j-pattern-desugar-src");
            Assert.True(Directory.Exists(intermediate));
            var transformed = File.ReadAllText(
                Path.Combine(intermediate, "Class1.cs"));
            Assert.Contains("is string s", transformed);
            Assert.DoesNotContain("case string s:", transformed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ListPasses_ShowsPatternDesugarAsImplemented()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("pattern-desugar"));
        Assert.Contains("Implemented", line);
    }
```

(Verify the exact `preprocess` verb argument order against an existing test in the same file that runs a real pass; adjust `--pass` placement if the CLI parser expects it elsewhere.)

- [ ] **Step 2: Run**

```bash
dotnet test tests/CSharpToJava.ProjectTests -c Release --filter "FullyQualifiedName~PreprocessVerbCLITests" 2>&1 | tail -5
```

Expected: all pass.

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.ProjectTests/PreprocessVerbCLITests.cs
git commit -m "test(c03): CLI reachability for the pattern-desugar pass"
```

---

### Task 13: SamplePatternSolution fixture + project tests

**Files:**
- Create: `tests/SamplePatternSolution/SamplePatternSolution.sln`
- Create: `tests/SamplePatternSolution/SamplePatternLib/SamplePatternLib.csproj`
- Create: `tests/SamplePatternSolution/SamplePatternLib/PatternSwitchSamples.cs`
- Create: `tests/SamplePatternSolution/SamplePatternLib/CatchSamples.cs`
- Create: `tests/SamplePatternSolution/SamplePatternApp/SamplePatternApp.csproj`
- Create: `tests/SamplePatternSolution/SamplePatternApp/Program.cs`
- Create: `tests/SamplePatternSolution/SamplePatternTests/SamplePatternTests.csproj`
- Create: `tests/SamplePatternSolution/SamplePatternTests/RuntimeBehaviorTests.cs`
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` (Content include)
- Create: `tests/CSharpToJava.ProjectTests/PatternDesugarProjectTests.cs`

- [ ] **Step 1: Fixture projects** — copy the csproj/sln patterns from SampleRecordSolution. `SamplePatternLib.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

`SamplePatternApp.csproj` adds `<ProjectReference Include="..\SamplePatternLib\SamplePatternLib.csproj" />`. `SamplePatternTests.csproj` mirrors the xunit package versions of `tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` plus a ProjectReference to the Lib. The `.sln` registers all three projects (GUIDs `{77777777-…}`, `{88888888-…}`, `{99999999-…}` in the SampleRecordSolution.sln format).

- [ ] **Step 2: Fixture library sources** — `PatternSwitchSamples.cs` (every construct with observable behavior) and `CatchSamples.cs`:

```csharp
namespace SamplePatternLib;

public static class PatternSwitchSamples
{
    public static string ClassifyObject(object o)
    {
        switch (o)
        {
            case string s when s.Length > 3: return "long-string";
            case string s: return "string";
            case int i when i > 0: return "positive";
            case int i: return "non-positive-int";
            case null: return "null";
            case 1 or 2: return "boxed-one-or-two";
            case bool b: return b ? "true" : "false";
            default: return "other";
        }
    }

    public static string ClassifyBool(bool flag)
    {
        switch (flag)
        {
            case true: return "yes";
            default: return "no";
        }
    }

    public static string ClassifyLong(long value)
    {
        switch (value)
        {
            case 0L: return "zero";
            case 42L: return "answer";
            default: return "other";
        }
    }

    public static int NestedLoopBreakInsidePatternCase(object o)
    {
        switch (o)
        {
            case int i:
                var total = 0;
                while (true)
                {
                    total++;
                    break;
                }
                total += i;
                return total;
            default: return -1;
        }
    }
}

public class Point
{
    public int X { get; set; }
    public int Y { get; set; }
}

public static class PropertyPatternSamples
{
    public static string Describe(Point p)
    {
        switch (p)
        {
            case Point { X: 0, Y: 0 }: return "origin";
            case Point { X: 0 }: return "y-axis";
            case { X: 1 }: return "x-is-one";
            case null: return "null";
            default: return "other";
        }
    }
}
```

`CatchSamples.cs`:

```csharp
namespace SamplePatternLib;

public static class CatchSamples
{
    public static string FilterThenSameTypeFallback(bool matchFilter)
    {
        try
        {
            if (matchFilter)
                throw new InvalidOperationException("expected");
            throw new InvalidOperationException("other");
        }
        catch (InvalidOperationException ex) when (ex.Message == "expected")
        {
            return "filtered";
        }
        catch (InvalidOperationException)
        {
            return "fallback";
        }
    }

    public static string FilterFalsePropagates(bool matchFilter)
    {
        try
        {
            try
            {
                throw new InvalidOperationException("no-match");
            }
            catch (InvalidOperationException ex) when (ex.Message == "expected")
            {
                return "wrongly-caught";
            }
        }
        catch (InvalidOperationException)
        {
            // The inner filter must not catch "no-match": the exception propagates here.
            return "propagated";
        }
    }

    public static string CatchTypeParameter<T>(T arg) where T : Exception
    {
        try
        {
            throw arg;
        }
        catch (T ex) when (ex.Message == "typed")
        {
            return "typed-catch";
        }
    }

    public static string WithFinally(bool matchFilter)
    {
        var log = "";
        try
        {
            try { throw new InvalidOperationException("x"); }
            catch (InvalidOperationException ex) when (ex.Message == "expected") { log += "caught;"; }
            finally { log += "finally;"; }
        }
        catch (InvalidOperationException)
        {
            log += "propagated;";
        }
        return log;
    }
}
```

`SamplePatternApp/Program.cs` (cross-file consumption — proves no variable leaks across files):

```csharp
using SamplePatternLib;

Console.WriteLine(PatternSwitchSamples.ClassifyObject("abcd"));
Console.WriteLine(CatchSamples.FilterThenSameTypeFallback(true));
```

- [ ] **Step 3: Runtime behavior tests in the fixture** — `RuntimeBehaviorTests.cs` (xunit; these run against the intermediate tree in the project test below):

```csharp
using SamplePatternLib;
using Xunit;

namespace SamplePatternTests;

public class RuntimeBehaviorTests
{
    [Theory]
    [InlineData("abcd", "long-string")]
    [InlineData("ab", "string")]
    [InlineData(5, "positive")]
    [InlineData(-5, "non-positive-int")]
    [InlineData(null, "null")]
    [InlineData(1, "boxed-one-or-two")]
    [InlineData(true, "true")]
    [InlineData(3.5, "other")]
    public void ClassifyObject_MatchesEachBranch(object o, string expected)
        => Assert.Equal(expected, PatternSwitchSamples.ClassifyObject(o));

    [Fact]
    public void BoolAndLongGoverningSwitches_Match()
    {
        Assert.Equal("yes", PatternSwitchSamples.ClassifyBool(true));
        Assert.Equal("answer", PatternSwitchSamples.ClassifyLong(42L));
        Assert.Equal("zero", PatternSwitchSamples.ClassifyLong(0L));
    }

    [Fact]
    public void NestedLoopBreak_KeepsInnerBreakSemantics()
        => Assert.Equal(6, PatternSwitchSamples.NestedLoopBreakInsidePatternCase(5));

    [Theory]
    [InlineData(0, 0, "origin")]
    [InlineData(0, 5, "y-axis")]
    [InlineData(1, 9, "x-is-one")]
    [InlineData(2, 9, "other")]
    public void PropertyPattern_BranchesMatch(int x, int y, string expected)
        => Assert.Equal(expected, PropertyPatternSamples.Describe(new Point { X = x, Y = y }));

    [Fact]
    public void SameTypeTwoCatches_FilterFalseFallsToSecondClause()
    {
        Assert.Equal("filtered", CatchSamples.FilterThenSameTypeFallback(true));
        Assert.Equal("fallback", CatchSamples.FilterThenSameTypeFallback(false));
    }

    [Fact]
    public void FilterFalse_PropagatesToOuterHandler()
        => Assert.Equal("propagated", CatchSamples.FilterFalsePropagates(false));

    [Fact]
    public void CatchTypeParameter_WithFilter_CatchesDerivedException()
        => Assert.Equal("typed-catch",
            CatchSamples.CatchTypeParameter(new InvalidOperationException("typed")));

    [Fact]
    public void FinallyRuns_BeforePropagationDecision()
        => Assert.Equal("finally;propagated;", CatchSamples.WithFinally(false));
}
```

- [ ] **Step 4: Content include** — in `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`, next to the other sample-solution includes:

```xml
    <Content Include="..\SamplePatternSolution\**\*.*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
```

- [ ] **Step 5: Project tests** — `PatternDesugarProjectTests.cs` (drives the pass through `CliPreprocessChain`, mirroring `PassStubCatalogTests` usage):

```csharp
using CSharpToJava.CLI;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for the pattern-desugar pass: the SamplePatternSolution
/// intermediate tree must compile, its runtime tests must pass on the desugared
/// C#, and the output must contain no C03 target syntax (residual = 0).
/// </summary>
public class PatternDesugarProjectTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "SamplePatternSolution");

    private static async Task<(CSharpToJava.CLI.CliChainResult Result, string OutputRoot)> RunAsync()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-pd-" + Guid.NewGuid().ToString("N"));
        var result = await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = Path.Combine(FixtureDir, "SamplePatternSolution.sln"),
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = CSharpToJava.CLI.Preprocessing.PassChainFactory.BuildAllDisabledToggles(
                CSharpToJava.CLI.Preprocessing.PassCatalog.BuildRegistry()),
            EnablePassIds = new[] { "pattern-desugar" },
        });
        return (result, outputRoot);
    }

    [Fact]
    public async Task Fixture_LowersTargets_AndIntermediateTestsPass()
    {
        var (result, outputRoot) = await RunAsync();
        try
        {
            Assert.True(result.Success, string.Join("\n", result.Diagnostics ?? new List<string>()));

            var libFile = Directory.EnumerateFiles(outputRoot, "PatternSwitchSamples.cs", SearchOption.AllDirectories).First();
            var lowered = await File.ReadAllTextAsync(libFile);
            Assert.DoesNotContain("case string s when", lowered);
            Assert.DoesNotContain("switch (o)", lowered);
            Assert.Contains("is string s", lowered);

            // Residual gate: parse every output tree file; no C03 target syntax remains.
            foreach (var file in Directory.EnumerateFiles(outputRoot, "*.cs", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file);
                var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetRoot();
                Assert.DoesNotContain(root.DescendantNodes(),
                    n => n is Microsoft.CodeAnalysis.CSharp.Syntax.CasePatternSwitchLabelSyntax);
                Assert.DoesNotContain(root.DescendantNodes()
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.CatchClauseSyntax>()
                        .Where(c => c.Filter != null),
                    n => true);
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }
}
```

Notes: verify `CliChainResult`'s actual diagnostic/summary member names against `CliPreprocessChain` at implementation time (the stub test in `PassStubCatalogTests` shows `result.Success` exists). If `dotnet test` on the intermediate tree needs to be asserted explicitly, use `VerifyTestsByDefault: true` on the descriptor (already set) — the chain runs it; failure surfaces via `result.Success`/diagnostics.

- [ ] **Step 6: Run the project tests**

```bash
dotnet test tests/CSharpToJava.ProjectTests -c Release --filter "FullyQualifiedName~PatternDesugarProjectTests" 2>&1 | tail -5
```

Expected: pass. First run may be slow (fixture restore).

- [ ] **Step 7: Commit**

```bash
git add tests/SamplePatternSolution/ tests/CSharpToJava.ProjectTests/
git commit -m "test(c03): SamplePatternSolution fixture with runtime behavior + project gate tests"
```

---

### Task 14: Full verification

- [ ] **Step 1: Build everything Release**

```bash
dotnet build -c Release 2>&1 | tail -3
```

Expected: `Build succeeded`, 0 warnings introduced by the new code.

- [ ] **Step 2: Run all three test projects**

```bash
dotnet test tests/CSharpToJava.Tests -c Release 2>&1 | tail -2
dotnet test tests/CSharpToJava.ProjectTests -c Release 2>&1 | tail -2
dotnet test tests/CSharpToJava.ConversionTests -c Release 2>&1 | tail -2
```

Expected: all green; ConversionTests reports exactly 1 skipped (the C37 property-pattern draft).

- [ ] **Step 3: Final commit if anything was touched during verification**

```bash
git status --short
git add -A
git commit -m "test(c03): full Release verification green"  # only if there are changes
```

---

## Self-review notes (kept from plan authoring)

1. **Spec coverage:** §4.1–4.4 → Tasks 2–6; §5 → Tasks 9–10; §6 (PD010/011/020/030) → Tasks 3/4/9; §7.1 → Tasks 7–8; §7.2 → Task 11; §7.3 → Task 13; §7.4 → Task 10/12; §7.5 → Task 14. §8 default-on evidence collection is explicitly OUT of this plan (two-step flip, `defaultEnabled: false` ships here).
2. **Known simplifications locked in the reviewed spec:** no-model constant-on-object limitation (spec §4.1); mid-chain default → PD010; typeless-catch-with-discard-designation edge; `catch (Foo _)` discard designations don't rebind.
3. **Type consistency check done:** `PatternReductionContext.NewName` is `Func<string,string>` fed by `NameScope.Next`; `PatternDesugarRewriteContext.ReductionContext()` bridges them; component interface signatures match §4.4 of the spec (`IPatternReducer` remains declared in the spec for C37 and is intentionally NOT created in C03 — the reducer dispatch is a static switch; C37 converts it to a registry without changing C03 callers).

   **Correction applied during self-review:** the spec freezes the `IPatternReducer` interface for C37, so C03 MUST ship it. If `PatternReducer` stays a static switch, C37 cannot "only add registrations". Add to Task 3: declare the interface in `PatternReducer.cs`:

```csharp
/// <summary>C37 extension point: per-pattern-kind reducers registered into PatternReducer.</summary>
internal interface IPatternReducer
{
    bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx);
    PatternReduction Reduce(PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx);
}
```

   and make the C03 kinds implement it as small internal classes registered in a static list inside `PatternReducer` (the static `Reduce` dispatch walks the registry; unknown kinds fall through to the PD010 helper). The task code above shows the switch form — when implementing, keep the public static `Reduce` signature identical but route it through the registered `IPatternReducer` list so C03 ships the frozen extension point.
4. **Rollback safety:** every task is a separate commit; the pass ships `defaultEnabled: false` so the default chain is untouched until §8 evidence is gathered.
