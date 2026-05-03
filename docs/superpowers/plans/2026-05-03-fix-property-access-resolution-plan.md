# Fix Property Access Resolution — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix ~200+ Java compilation errors where C# property access (e.g., `curve.Start`) is emitted as raw field access instead of getter calls (e.g., `curve.getStart()`).

**Architecture:** Three-phase fix in `IdentifierExpressionTransformer.TransformMemberAccess()`: (1) add diagnostic logging to understand why semantic model resolution fails, (2) add 4 fallback strategies to resolve `receiverType` when `GetTypeInfo` fails, (3) restructure the fallback chain to move per-type `GetMembers`-based property detection before the global whitelist, then remove the whitelist entirely.

**Tech Stack:** C#, Roslyn (Microsoft.CodeAnalysis), XUnit

---

## File Map

| Role | Path |
|------|------|
| **Primary change** | `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs` |
| **New tests** | `tests/CSharpToJava.Tests/PerTypePropertyResolutionTests.cs` |

---

### Task 1: Write failing tests for per-type property resolution

**Files:**
- Create: `tests/CSharpToJava.Tests/PerTypePropertyResolutionTests.cs`

- [ ] **Step 1: Create the test file with all test cases**

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that property access is resolved per-type via GetMembers(),
/// not via a global whitelist. Verifies the fix for property names that
/// are fields on some types but properties on others.
/// </summary>
public class PerTypePropertyResolutionTests
{
    /// <summary>
    /// Property on an interface accessed through interface-typed variable
    /// must generate a getter call. This is the most common failure pattern.
    /// </summary>
    [Fact]
    public void InterfaceProperty_GeneratesGetter()
    {
        var result = Convert(@"
interface IShape {
    Point Start { get; set; }
    Point End { get; set; }
}
class Point { public double X { get; set; } public double Y { get; set; } }
class Test {
    void M(IShape shape) {
        var s = shape.Start;
        var e = shape.End;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("shape.getStart()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("shape.getEnd()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("shape.Start", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("shape.End", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same property name that is a field on a different type must still work
    /// correctly per-type. "Nodes" is a property on Cluster but a field on
    /// some other MSAGL types.
    /// </summary>
    [Fact]
    public void SameName_PropertyOnOneType_FieldOnAnother()
    {
        var result = Convert(@"
class Container {
    public int Nodes;  // field
}
class Cluster {
    public System.Collections.Generic.List<string> Nodes { get; set; }  // property
}
class Test {
    void M(Container c, Cluster cl) {
        var n1 = c.Nodes;   // field access
        var n2 = cl.Nodes;  // property → getter
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("c.Nodes", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("cl.getNodes()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property defined in a base class must be found via base type walking.
    /// </summary>
    [Fact]
    public void BaseClassProperty_GeneratesGetter()
    {
        var result = Convert(@"
class Base {
    public string Label { get; set; }
}
class Derived : Base {
    public int Extra { get; set; }
}
class Test {
    void M(Derived d) {
        var l = d.Label;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("d.getLabel()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property defined in an implemented interface must be found via interface walking.
    /// </summary>
    [Fact]
    public void InterfaceProperty_OnImplementingClass_GeneratesGetter()
    {
        var result = Convert(@"
interface IHasId {
    int Id { get; set; }
}
class Entity : IHasId {
    public int Id { get; set; }
}
class Test {
    void M(Entity e) {
        var id = e.Id;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("e.getId()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Chained property access (a.B.C) must resolve correctly.
    /// </summary>
    [Fact]
    public void ChainedPropertyAccess_GeneratesChainedGetters()
    {
        var result = Convert(@"
class Inner {
    public int Value { get; set; }
}
class Outer {
    public Inner Child { get; set; }
}
class Test {
    void M(Outer o) {
        var v = o.Child.Value;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("o.getChild().getValue()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property names that were removed from the whitelist in commit 70bfbc5
    /// must still generate getters when they are properties on the receiver type.
    /// </summary>
    [Fact]
    public void RemovedWhitelistNames_StillGenerateGetters()
    {
        var result = Convert(@"
class Shape {
    public double Width { get; set; }
    public double Height { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Radius { get; set; }
}
class Edge {
    public Shape SourcePoint { get; set; }
    public Shape TargetPoint { get; set; }
}
class Test {
    void M(Shape s, Edge e) {
        var w = s.Width;
        var h = s.Height;
        var l = s.Left;
        var t = s.Top;
        var r = s.Radius;
        var sp = e.SourcePoint;
        var tp = e.TargetPoint;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("s.getWidth()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.getHeight()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.getLeft()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.getTop()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.getRadius()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("e.getSourcePoint()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("e.getTargetPoint()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Auto-properties that are emitted as public fields in project pipeline
    /// must NOT get getters.
    /// </summary>
    [Fact]
    public void AutoProperty_EmittedAsField_AccessedAsField()
    {
        var result = Convert(@"
class Node {
    public object AlgorithmData { get; set; }
}
class Test {
    void M(Node n) {
        var data = n.AlgorithmData;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("n.AlgorithmData", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getAlgorithmData()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// System type property access (e.g. Count → size()) must still work.
    /// </summary>
    [Fact]
    public void SystemTypeProperty_CountMapsToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(List<int> items) {
        var c = items.Count;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("items.size()", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
```

- [ ] **Step 2: Run the new tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~PerTypePropertyResolutionTests"
```

Expected: Most tests FAIL because the whitelist doesn't contain these names or the per-type GetMembers check runs too late.

- [ ] **Step 3: Commit failing tests**

```bash
git add tests/CSharpToJava.Tests/PerTypePropertyResolutionTests.cs
git commit -m "test: add failing tests for per-type property resolution

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 2: Phase 1 — Add diagnostic logging in last-resort fallback

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`

- [ ] **Step 1: Add diagnostic at the raw field access fallback (line 938-939)**

Replace lines 938-939:
```csharp
        var member = ConversionContext.EscapeJavaKeyword(memberName);
        return $"{target}.{member}";
```

With:
```csharp
        // Phase 1 diagnostic: log why we fell through to raw field access
        if (memberName != "AlgorithmData") // AlgorithmData is intentionally a field
        {
            var reasonA = context.SemanticModel == null ? "SemanticModel null"
                : context.SemanticModel.GetSymbolInfo(node).Symbol switch
                {
                    null => "GetSymbolInfo returned null",
                    IFieldSymbol => $"Symbol was IFieldSymbol({memberName})",
                    IMethodSymbol => $"Symbol was IMethodSymbol({memberName})",
                    var s => $"Symbol was {s?.Kind}({memberName})"
                };
            var reasonReceiverType = receiverType == null ? "receiverType null"
                : receiverType.TypeKind == TypeKind.Error ? "receiverType Error"
                : $"receiverType={receiverType.ToDisplayString()}, GetMembers returned no IPropertySymbol for '{memberName}'";
            context.Diagnostics.Info(
                $"Property fallback: '{node}' -> {target}.{memberName} | PathA: {reasonA} | PathC: {reasonReceiverType}",
                node.GetLocation());
        }

        var member = ConversionContext.EscapeJavaKeyword(memberName);
        return $"{target}.{member}";
```

- [ ] **Step 2: Build to verify no compilation error**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs
git commit -m "feat: add diagnostic logging for property resolution fallback

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 3: Phase 2 — Strengthen receiverType resolution

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`

- [ ] **Step 1: Add 4 new receiverType fallback strategies after line 505**

Find the closing brace `}` of the block starting at line 461 with `if (receiverType == null && node.Expression is IdentifierNameSyntax id)` (the block ends around line 505 with `}`).

Insert new strategies after line 505 (before the `TryGetStaticTypeReceiverJavaReference` call at line 508):

```csharp
            // Strategy 5: Unwrap parenthesized and cast expressions.
            // e.g. ((ICurve)obj).Start → get type of obj cast to ICurve
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                var inner = node.Expression;
                while (inner is ParenthesizedExpressionSyntax paren)
                    inner = paren.Expression;
                if (inner is CastExpressionSyntax cast)
                {
                    var castType = context.SemanticModel?.GetTypeInfo(cast.Type).Type;
                    if (castType != null && castType.TypeKind != TypeKind.Error)
                        receiverType = castType;
                }
            }

            // Strategy 6: Resolve 'this' and 'base' expressions to the enclosing type.
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                if (node.Expression is ThisExpressionSyntax)
                {
                    var enclosingSym = context.SemanticModel?.GetEnclosingSymbol(node.SpanStart);
                    receiverType = enclosingSym?.ContainingType;
                }
                else if (node.Expression is BaseExpressionSyntax)
                {
                    var enclosingSym = context.SemanticModel?.GetEnclosingSymbol(node.SpanStart);
                    receiverType = enclosingSym?.ContainingType?.BaseType;
                }
            }

            // Strategy 7: For MemberAccessExpressionSyntax receiver (chained access),
            // recursively resolve the type of the inner member.
            // e.g. obj.Property.Start → resolve type of obj.Property first
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                if (node.Expression is MemberAccessExpressionSyntax innerMemberAccess)
                {
                    var innerTypeInfo = context.SemanticModel?.GetTypeInfo(innerMemberAccess).Type;
                    if (innerTypeInfo != null && innerTypeInfo.TypeKind != TypeKind.Error)
                        receiverType = innerTypeInfo;
                }
            }

            // Strategy 8: For IdentifierNameSyntax that looks like a type name (starts
            // with uppercase), try the compilation's GetTypeByMetadataName.
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                if (node.Expression is IdentifierNameSyntax typeId
                    && typeId.Identifier.Text.Length > 0
                    && char.IsUpper(typeId.Identifier.Text[0])
                    && context.ProjectCompilation != null)
                {
                    // Try current namespace + name, then bare name
                    var currentNs = context.CurrentNamespace ?? "";
                    var qualifiedName = string.IsNullOrEmpty(currentNs)
                        ? typeId.Identifier.Text
                        : $"{currentNs}.{typeId.Identifier.Text}";
                    receiverType = context.ProjectCompilation.GetTypeByMetadataName(qualifiedName)
                        ?? context.ProjectCompilation.GetTypeByMetadataName(typeId.Identifier.Text);
                }
            }
```

- [ ] **Step 2: Build to verify no compilation errors**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs
git commit -m "feat: add 4 fallback strategies for receiverType resolution

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 4: Phase 3 — Move and expand Path C, remove Path D

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`

- [ ] **Step 1: Add a private helper method for the expanded Path C**

Add the following method to the `IdentifierExpressionTransformer` class (place it before `MapPrimitiveStaticFieldName` around line 942):

```csharp
    /// <summary>
    /// Per-type property/field resolution via GetMembers(). Walks the full type
    /// hierarchy (receiverType + base types + all interfaces) to determine whether
    /// the member name is a property (needs getter) or a field (access directly).
    /// Returns null when the type cannot be resolved or the member is not found.
    /// </summary>
    private string? TryResolvePropertyByType(
        string memberName,
        string target,
        ITypeSymbol receiverType,
        ConversionContext context)
    {
        if (receiverType.TypeKind == TypeKind.Error)
            return null;

        // Array: Length is a property in C# but a field in Java
        if (receiverType.TypeKind == TypeKind.Array)
        {
            if (memberName == "Length")
                return $"{target}.length";
            return null;
        }

        // Type parameter: check constraint types
        if (receiverType is ITypeParameterSymbol typeParam)
        {
            foreach (var constraint in typeParam.ConstraintTypes)
            {
                var result = TryResolvePropertyByType(memberName, target, constraint, context);
                if (result != null)
                    return result;
            }
            return null;
        }

        if (receiverType is not INamedTypeSymbol namedReceiver)
            return null;

        // Collect all types to check: receiverType + base types + all interfaces
        var typesToCheck = new List<INamedTypeSymbol> { namedReceiver };
        var baseType = namedReceiver.BaseType;
        while (baseType != null)
        {
            typesToCheck.Add(baseType);
            baseType = baseType.BaseType;
        }
        typesToCheck.AddRange(namedReceiver.AllInterfaces);

        // Walk each type in the hierarchy looking for the member
        foreach (var typeSymbol in typesToCheck)
        {
            foreach (var m in typeSymbol.GetMembers(memberName))
            {
                if (m is IPropertySymbol foundProp)
                {
                    // Check TypeMappings for a configured method name override
                    var typeName = foundProp.ContainingType.ToDisplayString();
                    var mapped = context.TypeMappings.MapMethod(typeName, memberName);
                    if (mapped == null && foundProp.ContainingType.ContainingNamespace != null)
                        mapped = context.TypeMappings.MapMethod(
                            $"{foundProp.ContainingType.ContainingNamespace}.{foundProp.ContainingType.Name}", memberName);
                    // Walk AllInterfaces for TypeMappings matches too
                    if (mapped == null)
                    {
                        foreach (var iface in foundProp.ContainingType.AllInterfaces)
                        {
                            mapped = context.TypeMappings.MapMethod(iface.ToDisplayString(), memberName);
                            if (mapped == null)
                                mapped = context.TypeMappings.MapMethod(
                                    $"{iface.ContainingNamespace}.{iface.Name}", memberName);
                            if (mapped != null) break;
                        }
                    }
                    if (mapped != null)
                    {
                        if (mapped == "getValues") return $"{target}.values()";
                        if (mapped == "getKeys") return $"{target}.keySet()";
                        return mapped.Contains('.') ? mapped : $"{target}.{mapped}()";
                    }

                    // Default: generate getXxx() getter
                    var getter = "get" + char.ToUpperInvariant(memberName[0]) + memberName[1..];
                    return $"{target}.{getter}()";
                }
                if (m is IFieldSymbol { IsStatic: false })
                {
                    // It's a field — just access it directly
                    return $"{target}.{memberName}";
                }
            }
        }

        return null; // member not found in any type
    }
```

- [ ] **Step 2: Insert call to new Path C right after Path A (after line 778)**

After line 778 (the closing `}` of `if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IPropertySymbol prop)`), insert:

```csharp

        // Path C: Per-type property/field resolution via receiverType.GetMembers().
        // This runs immediately after Path A (GetSymbolInfo) fails. It uses the
        // type's own metadata to decide — no global whitelist needed.
        // Only fires for instance access (target starts with lowercase).
        if (target.Length > 0 && char.IsLower(target[0]) && receiverType != null)
        {
            var resolved = TryResolvePropertyByType(memberName, target, receiverType, context);
            if (resolved != null)
                return resolved;
        }
```

- [ ] **Step 3: Remove old Path C (lines 874-897) and Path D (lines 899-939)**

Remove lines 874-897 (the old `receiverType is INamedTypeSymbol` check):
```csharp
        // Generic fallback: when GetSymbolInfo failed, use the receiver type's
        // GetMembers to detect if this is a property (needs getter) or field.
        if (receiverType is INamedTypeSymbol { TypeKind: not TypeKind.Error } receiverNamed)
        {
            foreach (var m in receiverNamed.GetMembers(memberName))
            {
                if (m is IPropertySymbol)
                {
                    // Check TypeMappings for configured method name
                    var ptn = m.ContainingType.ToDisplayString();
                    var pm = context.TypeMappings.MapMethod(ptn, memberName);
                    if (pm == null && m.ContainingType.ContainingNamespace != null)
                        pm = context.TypeMappings.MapMethod(
                            $"{m.ContainingType.ContainingNamespace}.{m.ContainingType.Name}", memberName);
                    if (pm != null)
                        return pm.Contains('.') ? pm : $"{target}.{pm}()";

                    var getter = "get" + char.ToUpperInvariant(memberName[0]) + memberName[1..];
                    return $"{target}.{getter}()";
                }
                if (m is IFieldSymbol { IsStatic: false })
                    break;
            }
        }
```

And remove lines 917-937 (the Path D global whitelist):
```csharp
        // Common C# property names that always need getters in Java
        // (only when receiver is an instance, not a type name)
        if (target.Length > 0 && char.IsLower(target[0]))
        {
            // Only emit getters for names that are always C# properties in MSAGL,
            // never fields. Names like End/Start/Width/Height can be fields.
            var pascalGetter = memberName switch
            {
                "Source" or "Target" or "Rectangle" or "Center" or "BoundingBox"
                    or "ParStart" or "ParEnd" or "Par0" or "LayerEdges" or "VariableToEval"
                    or "VariableDoneEval" or "LeftConstraints" or "Globalization"
                    or "RectangularBoundary" or "UpperBound" or "IsActive"
                    or "UserData" or "CwTriangle" or "Parallelogram"
                    or "Right" or "First" or "Second"
                    => "get" + memberName,
                _ => null
            };
            if (pascalGetter != null)
                return $"{target}.{pascalGetter}()";
        }
```

- [ ] **Step 4: Build to verify no compilation errors**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs
git commit -m "feat: move Path C before Path B, expand type walking, remove global whitelist

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 5: Run all tests and verify

**Files:** None (verification only)

- [ ] **Step 1: Run full test suite**

```bash
dotnet test
```

Expected: All existing tests pass. The new `PerTypePropertyResolutionTests` tests pass.

- [ ] **Step 2: Run specific property-related tests to verify no regressions**

```bash
dotnet test --filter "FullyQualifiedName~Property"
dotnet test --filter "FullyQualifiedName~MemberAccess"
dotnet test --filter "FullyQualifiedName~AutoProperty"
dotnet test --filter "FullyQualifiedName~Count"
```

Expected: All pass.

- [ ] **Step 3: If any tests fail, fix the issue**

Examine failure output. The most likely regression area is System type property mappings (Count→size(), Dictionary.Values→values()). If Path C's TypeMappings lookup doesn't find the mapping, it would generate `getValues()` instead of `values()`. Fix by ensuring `TryResolvePropertyByType` checks AllInterfaces for TypeMappings (already included in the helper method code above).

- [ ] **Step 4: Commit any fixes**

```bash
git add -A
git commit -m "fix: ensure TypeMappings lookup covers interface hierarchy in Path C

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 6: End-to-end verification with MSAGL project

- [ ] **Step 1: Run the converter on the MSAGL project**

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s D:\agl\GraphLayout\MSAGL -d d:\aj21425 --force --verbose
```

- [ ] **Step 2: Check for remaining property-related errors in output**

Look for `Property fallback:` diagnostics in the verbose output. These indicate cases still falling through to raw field access.

If there are fewer `Property fallback:` warnings than the original ~200 errors, the fix is working. Some cases may still fall through (e.g., truly unresolvable symbols), which is expected.
```

---

## Self-Review

**1. Spec coverage:**
- Phase 1 (diagnostic logging) → Task 2 ✓
- Phase 2 (strengthen receiverType) → Task 3 ✓
- Phase 3 (restructure fallback chain, move Path C, remove Path D) → Task 4 ✓
- Tests → Task 1 ✓
- Verification → Tasks 5, 6 ✓
- Removed whitelist names handled per-type → Task 4 Step 1 (TryResolvePropertyByType) ✓

**2. Placeholder scan:** No TBD, TODO, or vague instructions. All code is concrete.

**3. Type consistency:**
- `TryResolvePropertyByType` method signature: `(string memberName, string target, ITypeSymbol receiverType, ConversionContext context)` → called with same args in Step 2 ✓
- `context.Diagnostics.Info()` — verified DiagnosticCollector has Info method ✓
- `context.ProjectCompilation` — verified ConversionContext has this property ✓
- `context.CurrentNamespace` — verified in ConversionContext ✓

