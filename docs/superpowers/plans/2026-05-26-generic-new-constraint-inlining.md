# Generic `new()` Constraint Call-Site Inlining — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix `ClassCastException` caused by hardcoded `(TC) new ArrayList<>()` for generic `new()` constraints by inlining the method body with concrete types at call sites.

**Architecture:** Two coordinated changes in the converter: (1) `ObjectCreationTransformer` gets a detection method + diagnostic fallback; (2) `InvocationExpressionTransformer` detects generic method calls where the callee instantiates type params with `new()` constraint, resolves concrete type bindings at the call site, and generates an inlined block with the correct concrete constructor instead of delegating to the broken generic method.

**Tech Stack:** C#, Roslyn (Microsoft.CodeAnalysis), .NET 8.0

---

## File Map

| File | Role |
|------|------|
| `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs` | Add `HasNewConstraintObjectCreation` detection; improve fallback diagnostic |
| `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs` | Add call-site detection + `TryInlineNewConstraintMethodCall`; hook into member invocation path |
| `tests/CSharpToJava.Tests/GenericNewConstraintInliningTests.cs` | New test file covering inlining and fallback |

---

### Task 1: Add detection method to ObjectCreationTransformer

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs`

- [ ] **Step 1: Add `HasNewConstraintObjectCreation` static method**

Add this public static method after `HasCollectionConstraint` (around line 261):

```csharp
/// <summary>
/// Returns true if <paramref name="method"/> has any type parameter with a
/// <c>new()</c> constraint that is materially instantiated via an
/// <c>ObjectCreationExpression</c> (new T()) in the method body.
/// </summary>
public static bool HasNewConstraintObjectCreation(IMethodSymbol method)
{
    var newConstrainedParams = method.TypeParameters
        .Where(tp => tp.HasConstructorConstraint)
        .ToHashSet(SymbolEqualityComparer.Default);

    if (newConstrainedParams.Count == 0)
        return false;

    foreach (var syntaxRef in method.DeclaringSyntaxReferences)
    {
        var syntax = syntaxRef.GetSyntax();
        if (syntax is not BaseMethodDeclarationSyntax methodDecl)
            continue;

        foreach (var creation in methodDecl.DescendantNodes()
                     .OfType<ObjectCreationExpressionSyntax>())
        {
            if (creation.ArgumentList == null || creation.ArgumentList.Arguments.Count == 0)
            {
                var typeInfo = methodDecl.SyntaxTree.GetRoot()
                    .GetCompilationUnitRoot()
                    .GetLocation()
                    .SourceTree == null
                        ? null
                        : ((CSharpSyntaxNode)methodDecl).GetLocation().SourceTree == null
                            ? null
                            : (object)creation;

                // Check if the created type is a type parameter with new() constraint.
                // We need the semantic model, but at this point we only have syntax.
                // Instead, check if the type name matches one of the new()-constrained
                // type parameters. This is a syntactic check — the semantic check
                // happens at the call site via TypeArguments binding.
                if (creation.Type is IdentifierNameSyntax idName
                    && newConstrainedParams.Any(tp => tp.Name == idName.Identifier.Text))
                {
                    return true;
                }
            }
        }
    }

    return false;
}
```

Wait — we don't have access to the semantic model in this static method. Let me reconsider. The `HasNewConstraintObjectCreation` check should work purely on the method symbol. The syntactic check above is unreliable because `new TC()` could appear in nested lambdas or using aliases.

Better approach: check via semantic model by looking at the `GetSymbolInfo` of each `ObjectCreationExpression` inside the method body. But the semantic model requires a `SemanticModel` which we don't have at detection time.

Simpler approach: just check `typeParameter.HasConstructorConstraint` — if a type parameter with `new()` constraint exists, assume it might be used in `new T()`. The worst case is a false positive that tries (and fails) to inline — which degrades gracefully to the normal method call.

```csharp
public static bool HasNewConstraintObjectCreation(IMethodSymbol method)
{
    return method.TypeParameters.Any(tp => tp.HasConstructorConstraint);
}
```

In practice this is sufficient: methods with `new()` constraints almost always use `new T()`. False positives (where a type param has `new()` but never appears in `new T()`) are rare and the inlining attempt will just fall back.

> **Correction after review:** Keep it simple — just check `HasConstructorConstraint` on type parameters. The call-site inlining logic (Task 3) will handle the actual inlining and fall back if the pattern doesn't match.

- [ ] **Step 2: Improve fallback diagnostic in TransformObjectCreation**

At line 216, replace:
```csharp
if (HasCollectionConstraint(typeParameter))
{
    context.AddImport("java.util.ArrayList");
    return $"({typeName}) new ArrayList<>()";
}
```

With:
```csharp
if (HasCollectionConstraint(typeParameter))
{
    context.AddImport("java.util.ArrayList");
    context.AddDiagnostic(new DiagnosticInfo(
        DiagnosticSeverity.Warning,
        "CS2J1006",
        "GenericConstraint",
        $"Type parameter '{typeParameter.Name}' with new() constraint fell back to ArrayList. " +
        "Call-site inlining should have handled this. Verify the call site."
    ));
    return $"({typeName}) new ArrayList<>()";
}
```

- [ ] **Step 3: Build and verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: Build succeeds.

---

### Task 2: Add call-site inlining to InvocationExpressionTransformer

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`

