# Enum Preprocessor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a C# → C# preprocessing stage that rewrites all enum bitwise operations (`| & ^ ~`), compound assignments (`|= &= ^=`), and `HasFlag` calls into explicit underlying-type integer operations + `(E)` casts, eliminating the main converter's dependence on parent-node heuristics.

**Architecture:** A new `EnumPreprocessor` core class (mirroring `NullCoalesceRewriter`) with a `CSharpSyntaxRewriter`-based `EnumSyntaxRewriter` that operates bottom-up on expression nodes, querying `SemanticModel` to determine enum types and their underlying types. A `ProjectEnumPreprocessor` CLI wrapper (mirroring `ProjectNullCoalescePreprocessor`) handles project-level file iteration, dirty detection, compilation, and post-rewrite semantic verification. Integration into `ConvertProject` places the enum stage between `NullCoalesce` and `TupleToStruct`.

**Tech Stack:** C# / .NET 10, Microsoft.CodeAnalysis (Roslyn), xUnit for tests.

---

## File Structure

### Core library (`src/CSharpToJava.Core/EnumPreprocessor/`)

| File | Responsibility |
|------|----------------|
| `EnumPreprocessorOptions.cs` | `record(Verbose, Strict)` |
| `EnumPreprocessorStatistics.cs` | Statistics fields matching spec §7.2 |
| `EnumPreprocessorDiagnostic.cs` | Severity enum + diagnostic record |
| `EnumPreprocessorResult.cs` | Result record |
| `EnumPreprocessor.cs` | Public entry point: dirty check, invoke rewriter, format, post-check |
| `EnumSyntaxRewriter.cs` | `CSharpSyntaxRewriter` — visits binary/assignment/unary/invocation nodes, rewrites enum bitwise ops |

### CLI wrapper (`src/CSharpToJava.CLI/`)

| File | Responsibility |
|------|----------------|
| `ProjectEnumPreprocessor.cs` | Project-level wrapper: mirrors `ProjectNullCoalescePreprocessor` |

### CLI entry (`src/CSharpToJava.CLI/Program.cs`)

Modifications to add verb options, dispatch, `EnumPreprocessAsync` method, and `ConvertProject` pipeline integration.

### Tests (`tests/CSharpToJava.Tests/`)

| File | Responsibility |
|------|----------------|
| `EnumPreprocessorUnitTests.cs` | Unit tests for `EnumPreprocessor.Preprocess` |
| `ProjectEnumPreprocessorTests.cs` | Project-level integration tests |

### Config (`.gitignore`, `.zcodeignore`)

Minor additions (`.cs2j-enum-src/` already present in `.gitignore`; sync `.zcodeignore`).

---

## Task 1: Core Data Structures

**Files:**
- Create: `src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessorOptions.cs`
- Create: `src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessorStatistics.cs`
- Create: `src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessorDiagnostic.cs`
- Create: `src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessorResult.cs`

- [ ] **Step 1: Create `EnumPreprocessorOptions.cs`**

```csharp
namespace CSharpToJava.Core.EnumPreprocessor;

/// <param name="Verbose">If true, emit info-level diagnostics.</param>
/// <param name="Strict">If true, residual enum bitwise/HasFlag nodes produce Error instead of Warning.</param>
public sealed record EnumPreprocessorOptions(
    bool Verbose = false,
    bool Strict = false);
```

- [ ] **Step 2: Create `EnumPreprocessorStatistics.cs`**

```csharp
namespace CSharpToJava.Core.EnumPreprocessor;

public sealed class EnumPreprocessorStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int BitwiseOrRewritten;
    public int BitwiseAndRewritten;
    public int BitwiseXorRewritten;
    public int BitwiseNotRewritten;
    public int CompoundAssignRewritten;
    public int HasFlagRewritten;
    public int NullableLiftedRewritten;
    public int SkippedUnsupported;
    public int ResidualCount;
}
```

- [ ] **Step 3: Create `EnumPreprocessorDiagnostic.cs`**

```csharp
namespace CSharpToJava.Core.EnumPreprocessor;

public enum EnumPreprocessorSeverity { Warning, Error }

public sealed record EnumPreprocessorDiagnostic(
    EnumPreprocessorSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0);
```

- [ ] **Step 4: Create `EnumPreprocessorResult.cs`**

```csharp
namespace CSharpToJava.Core.EnumPreprocessor;

public sealed record EnumPreprocessorResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<EnumPreprocessorDiagnostic> Diagnostics,
    EnumPreprocessorStatistics Statistics);
```

