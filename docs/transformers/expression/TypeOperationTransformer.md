# TypeOperationTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`

## 1. C# Language Feature Converted

`TypeOperationTransformer` converts C# type-related expressions to Java equivalents. These expressions concern type testing, casting, and type-value operations:

| C# expression | Java equivalent |
|---|---|
| `(T)x` (cast) | `(T)x` |
| `x is T` | `x instanceof T` |
| `x is T t` (declaration pattern) | `x instanceof T t` (Java 16+) |
| `x is T t when cond` (guarded) | `x instanceof T t && cond` |
| `x as T` | `x instanceof T ? (T)x : null` |
| `typeof(T)` | `T.class` |
| `default(T)` | `null` / `0` / `false` |
| `checked(expr)` | `Math.addExact(...)` (contextual) |
| `unchecked(expr)` | `expr` (pass-through) |
| Recursive/property patterns | `/* TODO */` |

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Does not use Java 16+ `instanceof` pattern variable syntax

For `x is T t` (declaration patterns), the transformer emits:
```java
x instanceof T && (t = (T)x) != null
```

This is a Java 8–14 workaround using an assignment expression as an intermediate. Java 16+ provides native pattern variables:
```java
x instanceof T t
```

The workaround compiles on all versions but the assignment expression is non-idiomatic for Java 16+ targets and produces overly complex code. The transformer should emit the cleaner version when the target Java version is 16+.

### Issue 2 — Recursive patterns not handled

C# 8 recursive/property patterns:
```csharp
if (shape is Circle { Radius: > 5 } c) { ... }
```

These fall through to `/* TODO: recursive pattern */`. Java 21+ has guarded patterns in `switch`, but `instanceof` with property conditions is not natively supported. The correct translation for Java 8–20 is:
```java
if (shape instanceof Circle && ((Circle)shape).getRadius() > 5) {
    Circle c = (Circle)shape;
    ...
}
```

No such translation is attempted.

### Issue 3 — `as` transformation does not verify result nullability

```csharp
string s = obj as string;
```

The transformer emits:
```java
String s = obj instanceof String ? (String)obj : null;
```

This is semantically correct but may cause issues when `s` is immediately dereferenced without a null check. The transformer does not emit a warning that the `as` result may be null.

### Issue 4 — `default(T)` for struct/value types should emit `new T()` not `null`

```csharp
default(int)    // 0
default(bool)   // false
default(MyStruct) // new MyStruct() in Java
```

For primitive types, the transformer correctly emits `0`, `false`, etc. However, for custom struct types (translated to Java classes), `default(MyStruct)` should emit `new MyStruct()` (a zero-initialised instance), not `null`. The transformer emits `null` for unknown types that are not in the primitive-type map.

### Issue 5 — `typeof(T)` on generic type parameters loses bounds

```csharp
typeof(T)  // where T : class
```

The transformer emits `T.class`, which is valid Java. However, if `T` has been erased to `Object` at the Java generics level (due to type erasure), `T.class` is not valid — Java erases generic type parameters. The transformer does not warn about this. `T.class` is valid at the Roslyn syntax level but may produce a Java compile warning or error depending on the context.

## 3. Proposed Fixes

### Fix 1 — Emit Java 16+ pattern variables when target version is 16+

```csharp
if (context.Options.JavaVersion >= JavaVersion.Java16 && pattern is DeclarationPatternSyntax decl)
{
    return $"{TransformExpression(isExpr.Expression, context)} instanceof " +
           $"{MapType(decl.Type, context)} {decl.Designation}";
}
// fallback for Java 8–15:
return $"{TransformExpression(isExpr.Expression, context)} instanceof " +
       $"{type} && ({decl.Designation} = ({type}){subject}) != null";
```

### Fix 2 — Translate recursive patterns to nested `instanceof` + condition chains

```csharp
if (pattern is RecursivePatternSyntax recPat)
{
    string typeCheck = $"{subject} instanceof {type}";
    string cast      = $"(({type}){subject})";
    var conditions   = recPat.PropertyPatternClause.Subpatterns
        .Select(s => $"{cast}.get{char.ToUpperInvariant(s.NameColon.Name.Identifier.Text[0])}{s.NameColon.Name.Identifier.Text[1..]}() {TransformPattern(s.Pattern, context)}");
    return string.Join(" && ", conditions.Prepend(typeCheck));
}
```

### Fix 3 — Add a null-safety JavaDoc note for `as` results

```csharp
javaExpr.TrailingComment = "// result may be null — check before use";
```

### Fix 4 — Emit `new T()` for struct-type `default(T)`

```csharp
if (typeSymbol is INamedTypeSymbol named && named.TypeKind == TypeKind.Struct)
    return $"new {MapType(context, defaultExpr.Type)}()";
return "null";  // reference types
```

### Fix 5 — Warn about `typeof(T)` on generic type parameters

```csharp
if (typeofExpr.Type is IdentifierNameSyntax id && context.IsTypeParameter(id.Identifier.Text))
    javaExpr.LeadingComment = "// WARNING: type parameter erased at runtime; T.class may fail";
```

## 4. Commit Changes

```
fix(type-operation): emit Java 16+ instanceof pattern vars, translate recursive patterns to nested instanceof+condition, note as-null risk, emit new T() for struct default, and warn on erased type parameters in typeof
```