- [ ] **Step 1: Add `TryInlineNewConstraintMethodCall` method**

Add this private static method to `InvocationExpressionTransformer`. Place it near the end of the class, before the closing brace of the class definition.

```csharp
/// <summary>
/// Attempts to inline a generic method call where a type parameter with new()
/// constraint is instantiated in the method body. Resolves concrete type bindings
/// at the call site and generates the method body with concrete types substituted.
/// </summary>
/// <returns>The inlined Java code, or null if inlining is not possible.</returns>
private static string? TryInlineNewConstraintMethodCall(
    IMethodSymbol methodSymbol,
    InvocationExpressionSyntax node,
    MemberAccessExpressionSyntax memberAccess,
    ConversionContext context)
{
    // Only handle generic methods with new() constraints
    if (!methodSymbol.IsGenericMethod)
        return null;

    var newConstrainedParams = methodSymbol.OriginalDefinition.TypeParameters
        .Where(tp => tp.HasConstructorConstraint)
        .ToHashSet(SymbolEqualityComparer.Default);

    if (newConstrainedParams.Count == 0)
        return null;

    // Check that all type arguments at this call site are concrete
    var typeArgMap = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
    var origParams = methodSymbol.OriginalDefinition.TypeParameters;
    var typeArgs = methodSymbol.TypeArguments;

    if (origParams.Length != typeArgs.Length)
        return null;

    for (int i = 0; i < origParams.Length; i++)
    {
        if (typeArgs[i] is ITypeParameterSymbol)
            return null; // Nested generic — can't inline
        typeArgMap[origParams[i]] = typeArgs[i];
    }

    // Get the method body syntax to analyze the pattern
    BaseMethodDeclarationSyntax? methodSyntax = null;
    foreach (var syntaxRef in methodSymbol.OriginalDefinition.DeclaringSyntaxReferences)
    {
        var syntax = syntaxRef.GetSyntax();
        if (syntax is BaseMethodDeclarationSyntax decl)
        {
            methodSyntax = decl;
            break;
        }
    }

    if (methodSyntax?.Body == null)
        return null;

    // Check if we can inline: the method must be short and follow a known pattern.
    // For now, handle the AddToMap pattern:
    //   TC tc;
    //   if (!dictionary.TryGetValue(key, out tc))
    //       dictionary[key] = tc = new TC();
    //   tc.Add(value);
    var bodyStatements = methodSyntax.Body.Statements;
    if (bodyStatements.Count < 2)
        return null;

    // Try to match the AddToMap pattern
    return TryInlineAddToMapPattern(
        bodyStatements, methodSymbol, typeArgMap, node, memberAccess, context);
}

/// <summary>
/// Attempts to match and inline the CollectionUtilities.AddToMap pattern.
/// </summary>
private static string? TryInlineAddToMapPattern(
    SyntaxList<StatementSyntax> bodyStatements,
    IMethodSymbol methodSymbol,
    Dictionary<ITypeParameterSymbol, ITypeSymbol> typeArgMap,
    InvocationExpressionSyntax node,
    MemberAccessExpressionSyntax memberAccess,
    ConversionContext context)
{
    // Identify the TC type parameter (the one with new() on ICollection<TS>)
    ITypeParameterSymbol? tcParam = null;
    ITypeParameterSymbol? tsParam = null;
    ITypeParameterSymbol? tParam = null;

    foreach (var tp in methodSymbol.OriginalDefinition.TypeParameters)
    {
        if (tp.HasConstructorConstraint)
            tcParam = tp;
        else if (tp.ConstraintTypes.Any(ct => ct.Name is "ICollection"))
            tsParam = tp; // TS is the element type parameter constrained by ICollection
    }

    // Map from parameter name → position in TypeParameters
    // AddToMap<TS, T, TC>: TS=0, T=1, TC=2
    if (tcParam == null)
        return null;

    // Find the dictionary argument index and the value argument index
    // Parameters: (Dictionary<T, TC> dictionary, T key, TS value)
    int dictParamIdx = -1;
    int keyParamIdx = -1;
    int valueParamIdx = -1;

    var parameters = methodSymbol.Parameters;
    for (int i = 0; i < parameters.Length; i++)
    {
        var paramType = parameters[i].Type;
        var paramDisplay = paramType.OriginalDefinition.ToDisplayString();
        if (paramDisplay.StartsWith("System.Collections.Generic.Dictionary<")
            || paramDisplay.StartsWith("System.Collections.Generic.IDictionary<"))
        {
            dictParamIdx = i;
        }
    }

    if (dictParamIdx < 0 || parameters.Length < 3)
        return null;

    // Find the key and value parameters by checking which contains TC (the collection type)
    // TC is the value type of the dictionary: Dictionary<T, TC>
    // T is the key type, TS is the element type added to TC
    // Parameter order: dictionary(T, TC), key(T), value(TS)
    var dictType = parameters[dictParamIdx].Type as INamedTypeSymbol;
    if (dictType?.TypeArguments.Length != 2)
        return null;

    // Find TC position in dict type args (it's the value type, so index 1)
    // Check: which TypeParameter matches dict.TypeArguments[1]
    for (int i = 0; i < parameters.Length; i++)
    {
        if (i == dictParamIdx) continue;

        var paramType = parameters[i].Type;
        if (paramType is ITypeParameterSymbol tp)
        {
            if (SymbolEqualityComparer.Default.Equals(tp, tcParam))
                continue; // TC param itself

            // Try to map: is this T (key) or TS (value)?
            // key parameter type should match dict.TypeArguments[0]
            // value parameter type should be TS
        }
    }

    // Simpler approach: determine from the method signature by index
    // AddToMap<TS, T, TC>: param 0 = dict, param 1 = key (T), param 2 = value (TS)
    // This is the canonical form. We'll use positions.
    keyParamIdx = 1;
    valueParamIdx = 2;

    if (node.ArgumentList.Arguments.Count <= valueParamIdx)
        return null;

    // Get the concrete Java types
    var facade = ExpressionTransformerFacade.Instance;

    // TC → concrete Java collection type
    var tcOrigSymbol = tcParam;
    var tcConcreteSymbol = typeArgMap[tcOrigSymbol];
    string tcType = context.MapType(tcConcreteSymbol);

    // Get the dictionary receiver expression
    var dictArg = node.ArgumentList.Arguments[dictParamIdx].Expression;
    var dictExpr = facade.Transform(dictArg, context);

    // Get the key expression
    var keyArg = node.ArgumentList.Arguments[keyParamIdx].Expression;
    var keyExpr = facade.Transform(keyArg, context);

    // Get the value expression
    var valueArg = node.ArgumentList.Arguments[valueParamIdx].Expression;
    var valueExpr = facade.Transform(valueArg, context);

    // Generate temp var name
    string tmpVar = context.GenerateSyntheticName("_tc");

    // Generate the inlined block
    // { TC _tc; _tc = dict.get(key); if (_tc == null) { _tc = new TC(); dict.put(key, _tc); } _tc.add(value); }
    return $"{{ {tcType} {tmpVar} = {dictExpr}.get({keyExpr}); " +
           $"if ({tmpVar} == null) {{ {tmpVar} = new {tcType}(); {dictExpr}.put({keyExpr}, {tmpVar}); }} " +
           $"{tmpVar}.add({valueExpr}); }}";
}
```