- [ ] **Step 5: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS (no errors)

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/EnumPreprocessor/
git commit -m "feat: add enum preprocessor data structures (options, statistics, diagnostic, result)"
```

---

## Task 2: EnumSyntaxRewriter — Type Resolution Helpers

**Files:**
- Create: `src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs`

This task creates the rewriter class skeleton with all the SemanticModel-based helper methods for enum type resolution, underlying-type extraction, stable-reference checking, and cast-node construction. The actual `Visit*` overrides are added in Tasks 3–6.

- [ ] **Step 1: Create `EnumSyntaxRewriter.cs` with skeleton + helpers**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EnumPreprocessor;

/// <summary>
/// Single-pass CSharpSyntaxRewriter. Rewrites enum bitwise operations
/// (|, &amp;, ^, ~), compound assignments (|=, &amp;=, ^=), and HasFlag
/// calls into explicit underlying-type integer operations + (E) casts.
/// Only queries SemanticModel on original (pre-rewrite) nodes.
/// </summary>
internal sealed class EnumSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly EnumPreprocessorStatistics _stats;
    private readonly List<EnumPreprocessorDiagnostic> _diagnostics;
    private readonly EnumPreprocessorOptions _options;

    public int Rewrites { get; private set; }
    public int Skipped { get; private set; }

    public EnumSyntaxRewriter(
        SemanticModel? semanticModel,
        EnumPreprocessorOptions options,
        EnumPreprocessorStatistics stats,
        List<EnumPreprocessorDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    // ── Type resolution helpers ──

    /// <summary>
    /// Returns the enum type symbol (INamedTypeSymbol with TypeKind.Enum)
    /// for the given expression, or null if the expression is not an enum.
    /// Handles nullable enums (E? → E).
    /// </summary>
    private INamedTypeSymbol? GetEnumType(ExpressionSyntax expr)
    {
        if (_semanticModel == null) return null;
        var type = _semanticModel.GetTypeInfo(expr).Type;
        if (type == null) return null;

        // Unwrap nullable
        if (type is INamedTypeSymbol nts
            && nts.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && nts.TypeArguments.Length == 1
            && nts.TypeArguments[0] is INamedTypeSymbol inner
            && inner.TypeKind == TypeKind.Enum)
        {
            return inner;
        }

        if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
            return named;

        return null;
    }

    /// <summary>
    /// Returns true if the expression's static type is a nullable enum (E?).
    /// </summary>
    private bool IsNullableEnum(ExpressionSyntax expr)
    {
        if (_semanticModel == null) return false;
        var type = _semanticModel.GetTypeInfo(expr).Type;
        if (type is INamedTypeSymbol nts
            && nts.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && nts.TypeArguments.Length == 1
            && nts.TypeArguments[0] is INamedTypeSymbol inner
            && inner.TypeKind == TypeKind.Enum)
            return true;
        return false;
    }

    /// <summary>
    /// Returns the underlying type symbol (e.g. int, byte) for an enum type symbol.
    /// </summary>
    private static INamedTypeSymbol? GetUnderlyingType(INamedTypeSymbol enumType)
        => enumType.EnumUnderlyingType;

    /// <summary>
    /// Returns the SpecialType of the underlying type, or None.
    /// </summary>
    private static SpecialType GetUnderlyingSpecialType(INamedTypeSymbol enumType)
        => enumType.EnumUnderlyingType?.SpecialType ?? SpecialType.None;

    /// <summary>
    /// Returns true if the underlying type is a narrow integer type
    /// that requires unchecked() for ~ operations: byte, sbyte, short, ushort.
    /// </summary>
    private static bool IsNarrowType(INamedTypeSymbol enumType)
    {
        var st = GetUnderlyingSpecialType(enumType);
        return st is SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16;
    }

    /// <summary>
    /// Returns the SyntaxKind for the cast expression to the underlying type
    /// (e.g. (int)x, (byte)x). Uses a predefined type name.
    /// </summary>
    private static ExpressionSyntax CastToUnderlying(
        ExpressionSyntax expr, INamedTypeSymbol underlyingType)
    {
        var typeName = GetPredefinedTypeName(underlyingType.SpecialType);
        var typeSyntax = SyntaxFactory.PredefinedType(
            SyntaxFactory.Token(GetTypeKeywordKind(underlyingType.SpecialType)));
        return SyntaxFactory.CastExpression(typeSyntax, expr.WithParenthesesIfNecessary());
    }

    private static string GetPredefinedTypeName(SpecialType st) => st switch
    {
        SpecialType.System_Int32 => "int",
        SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long",
        SpecialType.System_UInt64 => "ulong",
        SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Byte => "byte",
        SpecialType.System_SByte => "sbyte",
        _ => "int",
    };

    private static SyntaxKind GetTypeKeywordKind(SpecialType st) => st switch
    {
        SpecialType.System_Int32 => SyntaxKind.IntKeyword,
        SpecialType.System_UInt32 => SyntaxKind.UIntKeyword,
        SpecialType.System_Int64 => SyntaxKind.LongKeyword,
        SpecialType.System_UInt64 => SyntaxKind.ULongKeyword,
        SpecialType.System_Int16 => SyntaxKind.ShortKeyword,
        SpecialType.System_UInt16 => SyntaxKind.UShortKeyword,
        SpecialType.System_Byte => SyntaxKind.ByteKeyword,
        SpecialType.System_SByte => SyntaxKind.SByteKeyword,
        _ => SyntaxKind.IntKeyword,
    };

    /// <summary>
    /// Creates a (E) cast expression around the given expression.
    /// </summary>
    private static ExpressionSyntax CastToEnum(
        ExpressionSyntax expr, INamedTypeSymbol enumType)
    {
        var enumName = SyntaxFactory.IdentifierName(enumType.Name);
        return SyntaxFactory.CastExpression(enumName, expr.WithParenthesesIfNecessary());
    }

    // ── Stable reference check (spec §3.9) ──

    /// <summary>
    /// Returns true if the expression is a "stable reference":
    /// local variable, parameter, field, this.f, or Type.F.
    /// The receiver chain must not contain properties/indexers/calls.
    /// </summary>
    private bool IsStableReference(ExpressionSyntax expr)
    {
        // Strip parentheses
        expr = expr.SkipParentheses();

        if (_semanticModel == null) return false;

        // Simple identifier: local / parameter / field
        if (expr is IdentifierNameSyntax)
        {
            var symbol = _semanticModel.GetSymbolInfo(expr).Symbol;
            return symbol is ILocalSymbol
                or IParameterSymbol
                or IFieldSymbol;
        }

        // Member access: this.F, Type.F, Outer.Static.F
        if (expr is MemberAccessExpressionSyntax ma)
        {
            var symbol = _semanticModel.GetSymbolInfo(expr).Symbol;
            if (symbol is not IFieldSymbol) return false;

            // The receiver chain must be stable too
            // this.F → receiver is ThisExpression → OK
            // Type.F → receiver is IdentifierName (type) → OK
            // Outer.Static.F → recurse
            return IsStableReceiver(ma.Expression);
        }

        return false;
    }

    private bool IsStableReceiver(ExpressionSyntax expr)
    {
        expr = expr.SkipParentheses();

        // this
        if (expr is ThisExpressionSyntax) return true;

        // Simple identifier (could be a type name or a local/field)
        if (expr is IdentifierNameSyntax)
        {
            var symbol = _semanticModel?.GetSymbolInfo(expr).Symbol;
            if (symbol is IFieldSymbol) return true;
            // Could be a type name — check if it's a type
            if (symbol is INamedTypeSymbol) return true;
            // Could be local/parameter — that's also stable as a receiver chain
            if (symbol is ILocalSymbol or IParameterSymbol) return true;
            return false;
        }

        // Nested member access: recurse
        if (expr is MemberAccessExpressionSyntax ma)
        {
            var symbol = _semanticModel?.GetSymbolInfo(expr).Symbol;
            // Only fields and types are stable in the receiver chain
            if (symbol is IFieldSymbol or INamedTypeSymbol)
                return IsStableReceiver(ma.Expression);
            return false;
        }

        return false;
    }

    /// <summary>
    /// Wraps an expression in unchecked(...) if the underlying type is narrow.
    /// </summary>
    private static ExpressionSyntax MaybeUnchecked(
        ExpressionSyntax expr, INamedTypeSymbol enumType)
    {
        if (!IsNarrowType(enumType)) return expr;
        return SyntaxFactory.CheckedExpression(
            SyntaxKind.UncheckedExpression, expr);
    }

    private void AddWarning(string message)
    {
        _diagnostics.Add(new EnumPreprocessorDiagnostic(
            EnumPreprocessorSeverity.Warning, message));
        Skipped++;
    }

    private void IncrementRewrite()
    {
        Rewrites++;
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs
git commit -m "feat: add EnumSyntaxRewriter skeleton with type resolution helpers"
```

---

## Task 3: EnumSyntaxRewriter — Binary Bitwise Operations

**Files:**
- Modify: `src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs`

Add `VisitBinaryExpression` override handling `|`, `&`, `^` when at least one operand is an enum type. Also handle nullable (lifted) enums.

- [ ] **Step 1: Add `VisitBinaryExpression` method to `EnumSyntaxRewriter`**

Add the following method inside the `EnumSyntaxRewriter` class, after the helper methods:

```csharp
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        // Process children first (bottom-up)
        var visitedLeft = (ExpressionSyntax?)Visit(node.Left);
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var left = visitedLeft ?? node.Left;
        var right = visitedRight ?? node.Right;

        // Only handle enum bitwise operations
        if (!node.IsKind(SyntaxKind.BitwiseOrExpression)
            && !node.IsKind(SyntaxKind.BitwiseAndExpression)
            && !node.IsKind(SyntaxKind.ExclusiveOrExpression))
            return node.Update(left, node.OperatorToken, right);

        // Determine if either operand is an enum type
        var leftEnum = GetEnumType(node.Left);
        var rightEnum = GetEnumType(node.Right);
        var enumType = leftEnum ?? rightEnum;

        if (enumType == null)
            return node.Update(left, node.OperatorToken, right);

        var underlying = GetUnderlyingType(enumType);
        if (underlying == null)
        {
            AddWarning($"Cannot resolve underlying type for enum '{enumType.Name}' in bitwise expression; skipping.");
            return node.Update(left, node.OperatorToken, right);
        }

        // Handle nullable (lifted) enums
        var leftIsNullable = IsNullableEnum(node.Left);
        var rightIsNullable = IsNullableEnum(node.Right);

        if (leftIsNullable || rightIsNullable)
        {
            return RewriteLiftedBinary(node, left, right, enumType, underlying,
                leftIsNullable, rightIsNullable);
        }

        // Non-nullable: (E)(L' | R')
        var leftCasted = MaybeCastToUnderlying(left, node.Left, enumType, underlying);
        var rightCasted = MaybeCastToUnderlying(right, node.Right, enumType, underlying);

        var innerOp = node.Kind() switch
        {
            SyntaxKind.BitwiseOrExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseOrExpression, leftCasted, SyntaxFactory.Token(SyntaxKind.BarToken), rightCasted),
            SyntaxKind.BitwiseAndExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseAndExpression, leftCasted, SyntaxFactory.Token(SyntaxKind.AmpersandToken), rightCasted),
            SyntaxKind.ExclusiveOrExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.ExclusiveOrExpression, leftCasted, SyntaxFactory.Token(SyntaxKind.CaretToken), rightCasted),
            _ => throw new InvalidOperationException(),
        };

        // Outer (E) cast — skip if parent is already (E) cast
        var result = ShouldSkipOuterCast(node)
            ? innerOp
            : CastToEnum(innerOp, enumType);

        // Update stats
        switch (node.Kind())
        {
            case SyntaxKind.BitwiseOrExpression: _stats.BitwiseOrRewritten++; break;
            case SyntaxKind.BitwiseAndExpression: _stats.BitwiseAndRewritten++; break;
            case SyntaxKind.ExclusiveOrExpression: _stats.BitwiseXorRewritten++; break;
        }
        IncrementRewrite();
        return result.WithParenthesesIfNecessary();
    }

    /// <summary>
    /// Casts the visited operand to the underlying type if its original
    /// static type was the enum type; otherwise returns it as-is.
    /// Literal 0 and default are left as-is (already int-compatible).
    /// </summary>
    private ExpressionSyntax MaybeCastToUnderlying(
        ExpressionSyntax visited,
        ExpressionSyntax original,
        INamedTypeSymbol enumType,
        INamedTypeSymbol underlying)
    {
        // If the original operand's type was the enum, we need (U) cast
        var origEnum = GetEnumType(original);
        if (origEnum != null)
            return CastToUnderlying(visited, underlying);

        // Check if original is already the underlying type
        if (_semanticModel != null)
        {
            var origType = _semanticModel.GetTypeInfo(original).Type;
            if (origType != null && origType.Equals(underlying, SymbolEqualityComparer.Default))
                return visited; // already U
        }

        // Literal 0 or default → leave as-is
        return visited;
    }

    /// <summary>
    /// Checks if the immediate parent (skipping parentheses) is a cast
    /// to the same enum type, in which case the outer (E) cast is redundant.
    /// </summary>
    private bool ShouldSkipOuterCast(ExpressionSyntax node)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax pe)
            parent = pe.Parent;

        if (parent is CastExpressionSyntax cast)
        {
            var castType = _semanticModel?.GetTypeInfo(cast.Type).Type;
            var nodeEnum = GetEnumType(node);
            return castType != null && nodeEnum != null
                && castType.Equals(nodeEnum, SymbolEqualityComparer.Default);
        }
        return false;
    }

    private SyntaxNode RewriteLiftedBinary(
        BinaryExpressionSyntax node,
        ExpressionSyntax left,
        ExpressionSyntax right,
        INamedTypeSymbol enumType,
        INamedTypeSymbol underlying,
        bool leftIsNullable,
        bool rightIsNullable)
    {
        // Both sides nullable: x.HasValue && y.HasValue ? (E?)((U)x.Value | (U)y.Value) : (E?)null
        // One side nullable: x.HasValue ? (E?)((U)x.Value | (U)R) : (E?)null

        var leftExpr = leftIsNullable
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                left,
                SyntaxFactory.IdentifierName("Value"))
            : MaybeCastToUnderlying(left, node.Left, enumType, underlying);
        if (leftIsNullable)
            leftExpr = CastToUnderlying(leftExpr, underlying);

        var rightExpr = rightIsNullable
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                right,
                SyntaxFactory.IdentifierName("Value"))
            : MaybeCastToUnderlying(right, node.Right, enumType, underlying);
        if (rightIsNullable)
            rightExpr = CastToUnderlying(rightExpr, underlying);

        // Check stability of nullable operands
        if (leftIsNullable && !IsStableReference(node.Left))
        {
            AddWarning($"Nullable enum operand '{node.Left}' is not a stable reference; skipping lifted bitwise rewrite.");
            return node.Update(left, node.OperatorToken, right);
        }
        if (rightIsNullable && !IsStableReference(node.Right))
        {
            AddWarning($"Nullable enum operand '{node.Right}' is not a stable reference; skipping lifted bitwise rewrite.");
            return node.Update(left, node.OperatorToken, right);
        }

        var opKind = node.Kind();
        var innerOp = opKind switch
        {
            SyntaxKind.BitwiseOrExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseOrExpression, leftExpr, SyntaxFactory.Token(SyntaxKind.BarToken), rightExpr),
            SyntaxKind.BitwiseAndExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseAndExpression, leftExpr, SyntaxFactory.Token(SyntaxKind.AmpersandToken), rightExpr),
            SyntaxKind.ExclusiveOrExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.ExclusiveOrExpression, leftExpr, SyntaxFactory.Token(SyntaxKind.CaretToken), rightExpr),
            _ => throw new InvalidOperationException(),
        };

        var enumName = SyntaxFactory.IdentifierName(enumType.Name);
        var nullableEnum = SyntaxFactory.NullableType(enumName);

        var hasValueCondition = rightIsNullable
            ? SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalAndExpression,
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    left,
                    SyntaxFactory.IdentifierName("HasValue")),
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    right,
                    SyntaxFactory.IdentifierName("HasValue")))
            : SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                left,
                SyntaxFactory.IdentifierName("HasValue"));

        var trueExpr = SyntaxFactory.CastExpression(
            nullableEnum,
            CastToEnum(innerOp, enumType).WithParenthesesIfNecessary());
        var falseExpr = SyntaxFactory.CastExpression(
            nullableEnum,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

        var ternary = SyntaxFactory.ConditionalExpression(
            hasValueCondition,
            trueExpr,
            falseExpr);

        switch (opKind)
        {
            case SyntaxKind.BitwiseOrExpression: _stats.BitwiseOrRewritten++; break;
            case SyntaxKind.BitwiseAndExpression: _stats.BitwiseAndRewritten++; break;
            case SyntaxKind.ExclusiveOrExpression: _stats.BitwiseXorRewritten++; break;
        }
        _stats.NullableLiftedRewritten++;
        IncrementRewrite();
        return SyntaxFactory.ParenthesizedExpression(ternary);
    }
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs
git commit -m "feat: add binary bitwise expression rewriting to EnumSyntaxRewriter"
```

---

## Task 4: EnumSyntaxRewriter — Unary Bitwise Not (~)

**Files:**
- Modify: `src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs`

Add `VisitPrefixUnaryExpression` override handling `~` on enum types, with `unchecked(...)` for narrow types.

- [ ] **Step 1: Add `VisitPrefixUnaryExpression` method**

Add the following method inside `EnumSyntaxRewriter`:

```csharp
    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.BitwiseNotExpression))
            return base.VisitPrefixUnaryExpression(node);

        var visitedOperand = (ExpressionSyntax?)Visit(node.Operand);
        var operand = visitedOperand ?? node.Operand;

        var enumType = GetEnumType(node.Operand);
        if (enumType == null)
            return node.Update(node.OperatorToken, operand);

        var underlying = GetUnderlyingType(enumType);
        if (underlying == null)
        {
            AddWarning($"Cannot resolve underlying type for enum '{enumType.Name}' in ~ expression; skipping.");
            return node.Update(node.OperatorToken, operand);
        }

        // Handle nullable enum
        if (IsNullableEnum(node.Operand))
        {
            if (!IsStableReference(node.Operand))
            {
                AddWarning($"Nullable enum operand '{node.Operand}' is not a stable reference; skipping lifted ~ rewrite.");
                return node.Update(node.OperatorToken, operand);
            }
            // ~x (x: E?) → x.HasValue ? (E?)unchecked(...) : (E?)null
            var valueAccess = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                operand,
                SyntaxFactory.IdentifierName("Value"));
            var castedValue = CastToUnderlying(valueAccess, underlying);
            var notExpr = SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.BitwiseNotExpression,
                SyntaxFactory.Token(SyntaxKind.TildeToken),
                castedValue.WithParenthesesIfNecessary());
            var narrowed = IsNarrowType(enumType)
                ? (ExpressionSyntax)SyntaxFactory.CheckedExpression(
                    SyntaxKind.UncheckedExpression, CastToUnderlying(notExpr, underlying))
                : CastToUnderlying(notExpr, underlying);
            var enumName = SyntaxFactory.IdentifierName(enumType.Name);
            var nullableEnum = SyntaxFactory.NullableType(enumName);
            var trueExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                CastToEnum(narrowed, enumType).WithParenthesesIfNecessary());
            var falseExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
            var ternary = SyntaxFactory.ConditionalExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    operand,
                    SyntaxFactory.IdentifierName("HasValue")),
                trueExpr,
                falseExpr);

            _stats.BitwiseNotRewritten++;
            _stats.NullableLiftedRewritten++;
            IncrementRewrite();
            return SyntaxFactory.ParenthesizedExpression(ternary);
        }

        // Non-nullable: ~X → (E)(~(U)X) or (E)unchecked((U)(~(U)X)) for narrow types
        var operandCasted = CastToUnderlying(operand, underlying);
        var notExpr2 = SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.BitwiseNotExpression,
            SyntaxFactory.Token(SyntaxKind.TildeToken),
            operandCasted.WithParenthesesIfNecessary());

        var result = ShouldSkipOuterCast(node)
            ? notExpr2
            : CastToEnum(notExpr2, enumType);

        // Wrap in unchecked for narrow types
        if (IsNarrowType(enumType))
        {
            result = SyntaxFactory.CheckedExpression(
                SyntaxKind.UncheckedExpression,
                result.WithParenthesesIfNecessary());
            if (!ShouldSkipOuterCast(node))
                result = CastToEnum(result, enumType);
        }

        _stats.BitwiseNotRewritten++;
        IncrementRewrite();
        return result.WithParenthesesIfNecessary();
    }
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs
git commit -m "feat: add unary bitwise not (~) rewriting to EnumSyntaxRewriter"
```

