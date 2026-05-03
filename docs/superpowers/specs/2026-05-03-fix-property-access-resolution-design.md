# Fix Property Access Resolution in C# to Java Converter

**Date**: 2026-05-03
**Status**: Design approved, awaiting implementation plan

## Problem

The converter emits plain field access (e.g., `curve.Start`) instead of getter calls (e.g., `curve.getStart()`) for many C# property accesses. This causes Java compilation errors of the form "cannot find symbol: variable X" across ~200+ locations in the MSAGL conversion output.

**Affected properties** (examples):
- `ICurve.Start`, `ICurve.End`
- `Point.X`, `Point.Y` (emitted as lowercase `x`, `y`)
- `Curve.Segment1`, `Curve.Par1`, `Curve.IntersectionPoint`
- `ArrayList.Capacity`
- `Cluster.Nodes`, `Cluster.Clusters`, `Cluster.BoundaryCurve`
- `Solver.Left`, `Solver.RightConstraints`, `Solver.ActualPos`
- `FastIncrementalLayout.Labels`, `LgData.DebugId`
- `VisibilityEdge.SourcePoint`, `VisibilityEdge.TargetPoint`, `VisibilityEdge.Degree`

## Root Cause

The property-access-to-getter conversion lives in `IdentifierExpressionTransformer.TransformMemberAccess()` ([IdentifierExpressionTransformer.cs:436-939](../../src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs#L436-L939)). It has a 4-path fallback chain:

| Path | Lines | Mechanism | Issue |
|------|-------|-----------|-------|
| **A** | 677-778 | `GetSymbolInfo` returns `IPropertySymbol` | Works when it fires, but fails for many intra-project properties |
| **B** | 780-852 | `GetTypeInfo` on receiver + TypeMappings | Only handles System types (Dictionary, KeyValuePair, etc.) |
| **C** | 874-897 | `receiverType.GetMembers()` per-type check | Skipped when `receiverType` is null |
| **D** | 917-939 | Global hardcoded property name whitelist | Names pruned in commit `70bfbc5` to avoid false positives for MSAGL fields |

**The core issue**: When `GetSymbolInfo` fails (Path A) AND `receiverType` is null (skipping Path C), the code falls to Path D — a global whitelist that cannot distinguish between types where a name is a property vs. a field. Commit `70bfbc5` removed 14 names (Start, End, Width, Height, Nodes, Left, Top, Bottom, Radius, SourcePoint, TargetPoint, Right, Second, First) because they are fields on some MSAGL types, but they remain properties on others.

## Design

### Phase 1: Diagnostic Logging

Add verbose-only diagnostics in `TransformMemberAccess` to report when a member access reaches the last resort (Path D). Each diagnostic captures:

- Full expression text (e.g., `curve.Start`)
- Why Path A failed: `SemanticModel null` / `GetSymbolInfo returned null` / `Symbol was IFieldSymbol` / `Symbol was IMethodSymbol`
- Why Path C was skipped: `receiverType null` / `receiverType was Error type` / `GetMembers returned no IPropertySymbol`
- Source file path and line span

Gated on `--verbose` flag via `context.Diagnostics.Warning()`.

### Phase 2: Strengthen `receiverType` Resolution

`receiverType` is computed at the top of `TransformMemberAccess` (lines 444-503). Currently 4 strategies exist. Add these additional fallbacks:

| # | Strategy | Handles |
|---|----------|---------|
| 5 | Unwrap parenthesized/cast expressions | `((ICurve)obj).Start` |
| 6 | Resolve `this`/`base` to enclosing type | `this.Start` |
| 7 | For `MemberAccessExpressionSyntax` receiver, recursively resolve type from inner member | `obj.Property.Start` (chained access) |
| 8 | `compilation.GetTypeByMetadataName` for qualified type name expressions | `Namespace.Type.StaticProp` |

### Phase 3: Restructure Fallback Chain

Move Path C (per-type `GetMembers` check) from position 3 to position 2, immediately after Path A fails. Expand it to walk **base types and all interfaces** of `receiverType`. Remove the global whitelist (Path D) entirely.

**New flow**:

```
1. Path A: GetSymbolInfo → IPropertySymbol → getter (unchanged)
   |
   +-- fails
   |
2. Path C (MOVED UP, EXPANDED):
   - If receiverType is INamedTypeSymbol:
     - Walk receiverType + BaseType chain + AllInterfaces
     - GetMembers(memberName) on each
     - If IPropertySymbol found → generate getter
     - If IFieldSymbol found → emit field access
   - If receiverType is IArrayTypeSymbol:
     - Length → .length field
   - If receiverType is ITypeParameterSymbol:
     - Check constraint types for members
   |
   +-- fails (receiverType null or member not found in any type)
   |
3. Path B: GetTypeInfo + TypeMappings lookup (System types, unchanged)
   |
   +-- fails
   |
4. Static boxed-class constant fixup (Int32.MaxValue → Integer.MAX_VALUE, unchanged)
   |
   +-- fails
   |
5. Minimal last-resort: Count→size(), Length→length, Key→getKey(), Value→getValue(),
   Item1→getKey(), Item2→getValue() (lines 899-916, unchanged)
   |
   +-- fails
   |
6. Raw field access: $"{target}.{member}" (line 939, fallback for truly unresolvable cases)
```

**Key change**: The global whitelist (lines 917-939) is **removed**. Every property name that was in it (Source, Target, Rectangle, Center, BoundingBox, ParStart, ParEnd, Par0, LayerEdges, VariableToEval, VariableDoneEval, LeftConstraints, Globalization, RectangularBoundary, UpperBound, IsActive, UserData, CwTriangle, Parallelogram, Right, First, Second) gets handled per-type by Path C instead.

### Key Constraint

The heuristic at line 919 (`target.Length > 0 && char.IsLower(target[0])`) that distinguishes instance access (`curve.Start`) from static access (`Type.Const`) must be preserved — Path C should only fire when the receiver is an instance (starts with lowercase), not a type name.

## Affected Files

| File | Change |
|------|--------|
| `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs` | Primary changes: restructure fallback chain, expand receiverType resolution, add diagnostics, remove whitelist |
| Tests (new) | Unit tests for per-type property/field detection, base type/interface walking, chained access resolution |

## Risks & Mitigations

- **Risk**: Per-type `GetMembers()` might be slow for large type hierarchies.
  **Mitigation**: Most types have shallow hierarchies; cache results per (type, memberName) pair in a dictionary scoped to the conversion context.

- **Risk**: Moving Path C before Path B might change behavior for System types.
  **Mitigation**: Path C only handles instance member access (lowercase first char of target). System type static access goes through Path B unchanged.

- **Risk**: Some cases still fall through to step 6 (raw field access).
  **Mitigation**: Phase 1 diagnostics capture these cases. If any remain, they can be addressed individually with targeted fixes rather than a global whitelist.