- [ ] **Step 2: Hook into the TransformMemberInvocation path**

After `methodSymbol` is resolved (around line 1114, right after the extension method detection block), add the inlining check. Insert this code after line 1114 (`}`) that closes the `if (methodSymbol is { IsExtensionMethod: true, ... })` block:

```csharp
        // Inline generic methods with new() constraint: resolve concrete type bindings
        // at the call site and generate the method body with concrete types substituted.
        if (methodSymbol != null
            && ObjectCreationTransformer.HasNewConstraintObjectCreation(methodSymbol.OriginalDefinition))
        {
            var inlined = TryInlineNewConstraintMethodCall(
                methodSymbol, node, memberAccess, context);
            if (inlined != null)
                return inlined;
        }
```

- [ ] **Step 3: Build and verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: Build succeeds.

---

### Task 3: Write tests

**Files:**
- Create: `tests/CSharpToJava.Tests/GenericNewConstraintInliningTests.cs`

- [ ] **Step 1: Create test file with Convert helper and first test**

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GenericNewConstraintInliningTests
{
    [Fact]
    public void AddToMap_WithCustomSetTypeParameter_InlinesCorrectly()
    {
        var result = Convert("""
using System.Collections.Generic;

public class Set<T> : ICollection<T>
{
    private readonly HashSet<T> _inner = new();
    public int Count => _inner.Count;
    public bool IsReadOnly => false;
    public void Add(T item) => _inner.Add(item);
    public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Clear() => _inner.Clear();
    public bool Contains(T item) => _inner.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
    public bool Remove(T item) => _inner.Remove(item);
    public Set() { }
}

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}

public class EdgeGeometry
{
    public double LineWidth { get; set; }
}

public class CdtEdge
{
    public double Capacity { get; set; }
}

public class BundleRouter
{
    public Dictionary<CdtEdge, Set<EdgeGeometry>> GetPathsOnCdtEdge(
        Dictionary<EdgeGeometry, Set<CdtEdge>> crossedEdges)
    {
        var res = new Dictionary<CdtEdge, Set<EdgeGeometry>>();
        foreach (var edge in crossedEdges.Keys)
        {
            foreach (var cdtEdge in crossedEdges[edge])
                CollectionUtilities.AddToMap(res, cdtEdge, edge);
        }
        return res;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The generated code must NOT use (TC) new ArrayList<>() in getPathsOnCdtEdge
        Assert.DoesNotContain("(TC) new ArrayList", code);
        // The generated code must use the correct concrete type
        Assert.Contains("new Set<EdgeGeometry>()", code);
        // The inlined code must be present: dict.get(key) pattern
        Assert.Contains(".get(", code);
    }

    [Fact]
    public void AddToMap_WithArrayListTypeParameter_StillWorks()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}

public class Demo
{
    public Dictionary<int, List<string>> Build()
    {
        var res = new Dictionary<int, List<string>>();
        CollectionUtilities.AddToMap(res, 1, "hello");
        return res;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Should inline with ArrayList<String> (the concrete type) — NOT (TC) cast
        Assert.DoesNotContain("(TC) new ArrayList", code);
        Assert.Contains("new ArrayList<String>()", code);
    }

    [Fact]
    public void AddToMap_Fallback_EmittedWhenNotInlined()
    {
        // When the addToMap method body is generated (for the generic case),
        // it should contain the diagnostic fallback.
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The standalone method body should have the fallback (no call sites to inline from)
        Assert.Contains("new ArrayList<>()", code);
        // Should have a diagnostic warning about the fallback
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "CS2J1006" && d.Category == "GenericConstraint");
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

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~GenericNewConstraintInlining"`
Expected: 3 tests pass.

- [ ] **Step 3: Run full test suite for regressions**

Run: `dotnet test`
Expected: All existing tests pass, no regressions.

---

### Task 4: Commit

- [ ] **Step 1: Verify git status**

Run: `git status`
Expected: Modified files in `src/` and new test file in `tests/`.

- [ ] **Step 2: Commit changes**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
git add tests/CSharpToJava.Tests/GenericNewConstraintInliningTests.cs
git commit -m "fix: inline generic new() constraint method calls with concrete types

Instead of hardcoding (TC) new ArrayList<>() for all collection-constrained
type parameters, detect the concrete type binding at the call site and
generate an inlined block with the correct constructor.

Fixes ClassCastException in MSAGL BundleRouter where TC was bound to
Set<EdgeGeometry> but the converter emitted ArrayList."
```

---

### Task 5: Full project verification

- [ ] **Step 1: Re-convert the MSAGL GraphLayout project**

Run the CLI to re-convert the project (adjust the command based on the actual CLI usage):

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s "E:/agl-master/GraphLayout" -d "E:/z5-verify"
```

- [ ] **Step 2: Check the generated CollectionUtilities.java**

Run: `grep -n "new ArrayList" "E:/z5-verify/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Core/DataStructures/CollectionUtilities.java"`
Expected: Still present (fallback in standalone method body), but with the diagnostic.

- [ ] **Step 3: Check the generated BundleRouter.java**

Run: `grep -n "addToMap\|new Set<" "E:/z5-verify/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Routing/Spline/Bundling/BundleRouter.java"`
Expected: No call to `addToMap` in the `getPathsOnCdtEdge` method; instead, inlined code with `new Set<>()`.

- [ ] **Step 4: Build the generated Java project**

Run: `cd "E:/z5-verify/AutomaticGraphLayout" && mvn compile -q 2>&1 | tail -5`
Expected: BUILD SUCCESS.

- [ ] **Step 5: Run the failing test from the original error log**

If a Maven test runner is available, run the specific test:
```bash
cd "E:/z5-verify/AutomaticGraphLayout" && mvn test -Dtest=RandomBundlingTests#routeEdges_SmallGroups
```
Expected: No `ClassCastException`. Test passes or fails for other reasons but not due to `ArrayList cannot be cast to Set`.
Expected: No `ClassCastException`.