---

## Task 5: EnumSyntaxRewriter — Compound Assignment and HasFlag

**Files:**
- Modify: `src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs`

Add `VisitExpressionStatement` and `VisitAssignmentExpression` for compound assignments (`|=`, `&=`, `^=`), and `VisitInvocationExpression` for `HasFlag`.

- [ ] **Step 1: Add compound assignment handling**

Add `VisitAssignmentExpression` override (for non-statement context, e.g. inside an expression):

```csharp
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var right = visitedRight ?? node.Right;

        if (!node.IsKind(SyntaxKind.OrAssignmentExpression)
            && !node.IsKind(SyntaxKind.AndAssignmentExpression)
            && !node.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression))
            return node.Update(node.Left, node.OperatorToken, right);

        // Compound assignment to enum — only rewrite if target is stable
        var enumType = GetEnumType(node.Left);
        if (enumType == null)
            return node.Update(node.Left, node.OperatorToken, right);

        var underlying = GetUnderlyingType(enumType);
        if (underlying == null)
        {
            AddWarning($"Cannot resolve underlying type for enum '{enumType.Name}' in compound assignment; skipping.");
            return node.Update(node.Left, node.OperatorToken, right);
        }

        if (!IsStableReference(node.Left))
        {
            AddWarning($"Compound assignment target '{node.Left}' is not a stable reference; skipping rewrite.");
            return node.Update(node.Left, node.OperatorToken, right);
        }

        // Handle nullable enum compound assignment
        if (IsNullableEnum(node.Left))
        {
            // x |= R → x = x.HasValue ? (E?)((U)x.Value | R') : (E?)null
            var rightCasted = MaybeCastToUnderlying(right, node.Right, enumType, underlying);
            var leftValue = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                node.Left,
                SyntaxFactory.IdentifierName("Value"));
            var leftCasted = CastToUnderlying(leftValue, underlying);

            var opKind = node.Kind() switch
            {
                SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
                SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
                SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
                _ => throw new InvalidOperationException(),
            };
            var opToken = node.Kind() switch
            {
                SyntaxKind.OrAssignmentExpression => SyntaxFactory.Token(SyntaxKind.BarToken),
                SyntaxKind.AndAssignmentExpression => SyntaxFactory.Token(SyntaxKind.AmpersandToken),
                SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxFactory.Token(SyntaxKind.CaretToken),
                _ => throw new InvalidOperationException(),
            };

            var innerOp = SyntaxFactory.BinaryExpression(opKind, leftCasted, opToken, rightCasted);
            var enumName = SyntaxFactory.IdentifierName(enumType.Name);
            var nullableEnum = SyntaxFactory.NullableType(enumName);
            var trueExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                CastToEnum(innerOp, enumType).WithParenthesesIfNecessary());
            var falseExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

            var ternary = SyntaxFactory.ConditionalExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    node.Left,
                    SyntaxFactory.IdentifierName("HasValue")),
                trueExpr,
                falseExpr);

            _stats.CompoundAssignRewritten++;
            _stats.NullableLiftedRewritten++;
            IncrementRewrite();
            return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                node.Left,
                SyntaxFactory.ParenthesizedExpression(ternary));
        }

        // Non-nullable: x |= R → x = (E)((U)x | R')
        var leftCasted2 = CastToUnderlying(node.Left, underlying);
        var rightCasted2 = MaybeCastToUnderlying(right, node.Right, enumType, underlying);

        var (binKind, binToken) = node.Kind() switch
        {
            SyntaxKind.OrAssignmentExpression => (SyntaxKind.BitwiseOrExpression, SyntaxFactory.Token(SyntaxKind.BarToken)),
            SyntaxKind.AndAssignmentExpression => (SyntaxKind.BitwiseAndExpression, SyntaxFactory.Token(SyntaxKind.AmpersandToken)),
            SyntaxKind.ExclusiveOrAssignmentExpression => (SyntaxKind.ExclusiveOrExpression, SyntaxFactory.Token(SyntaxKind.CaretToken)),
            _ => throw new InvalidOperationException(),
        };

        var binExpr = SyntaxFactory.BinaryExpression(binKind, leftCasted2, binToken, rightCasted2);
        var castedResult = CastToEnum(binExpr, enumType);

        _stats.CompoundAssignRewritten++;
        IncrementRewrite();
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            node.Left,
            castedResult.WithParenthesesIfNecessary());
    }
```

- [ ] **Step 2: Add `HasFlag` invocation handling**

Add `VisitInvocationExpression` override:

```csharp
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Process arguments first (bottom-up)
        var visitedArgs = Visit(node.ArgumentList);
        var args = visitedArgs as ArgumentListSyntax ?? node.ArgumentList;

        // Check if this is a HasFlag call: x.HasFlag(f)
        if (node.Expression is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.ValueText == "HasFlag"
            && args.Arguments.Count == 1)
        {
            var receiverEnum = GetEnumType(ma.Expression);
            if (receiverEnum == null)
                return node.Update(node.Expression, args);

            var argExpr = args.Arguments[0].Expression;
            var argEnum = GetEnumType(argExpr);

            // Both must be the same enum type
            if (argEnum == null
                || !argEnum.Equals(receiverEnum, SymbolEqualityComparer.Default))
            {
                AddWarning($"HasFlag argument type does not match receiver enum type; skipping rewrite.");
                return node.Update(node.Expression, args);
            }

            var underlying = GetUnderlyingType(receiverEnum);
            if (underlying == null)
            {
                AddWarning($"Cannot resolve underlying type for enum '{receiverEnum.Name}' in HasFlag; skipping.");
                return node.Update(node.Expression, args);
            }

            // Handle nullable receiver
            if (IsNullableEnum(ma.Expression))
            {
                AddWarning($"Nullable receiver in HasFlag call ('?.HasFlag' or 'E?.HasFlag'); skipping rewrite.");
                return node.Update(node.Expression, args);
            }

            // Rewrite: x.HasFlag(f) → ((U)x & (U)f) == (U)f
            var visitedReceiver = (ExpressionSyntax?)Visit(ma.Expression);
            var receiver = visitedReceiver ?? ma.Expression;
            var visitedArg = (ExpressionSyntax?)Visit(argExpr);
            var arg = visitedArg ?? argExpr;

            var receiverCasted = MaybeCastToUnderlying(receiver, ma.Expression, receiverEnum, underlying);
            var argCasted = MaybeCastToUnderlying(arg, argExpr, receiverEnum, underlying);

            var andExpr = SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseAndExpression,
                receiverCasted,
                SyntaxFactory.Token(SyntaxKind.AmpersandToken),
                argCasted);
            var eqExpr = SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                andExpr,
                SyntaxFactory.Token(SyntaxKind.EqualsEqualsToken),
                MaybeCastToUnderlying(arg, argExpr, receiverEnum, underlying));

            _stats.HasFlagRewritten++;
            IncrementRewrite();
            return SyntaxFactory.ParenthesizedExpression(eqExpr);
        }

        return node.Update(node.Expression, args);
    }
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/EnumPreprocessor/EnumSyntaxRewriter.cs
git commit -m "feat: add compound assignment and HasFlag rewriting to EnumSyntaxRewriter"
```

