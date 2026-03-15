# IdentifierExpressionTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`

## 1. C# Language Feature Converted

`IdentifierExpressionTransformer` converts C# identifier and member-access expressions to Java equivalents. It is one of the most frequently invoked transformers because identifiers appear in virtually every expression. It handles:

- Simple identifiers (`x`, `myField`, `MyClass`)
- Generic names (`List<int>`, `Dictionary<string, int>`)
- Member access (`obj.Property`, `obj.Method`, `ClassName.StaticMember`)
- Pointer member access (`ptr->field` — translated to `.field`)
- `PredefinedType` (`int`, `string`, `bool`, etc.)
- Using-alias resolution (e.g., `P1` → `Core.Geometry.Point`)
- Namespace-qualified names and type resolution

```csharp
// C#
list.Count
person.Name
Math.PI
int

// Java (expected)
list.size()
person.getName()
Math.PI
int
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Member access does not apply method/property name mapping

When transforming `list.Count`, the transformer emits `list.Count` as-is. The type mapping table in `TypeMappings.json` maps `Count` → `size()` for `ICollection`/`List`, but `IdentifierExpressionTransformer` does not consult the method/member name mapping. As a result, `list.Count` compiles if the Java `List` class had a `Count` field — which it doesn't.

The member-name mapping is partially applied in `InvocationExpressionTransformer` (for method calls) but **not** for property-style accesses (no parentheses).

### Issue 2 — Property accesses not converted to `getXxx()` calls

```csharp
string n = person.Name;  // property access
```

In Java there is no `Name` property; the generated getter is `getName()`. However, `IdentifierExpressionTransformer` emits `person.Name` unchanged. The semantic model would show that `Name` is an `IPropertySymbol`, which should trigger a getter-call transformation.

### Issue 3 — `PredefinedType` returns primitive even in generic context

```csharp
List<int> list;   // int → int (primitive) is wrong for generics
```

`PredefinedTypeTransformer` (part of this transformer) maps `int` → `int`. For a generic type argument, Java requires the boxed type `Integer`. The transformer does not check whether the `PredefinedType` is in a generic argument position and always emits the primitive. This produces `List<int>` which is a Java compile error.

### Issue 4 — `BoxedTypeName` helper is duplicated here and in `ExpressionTransformerHelpers`

Both `IdentifierExpressionTransformer` and `ExpressionTransformerHelpers` contain a `BoxedTypeName` lookup table. They may diverge over time. The canonical version should be in one place.

### Issue 5 — Using-alias resolution stops at the first alias and may not apply renames

When a using alias is registered (`using P1 = Core.Geometry.Point;`) the transformer substitutes `P1` with `Core.Geometry.Point` on identifier lookup. However, if the resolved name `Core.Geometry.Point` is itself subject to another mapping (e.g., from a namespace-to-package remapping in `TypeMappings.json`), that second mapping may not be applied because the substitution happens in the identifier transformer before the type registry processes it.

### Issue 6 — Pointer member access `ptr->field` comment is "pointer member access" but should note unsafe context

The emitted Java comment reads `/* pointer member access */` but no warning is added that the surrounding unsafety context cannot be translated. The output field access `ptr.field` may be valid but the semantics differ fundamentally from native pointer dereferencing.

## 3. Proposed Fixes

### Fix 1 — Consult member-name mapping table for property-style member accesses

After determining the member name, look it up in `TypeMappingRegistry`:
```csharp
if (context.SemanticModel.GetSymbolInfo(memberAccess).Symbol is IPropertySymbol prop)
{
    string mappedName = context.TypeMappingRegistry.MapMemberName(
        prop.ContainingType.Name, prop.Name);
    if (mappedName.EndsWith("()"))
        return $"{TransformExpression(memberAccess.Expression, context)}.{mappedName}";
    return $"{TransformExpression(memberAccess.Expression, context)}.{mappedName}";
}
```

### Fix 2 — Convert property access to `getXxx()` when no mapping is configured

```csharp
if (symbol is IPropertySymbol prop && !context.IsLeftHandSideOfAssignment(memberAccess))
{
    string getter = "get" + char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..];
    return $"{receiver}.{getter}()";
}
```

### Fix 3 — Use boxed type for generic type arguments

```csharp
bool isGenericArg = node.Parent is TypeArgumentListSyntax;
if (isGenericArg)
    return BoxedTypeName(primitiveType);
return primitiveType;  // e.g., "int"
```

### Fix 4 — Remove duplicate `BoxedTypeName` and centralise in `ExpressionTransformerHelpers`

Delete the local `BoxedTypeName` map from `IdentifierExpressionTransformer` and call `ExpressionTransformerHelpers.BoxedTypeName(t)`.

### Fix 5 — Apply type-registry mapping after alias resolution

```csharp
string resolved = context.ResolveAlias(name) ?? name;
return context.TypeMappingRegistry.MapType(resolved) ?? resolved;
```

### Fix 6 — Add safety note for pointer member access

```csharp
javaExpr.LeadingComment =
    "// WARNING: C# unsafe pointer dereference — Java does not support pointer arithmetic.";
```

## 4. Commit Changes

```
fix(identifier): apply member name mapping for property accesses, convert properties to getXxx() calls, box primitives in generic context, centralise BoxedTypeName, chain alias→type-registry lookup, and warn on pointer member access
```