---

## Task 6: EnumPreprocessor — Public Entry Point

**Files:**
- Create: `src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessor.cs`

- [ ] **Step 1: Create `EnumPreprocessor.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.EnumPreprocessor;

/// <summary>
/// C# enum bitwise preprocessor public entry point. Rewrites enum bitwise
/// operations (|, &amp;, ^, ~), compound assignments (|=, &amp;=, ^=), and
/// HasFlag calls into explicit underlying-type integer operations + (E) casts.
/// Clean files are byte-level preserved. Rewritten output is valid C#.
/// </summary>
public sealed class EnumPreprocessor
{
    public EnumPreprocessorResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        EnumPreprocessorOptions? options = null)
    {
        options ??= new EnumPreprocessorOptions();
        var stats = new EnumPreprocessorStatistics { FilesScanned = 1 };
        var diagnostics = new List<EnumPreprocessorDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Dirty check: no bitwise/HasFlag nodes → byte-level copy
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new EnumPreprocessorResult(sourceCode, false, diagnostics, stats);
        }

        // Pass: rewrite expressions
        var rewriter = new EnumSyntaxRewriter(
            semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (rewriter.Rewrites == 0)
        {
            // Dirty syntax but no semantic enum bitwise ops (e.g. int bitwise)
            stats.FilesDirtyNoRewrite = 1;
            return new EnumPreprocessorResult(sourceCode, false, diagnostics, stats);
        }

        // Format
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        // Post-check: semantic-level residual enum ops
        var residualCount = CountResidualEnumOps(code, semanticModel, root);
        stats.ResidualCount = residualCount;

        if (residualCount > 0)
        {
            var severity = options.Strict
                ? EnumPreprocessorSeverity.Error
                : EnumPreprocessorSeverity.Warning;
            diagnostics.Add(new EnumPreprocessorDiagnostic(
                severity,
                $"{residualCount} residual enum bitwise/HasFlag node(s) after preprocessing."));
        }

        return new EnumPreprocessorResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// Dirty check: syntax tree contains any bitwise operators, compound
    /// assignments, prefix ~, or HasFlag calls. This is intentionally a
    /// superset of enum operations (int bitwise also matches).
    /// </summary>
    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            (n is BinaryExpressionSyntax b &&
                (b.IsKind(SyntaxKind.BitwiseOrExpression)
                 || b.IsKind(SyntaxKind.BitwiseAndExpression)
                 || b.IsKind(SyntaxKind.ExclusiveOrExpression)))
            || (n is AssignmentExpressionSyntax a &&
                (a.IsKind(SyntaxKind.OrAssignmentExpression)
                 || a.IsKind(SyntaxKind.AndAssignmentExpression)
                 || a.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression)))
            || (n is PrefixUnaryExpressionSyntax p
                && p.IsKind(SyntaxKind.BitwiseNotExpression))
            || (n is InvocationExpressionSyntax i && IsHasFlagCall(i)));

    private static bool IsHasFlagCall(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.ValueText == "HasFlag"
            && invocation.ArgumentList.Arguments.Count == 1)
            return true;
        return false;
    }

    /// <summary>
    /// Post-check: count residual enum bitwise/HasFlag nodes in the output.
    /// Uses semantic analysis on the rewritten tree to distinguish enum
    /// operations from int operations.
    /// </summary>
    private static int CountResidualEnumOps(
        string code,
        SemanticModel? originalModel,
        CompilationUnitSyntax originalRoot)
    {
        if (originalModel == null)
            return 0;

        // Parse the output and create a new compilation to get a semantic model
        var newTree = CSharpSyntaxTree.ParseText(code, path: "enum-output.cs");
        var newCompilation = originalModel.Compilation.ReplaceSyntaxTree(
            originalModel.SyntaxTree, newTree);
        var newModel = newCompilation.GetSemanticModel(newTree);
        var newRoot = (CompilationUnitSyntax)newTree.GetRoot();

        int count = 0;

        foreach (var node in newRoot.DescendantNodes())
        {
            if (node is BinaryExpressionSyntax b
                && (b.IsKind(SyntaxKind.BitwiseOrExpression)
                    || b.IsKind(SyntaxKind.BitwiseAndExpression)
                    || b.IsKind(SyntaxKind.ExclusiveOrExpression)))
            {
                var leftType = newModel.GetTypeInfo(b.Left).Type;
                var rightType = newModel.GetTypeInfo(b.Right).Type;
                if (IsEnumType(leftType) || IsEnumType(rightType))
                    count++;
            }
            else if (node is AssignmentExpressionSyntax a
                && (a.IsKind(SyntaxKind.OrAssignmentExpression)
                    || a.IsKind(SyntaxKind.AndAssignmentExpression)
                    || a.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression)))
            {
                var leftType = newModel.GetTypeInfo(a.Left).Type;
                if (IsEnumType(leftType))
                    count++;
            }
            else if (node is PrefixUnaryExpressionSyntax p
                && p.IsKind(SyntaxKind.BitwiseNotExpression))
            {
                var operandType = newModel.GetTypeInfo(p.Operand).Type;
                if (IsEnumType(operandType))
                    count++;
            }
            else if (node is InvocationExpressionSyntax i && IsHasFlagCall(i))
            {
                if (i.Expression is MemberAccessExpressionSyntax ma)
                {
                    var receiverType = newModel.GetTypeInfo(ma.Expression).Type;
                    if (IsEnumType(receiverType))
                        count++;
                }
            }
        }

        return count;
    }

    private static bool IsEnumType(ITypeSymbol? type)
    {
        if (type == null) return false;
        if (type is INamedTypeSymbol nts)
        {
            if (nts.TypeKind == TypeKind.Enum) return true;
            if (nts.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
                && nts.TypeArguments.Length == 1
                && nts.TypeArguments[0] is INamedTypeSymbol inner
                && inner.TypeKind == TypeKind.Enum)
                return true;
        }
        return false;
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessor.cs
git commit -m "feat: add EnumPreprocessor public entry point with dirty check and post-verification"
```

---

## Task 7: ProjectEnumPreprocessor CLI Wrapper

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectEnumPreprocessor.cs`

- [ ] **Step 1: Create `ProjectEnumPreprocessor.cs`**

```csharp
using CSharpToJava.Core.EnumPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI;

internal sealed class ProjectEnumPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public EnumPreprocessorOptions Options { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectEnumPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required EnumPreprocessorStatistics Statistics { get; init; }
    public required IReadOnlyList<EnumPreprocessorDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectEnumPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-enum-src";

    public static async Task<ProjectEnumPreprocessResult> PreprocessAsync(
        ProjectEnumPreprocessRequest request)
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
                    $"Enum preprocess source directory already exists: {intermediateRoot}. " +
                    "Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<EnumPreprocessorDiagnostic>();
        var statistics = new EnumPreprocessorStatistics();
        var rewriter = new EnumPreprocessor();
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
            sourceRoot, intermediateRoot, "[enum]", request.Verbose);

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
            "enum-preprocess",
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
                var status = result.Changed ? "transformed"
                    : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                    : "dirty-no-rewrite (copy)";
                Console.WriteLine($"[enum] {status}: {relativePath}");
            }
        }

        // Residual detection (accumulated from per-file post-checks)
        if (statistics.ResidualCount > 0)
        {
            var severity = request.Strict
                ? EnumPreprocessorSeverity.Error
                : EnumPreprocessorSeverity.Warning;
            diagnostics.Add(new EnumPreprocessorDiagnostic(
                severity,
                $"{statistics.ResidualCount} residual enum bitwise/HasFlag node(s) remaining after preprocessing."));
        }

        // Restore + verify build/tests
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[enum]");
            if (request.Verbose)
                Console.WriteLine($"Restore exit code: {restoreExitCode}");

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new EnumPreprocessorDiagnostic(
                    EnumPreprocessorSeverity.Warning,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
            }

            await VerifyBuildAndTestsAsync(restoreTarget, projectGraph, exclusionInfo,
                request, diagnostics);
            DeleteBuildOutputDirectories(intermediateRoot);
        }

        return new ProjectEnumPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == EnumPreprocessorSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static async Task VerifyBuildAndTestsAsync(
        string restoreTarget,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        ProjectEnumPreprocessRequest request,
        List<EnumPreprocessorDiagnostic> diagnostics)
    {
        if (!request.VerifyBuild && !request.VerifyTests)
            return;

        Console.WriteLine($"[enum] Verifying build: dotnet build \"{restoreTarget}\" -c Release");
        var buildExit = await RunDotNetCommandAsync("build", restoreTarget, request.Verbose, "Release");
        if (buildExit != 0)
        {
            diagnostics.Add(new EnumPreprocessorDiagnostic(
                EnumPreprocessorSeverity.Error,
                $"dotnet build failed with exit code {buildExit} after enum preprocessing."));
            return;
        }

        if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
        {
            Console.WriteLine($"[enum] Verifying tests: dotnet test \"{restoreTarget}\" -c Release");
            var testExit = await RunDotNetCommandAsync("test", restoreTarget, request.Verbose, "Release");
            if (testExit != 0)
            {
                diagnostics.Add(new EnumPreprocessorDiagnostic(
                    EnumPreprocessorSeverity.Error,
                    $"dotnet test failed with exit code {testExit} after enum preprocessing."));
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
        EnumPreprocessorStatistics target,
        EnumPreprocessorStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        target.BitwiseOrRewritten += source.BitwiseOrRewritten;
        target.BitwiseAndRewritten += source.BitwiseAndRewritten;
        target.BitwiseXorRewritten += source.BitwiseXorRewritten;
        target.BitwiseNotRewritten += source.BitwiseNotRewritten;
        target.CompoundAssignRewritten += source.CompoundAssignRewritten;
        target.HasFlagRewritten += source.HasFlagRewritten;
        target.NullableLiftedRewritten += source.NullableLiftedRewritten;
        target.SkippedUnsupported += source.SkippedUnsupported;
        target.ResidualCount += source.ResidualCount;
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.CLI/ProjectEnumPreprocessor.cs
git commit -m "feat: add ProjectEnumPreprocessor CLI wrapper mirroring ProjectNullCoalescePreprocessor"
```

---

## Task 8: CLI Verb Integration — Program.cs

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

Add `EnumPreprocessVerbOptions` class, register in parser, add dispatch, add `EnumPreprocessAsync` method.

- [ ] **Step 1: Add `using` directive**

In the `using` block at the top of `Program.cs`, after the existing `using CSharpToJava.Core.NullCoalesceRewriter;` line, add:

```csharp
using CSharpToJava.Core.EnumPreprocessor;
```

- [ ] **Step 2: Register in parser and add dispatch**

In `MainImpl`, update the `ParseArguments` call to add `EnumPreprocessVerbOptions`:

Find:
```csharp
return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions, ForeachDeconstructionVerbOptions, NullCoalesceVerbOptions, DelegatePreprocessVerbOptions>(args)
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
        (DelegatePreprocessVerbOptions opts) => DelegatePreprocessAsync(opts),
        errs => Task.FromResult(1)
    );
```

Replace with (adding `EnumPreprocessVerbOptions` to both the type list and the dispatch):

```csharp
return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions, ForeachDeconstructionVerbOptions, NullCoalesceVerbOptions, DelegatePreprocessVerbOptions, EnumPreprocessVerbOptions>(args)
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
        (DelegatePreprocessVerbOptions opts) => DelegatePreprocessAsync(opts),
        (EnumPreprocessVerbOptions opts) => EnumPreprocessAsync(opts),
        errs => Task.FromResult(1)
    );
```

- [ ] **Step 3: Add `EnumPreprocessVerbOptions` class**

Add this after the `DelegatePreprocessVerbOptions` class definition (at the end of `Program.cs` before the closing namespace):

```csharp
[Verb("enum-preprocess", HelpText = "Rewrite enum bitwise operations, compound assignments, and HasFlag to explicit integer operations + (E) casts (single-file or project mode)")]
class EnumPreprocessVerbOptions
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
    [Option("strict", Default = false, HelpText = "Treat residual enum bitwise/HasFlag as errors instead of warnings")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after enum preprocessing")]
    public bool NoVerifyBuild { get; set; }
    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after enum preprocessing")]
    public bool NoVerifyTests { get; set; }
}
```

- [ ] **Step 4: Add `EnumPreprocessAsync` method**

Add this method inside the `Program` class (after `DelegatePreprocessAsync`):

```csharp
    private static async Task<int> EnumPreprocessAsync(EnumPreprocessVerbOptions opts)
    {
        var coreOptions = new EnumPreprocessorOptions(Verbose: opts.Verbose, Strict: opts.Strict);
        var rewriter = new EnumPreprocessor();

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
                "enum-preprocess-standalone",
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

            return result.Diagnostics.Any(d => d.Severity == EnumPreprocessorSeverity.Error) ? 1 : 0;
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

        var preprocessResult = await ProjectEnumPreprocessor.PreprocessAsync(
            new ProjectEnumPreprocessRequest
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
            $"enum: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.FilesDirtyNoRewrite} dirty-no-rewrite, " +
            $"{preprocessResult.Statistics.BitwiseOrRewritten + preprocessResult.Statistics.BitwiseAndRewritten + preprocessResult.Statistics.BitwiseXorRewritten} bitwise, " +
            $"{preprocessResult.Statistics.BitwiseNotRewritten} ~, " +
            $"{preprocessResult.Statistics.CompoundAssignRewritten} compound, " +
            $"{preprocessResult.Statistics.HasFlagRewritten} HasFlag, " +
            $"{preprocessResult.Statistics.NullableLiftedRewritten} lifted, " +
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

- [ ] **Step 5: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.CLI/Program.cs
git commit -m "feat: add enum-preprocess CLI verb and EnumPreprocessAsync method"
```

---

## Task 9: ConvertProject Pipeline Integration

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

Add `--no-enum-preprocess` option to `ConvertProjectOptions`, add fingerprint token, add preserved directory, insert pipeline block.

- [ ] **Step 1: Add `--no-enum-preprocess` option to `ConvertProjectOptions`**

In `ConvertProjectOptions`, after the `NoDelegatePreprocess` option, add:

```csharp
    [Option("no-enum-preprocess", Default = false,
        HelpText = "Disable the default enum bitwise / HasFlag preprocessing step")]
    public bool NoEnumPreprocess { get; set; }
```

And add the convenience property (near the other `=>` properties):

```csharp
    public bool EnumPreprocess => !NoEnumPreprocess;
```

- [ ] **Step 2: Add fingerprint token**

In `GetProjectConversionOptionTokens`, after the `delegate-preprocess` token, add:

```csharp
            $"enum-preprocess={opts.EnumPreprocess}",
```

- [ ] **Step 3: Add preserved destination directory**

In `GetPreservedDestinationDirectories`, after the `DelegatePreprocess` block, add:

```csharp
        if (opts.EnumPreprocess)
        {
            preserved.Add(ProjectEnumPreprocessor.IntermediateDirectoryName);
        }
```

- [ ] **Step 4: Insert enum preprocessor block in `ConvertProject`**

After the `NullCoalesce` block (`opts.Source = nullCoalesceResult.PreprocessedSourcePath;` and related lines) and before the `DelegatePreprocess` block (`if (opts.DelegatePreprocess)`), insert:

```csharp
            if (opts.EnumPreprocess)
            {
                var enumResult = await ProjectEnumPreprocessor.PreprocessAsync(
                    new ProjectEnumPreprocessRequest
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
                    $"enum: {enumResult.Statistics.FilesTransformed} transformed, " +
                    $"{enumResult.Statistics.FilesSkippedClean} clean, " +
                    $"{enumResult.Statistics.FilesDirtyNoRewrite} dirty-no-rewrite, " +
                    $"{enumResult.Statistics.BitwiseOrRewritten + enumResult.Statistics.BitwiseAndRewritten + enumResult.Statistics.BitwiseXorRewritten} bitwise, " +
                    $"{enumResult.Statistics.BitwiseNotRewritten} ~, " +
                    $"{enumResult.Statistics.CompoundAssignRewritten} compound, " +
                    $"{enumResult.Statistics.HasFlagRewritten} HasFlag, " +
                    $"{enumResult.Statistics.NullableLiftedRewritten} lifted, " +
                    $"{enumResult.Statistics.ResidualCount} residual");

                foreach (var diagnostic in enumResult.Diagnostics)
                {
                    var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
                    Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
                }

                if (!enumResult.Success)
                    return 1;

                opts.Source = enumResult.PreprocessedSourcePath;
                opts.FingerprintSourcePath ??= enumResult.OriginalSourcePath;
                opts.FingerprintInputRoot ??= enumResult.SourceRoot;
            }
```

**Important**: The insertion point is after the `NullCoalesce` `if` block (which ends with `opts.FingerprintInputRoot ??= nullCoalesceResult.SourceRoot; }`) and before the `DelegatePreprocess` `if` block (`if (opts.DelegatePreprocess)`). If the NullCoalesce block is not the immediately preceding block, search for `opts.FingerprintInputRoot ??= nullCoalesceResult.SourceRoot;` and insert after the closing brace of that `if` block.

- [ ] **Step 5: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.CLI/Program.cs
git commit -m "feat: integrate enum preprocessor into convert-project pipeline"
```

---

## Task 10: Update .zcodeignore

**Files:**
- Modify: `.zcodeignore`

- [ ] **Step 1: Sync .zcodeignore with .gitignore**

The `.gitignore` already has `.cs2j-enum-src/` and other intermediate dirs. The `.zcodeignore` is missing several. After the `.cs2j-local-fn-src/` line, add the missing entries to match `.gitignore`:

Find:
```
.cs2j-local-fn-src/
.cs2j-test-output/
```

Replace with:
```
.cs2j-local-fn-src/
.cs2j-tuple-src/
.cs2j-foreach-deconstruction-src/
.cs2j-null-coalesce-src/
.cs2j-delegate-src/
.cs2j-enum-src/
.cs2j-record-readonly-src/
.cs2j-array-src/
.cs2j-test-output/
```

- [ ] **Step 2: Commit**

```bash
git add .zcodeignore
git commit -m "chore: sync .zcodeignore with .gitignore for cs2j intermediate directories"
```

---

## Task 11: Unit Tests — Binary Bitwise Operations

**Files:**
- Create: `tests/CSharpToJava.Tests/EnumPreprocessorUnitTests.cs`

- [ ] **Step 1: Create test file with binary bitwise tests**

```csharp
using CSharpToJava.Core.EnumPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class EnumPreprocessorUnitTests
{
    private static EnumPreprocessorResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("enum-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new EnumPreprocessor().Preprocess(source, model);
    }

    [Fact]
    public void Clean_File_Is_Byte_Level_Preserved()
    {
        var src = """
        class C {
            void M() {
                int a = 1;
                System.Console.WriteLine(a);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Dirty_But_No_Enum_Bitwise_Is_Byte_Preserved()
    {
        var src = """
        class C {
            void M() {
                int a = 1 | 2;
                int b = a & 0xFF;
                int c = ~a;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.True(result.Statistics.FilesDirtyNoRewrite > 0);
    }

    [Fact]
    public void Simple_Enum_Or_Is_Rewritten()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("(int)E.B", result.OutputCode);
        Assert.Contains("|", result.OutputCode);
    }

    [Fact]
    public void Simple_Enum_And_Is_Rewritten()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A & E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("&", result.OutputCode);
    }

    [Fact]
    public void Simple_Enum_Xor_Is_Rewritten()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A ^ E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("^", result.OutputCode);
    }

    [Fact]
    public void Enum_Or_With_Zero_Literal_No_Cast_On_Zero()
    {
        var src = """
        enum E { A = 1 }
        class C {
            void M() {
                E x = E.A | 0;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(int)E.A", result.OutputCode);
        // 0 should not have (int) cast added
        Assert.DoesNotContain("(int)0", result.OutputCode);
    }

    [Fact]
    public void Enum_Remove_Flag_Pattern_X_And_Not_A()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                E y = x & ~E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)x", result.OutputCode);
        Assert.Contains("~(int)E.A", result.OutputCode);
    }

    [Fact]
    public void Nested_Bitwise_Parenthesized()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2, C = 4 }
        class C {
            void M(E x, E y, E z) {
                E r = (x | y) & ~z;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)x", result.OutputCode);
        Assert.Contains("(int)y", result.OutputCode);
        Assert.Contains("~(int)z", result.OutputCode);
    }

    [Fact]
    public void Multiple_Flags_Chained_Or()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2, C = 4 }
        class C {
            void M() {
                E x = E.A | E.B | E.C;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("(int)E.B", result.OutputCode);
        Assert.Contains("(int)E.C", result.OutputCode);
    }

    [Fact]
    public void Parent_Already_Enum_Cast_No_Double_Cast()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = (E)(E.A | E.B);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Should not produce (E)((E)(...))
        Assert.DoesNotContain("(E)((E)", result.OutputCode);
        Assert.Contains("(E)((int)E.A", result.OutputCode);
    }

    [Fact]
    public void Byte_Enum_Not_Has_Unchecked()
    {
        var src = """
        [System.Flags]
        enum FB : byte { A = 1, B = 2 }
        class C {
            void M(FB x) {
                FB y = x & ~FB.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(byte)", result.OutputCode);
        Assert.Contains("unchecked", result.OutputCode);
    }

    [Fact]
    public void Int_Enum_Not_Has_No_Unchecked()
    {
        var src = """
        [System.Flags]
        enum E { A = 1 }
        class C {
            void M(E x) {
                E y = x & ~E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("unchecked", result.OutputCode);
    }

    [Fact]
    public void Uint_Enum_Uses_Uint_Cast()
    {
        var src = """
        [System.Flags]
        enum E : uint { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(uint)E.A", result.OutputCode);
        Assert.Contains("(uint)E.B", result.OutputCode);
    }

    [Fact]
    public void Long_Enum_Uses_Long_Cast()
    {
        var src = """
        [System.Flags]
        enum E : long { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(long)E.A", result.OutputCode);
    }

    [Fact]
    public void NonEnum_Custom_Struct_Or_Not_Rewritten()
    {
        var src = """
        struct S {
            public static S operator |(S a, S b) => a;
            void M() {
                S a = new S();
                S b = new S();
                S c = a | b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
Expected: PASS

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~EnumPreprocessorUnitTests" --no-build -v n`
Expected: All tests PASS (some may fail if rewriter has bugs — fix and re-run)

- [ ] **Step 4: Commit**

```bash
git add tests/CSharpToJava.Tests/EnumPreprocessorUnitTests.cs
git commit -m "test: add unit tests for enum binary bitwise operations"
```

---

## Task 12: Unit Tests — Compound Assignment, HasFlag, Nullable

**Files:**
- Modify: `tests/CSharpToJava.Tests/EnumPreprocessorUnitTests.cs`

- [ ] **Step 1: Add compound assignment tests**

Append the following tests to the `EnumPreprocessorUnitTests` class:

```csharp
    [Fact]
    public void Compound_Or_Assignment_Local_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A;
                x |= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("x = (E)((int)x | (int)E.B)", result.OutputCode);
    }

    [Fact]
    public void Compound_And_Assignment_Local_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                x &= E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("x = (E)((int)x & (int)E.A)", result.OutputCode);
    }

    [Fact]
    public void Compound_Xor_Assignment_Parameter_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                x ^= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("x = (E)((int)x ^ (int)E.B)", result.OutputCode);
    }

    [Fact]
    public void Compound_Assignment_Property_Not_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            public E Prop { get; set; }
            void M() {
                Prop |= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        // Should NOT rewrite — property target is not stable
        Assert.False(result.Changed);
    }

    [Fact]
    public void Compound_Assignment_This_Field_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            E _f;
            void M() {
                _f |= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("= (E)((int)_f | (int)E.B)", result.OutputCode);
    }

    [Fact]
    public void HasFlag_Rewritten_To_Mask_Equality()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                bool b = x.HasFlag(E.A);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(int)x", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("==", result.OutputCode);
        Assert.DoesNotContain("HasFlag", result.OutputCode);
    }

    [Fact]
    public void HasFlag_Cross_Type_Not_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1 }
        [System.Flags]
        enum F { A = 1 }
        class C {
            void M(E x) {
                bool b = x.HasFlag(F.A);
            }
        }
        """;
        var result = Preprocess(src);
        // Cross-type HasFlag should not be rewritten
        Assert.Contains("HasFlag", result.OutputCode);
    }

    [Fact]
    public void Nullable_Enum_Or_Lifted_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E? x) {
                E? y = x | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("HasValue", result.OutputCode);
        Assert.Contains("(E?)", result.OutputCode);
    }

    [Fact]
    public void Nullable_Enum_Tilde_Lifted_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E : byte { A = 1 }
        class C {
            void M(E? x) {
                E? y = ~x;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("HasValue", result.OutputCode);
        Assert.Contains("unchecked", result.OutputCode);
        Assert.Contains("(byte)", result.OutputCode);
    }

    [Fact]
    public void Nullable_Enum_Property_Operand_Not_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            public E? Prop { get; set; }
            void M() {
                E? y = Prop | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        // Property operand is not stable — should skip
        Assert.False(result.Changed);
    }
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~EnumPreprocessorUnitTests" -v n`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/EnumPreprocessorUnitTests.cs
git commit -m "test: add unit tests for compound assignment, HasFlag, and nullable enums"
```

---

## Task 13: Unit Tests — Constant Context and Residual Check

**Files:**
- Modify: `tests/CSharpToJava.Tests/EnumPreprocessorUnitTests.cs`

- [ ] **Step 1: Add constant context and residual tests**

Append the following tests:

```csharp
    [Fact]
    public void Enum_Member_Initializer_Rewritten()
    {
        var src = """
        enum E {
            A = 1,
            B = 2,
            AB = A | B
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("(int)E.B", result.OutputCode);
        Assert.Contains("(E)", result.OutputCode);
    }

    [Fact]
    public void Const_Enum_Rewritten()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            const E C = E.A | E.B;
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)((int)E.A | (int)E.B)", result.OutputCode);
    }

    [Fact]
    public void Residual_Count_Is_Zero_After_Rewrite()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2, C = 4 }
        class C {
            void M(E x) {
                E y = x | E.B;
                E z = y & ~E.A;
                bool b = x.HasFlag(E.C);
                x ^= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    [Fact]
    public void Statistics_Track_Bitwise_Counts()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                E a = x | E.A;
                E b = x & E.A;
                E c = x ^ E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.True(result.Statistics.BitwiseOrRewritten >= 1);
        Assert.True(result.Statistics.BitwiseAndRewritten >= 1);
        Assert.True(result.Statistics.BitwiseXorRewritten >= 1);
    }

    [Fact]
    public void Statistics_Track_HasFlag_And_Compound()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                bool b = x.HasFlag(E.A);
                x |= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.True(result.Statistics.HasFlagRewritten >= 1);
        Assert.True(result.Statistics.CompoundAssignRewritten >= 1);
    }
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~EnumPreprocessorUnitTests" -v n`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/EnumPreprocessorUnitTests.cs
git commit -m "test: add constant context and residual check unit tests"
```

---

## Task 14: Project-Level Integration Tests

**Files:**
- Create: `tests/CSharpToJava.Tests/ProjectEnumPreprocessorTests.cs`

- [ ] **Step 1: Create project-level tests**

```csharp
using CSharpToJava.CLI;
using CSharpToJava.Core.EnumPreprocessor;
using Xunit;

namespace CSharpToJava.Tests;

public class ProjectEnumPreprocessorTests
{
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
        catch
        {
            return false;
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-enum-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void IntermediateDirectoryName_Is_Correct()
    {
        Assert.Equal(".cs2j-enum-src", ProjectEnumPreprocessor.IntermediateDirectoryName);
    }

    [Fact]
    public async Task Single_Project_Enum_Preprocess_Succeeds()
    {
        if (!IsDotNetAvailable()) return;

        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            var projectDir = Path.Combine(sourceRoot, "EnumLib");
            Directory.CreateDirectory(projectDir);

            var csprojPath = Path.Combine(projectDir, "EnumLib.csproj");
            await File.WriteAllTextAsync(csprojPath,
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");

            var csPath = Path.Combine(projectDir, "Enums.cs");
            await File.WriteAllTextAsync(csPath, """
                using System;

                [Flags]
                public enum Permissions : int
                {
                    None = 0,
                    Read = 1,
                    Write = 2,
                    Execute = 4
                }

                [Flags]
                public enum ByteFlags : byte
                {
                    A = 1,
                    B = 2,
                    C = 4
                }

                public class Service
                {
                    public Permissions Current { get; set; }

                    public void Run(Permissions p)
                    {
                        var combined = p | Permissions.Write;
                        var removed = p & ~Permissions.Read;
                        bool canWrite = p.HasFlag(Permissions.Write);
                        p |= Permissions.Execute;
                    }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            Directory.CreateDirectory(destinationRoot);

            var result = await ProjectEnumPreprocessor.PreprocessAsync(
                new ProjectEnumPreprocessRequest
                {
                    SourcePath = csprojPath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    Verbose = true,
                    FilterUnsupportedProjects = true,
                    VerifyBuild = true,
                    VerifyTests = false,
                    Strict = false,
                });

            Assert.True(result.Success,
                string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.FilePath}: {d.Message}")));
            Assert.True(result.Statistics.FilesTransformed >= 1);
            Assert.Equal(0, result.Statistics.ResidualCount);

            // Verify intermediate directory exists
            Assert.True(Directory.Exists(result.IntermediateRoot));

            // Verify output file was written
            var outputFile = Path.Combine(result.IntermediateRoot, "EnumLib", "Enums.cs");
            Assert.True(File.Exists(outputFile));

            var outputContent = await File.ReadAllTextAsync(outputFile);
            Assert.Contains("(int)Permissions", outputContent);
            Assert.Contains("(byte)ByteFlags", outputContent);
            Assert.DoesNotContain("HasFlag", outputContent);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~ProjectEnumPreprocessorTests" -v n`
Expected: Tests PASS (may be slow due to dotnet build verification)

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/ProjectEnumPreprocessorTests.cs
git commit -m "test: add project-level integration tests for enum preprocessor"
```

---

## Task 15: Update ProjectPreprocessorsConsistentTests

**Files:**
- Modify: `tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs`

- [ ] **Step 1: Add enum preprocessor consistency assertion**

After the `record-readonly` assertion block (after `Assert.Equal(".cs2j-record-readonly-src", ProjectRecordReadOnlyPreprocessor.IntermediateDirectoryName);`), add:

```csharp
            // enum preprocessor — verify type exists and intermediate name is correct
            var enumPreprocessorType = typeof(ProjectEnumPreprocessor);
            Assert.NotNull(enumPreprocessorType);
            Assert.Equal(".cs2j-enum-src",
                ProjectEnumPreprocessor.IntermediateDirectoryName);
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~ProjectPreprocessorsConsistentTests" -v n`
Expected: Tests PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs
git commit -m "test: add enum preprocessor to ProjectPreprocessorsConsistentTests"
```

---

## Task 16: Full Build and Test Run

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build -c Release`
Expected: PASS (no errors)

- [ ] **Step 2: Run all enum-related tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~EnumPreprocessor" -v n`
Expected: All tests PASS

- [ ] **Step 3: Run full test suite (quick check)**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo -v q`
Expected: No new failures introduced

- [ ] **Step 4: Final commit if any fixups needed**

```bash
git add -A
git commit -m "fix: resolve any test/build issues from enum preprocessor integration"
```
