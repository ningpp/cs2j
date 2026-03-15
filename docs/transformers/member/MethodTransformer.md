# MethodTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`

## 1. C# Language Feature Converted

`MethodTransformer` converts C# method declarations to Java method declarations. It is the most complex member transformer, handling:

- Instance and static methods
- Generic methods with `where` constraints
- `async`/`await` methods → `CompletableFuture<T>` (via `CSharpToJava.Async`)
- `yield return` / `yield break` → `Iterator`/`Stream` patterns
- Default parameter values → overloaded method chain
- Extension methods (`this T source`) → static methods, or instance methods when possible
- `partial` method declarations/implementations
- Expression-body methods (`=> expr`)
- `abstract`, `virtual`, `override`, `sealed`, `extern` modifiers
- `[Obsolete]` → `@Deprecated`
- XML doc comments → JavaDoc

```csharp
// C#
public static int Sum(this IEnumerable<int> source, int offset = 0)
    => source.Sum() + offset;

// Java
public static int Sum(Iterable<Integer> source, int offset) { return ... }
public static int Sum(Iterable<Integer> source) { return Sum(source, 0); }
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Extension method `this` parameter is not stripped from the instance-method variant

When converting extension methods, the transformer generates a static Java method that retains the `this`-qualified first parameter:
```java
public static int Sum(Iterable<Integer> source, int offset) { ... }
```

This is correct for the static path. However, when `--rewrite-extensions` is enabled and the method is promoted to an instance method on the receiver type, the `source` parameter should be stripped (it becomes `this`), but the transformer does not remove it in all code paths.

### Issue 2 — Type-parameter promotion regex matches substrings

When a generic method's type parameter appears in the parameter list (e.g., `T` in `List<T>`), the transformer promotes `T` to the enclosing class scope using a regex substitution:
```csharp
body = Regex.Replace(body, $@"\b{name}\b", replacement);
```

The word-boundary `\b` avoids matching `T` inside `ToString`. However, a type parameter named `E` would still match `ExpressionBody` because `\b` considers `E` to be at a word boundary before `x` (both are `\w` characters, which is correct). Actually `\bE\b` matches standalone `E` only. But a parameter named `Type` combined with a type that contains `TypeName` in a generic: `\bType\b` would match the word `Type` in `TypeName` if `TypeName` is written as `Type Name` — niche edge cases. This is low risk but worth documenting.

### Issue 3 — Default-parameter overloads generated for `abstract` and `virtual` methods

```csharp
public abstract void Process(int x, string format = "G");
```

The transformer generates the abstract method plus a non-abstract overload that calls it:
```java
public abstract void Process(int x, String format);
public void Process(int x) { Process(x, "G"); }  // ERROR: non-abstract calling abstract
```

In Java, an abstract class cannot have a non-abstract method calling an abstract sibling — this compiles fine but breaks if the abstract method is not overridden and the no-default overload is called on a concrete subclass. More critically, `virtual` (non-abstract) methods generate a redundant duplicate. The correct fix for `abstract` methods is to generate the two overloads in **concrete** classes only, or to document the pattern for the implementor.

### Issue 4 — Expression-body methods use deprecated `new ExpressionTransformer()`

```csharp
var exprTransformer = new ExpressionTransformer();   // [Obsolete]
javaMethod.Body = exprTransformer.Transform(methodDecl.ExpressionBody.Expression, context);
```

This instantiates the deprecated wrapper class instead of going through `ExpressionTransformerFacade.Instance`. Even though the wrapper forwards all calls, it adds unnecessary allocation and hides whether the expression kind is registered.

### Issue 5 — `extern` methods emit empty bodies

```csharp
[DllImport("kernel32.dll")]
public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
```

The transformer maps `extern` to a Java method with an empty body (`{ }`), losing the P/Invoke semantics. P/Invoke interop has no Java equivalent; the transformer should emit a `throw new UnsupportedOperationException("P/Invoke: SetConsoleCtrlHandler");` body and a comment.

### Issue 6 — `partial` method declarations without implementations are silently skipped

C# `partial void OnChanged();` with no implementation is a no-op in C#. The transformer should either:
- Skip emitting any Java method (correct for no-op partials), or
- Emit an empty method with a comment

Currently it may emit an incomplete method stub (no body) that fails to compile.

## 3. Proposed Fixes

### Fix 1 — Strip `this` parameter when promoting extension method to instance method

```csharp
if (isExtensionMethod && context.Options.RewriteExtensionMethods)
    javaParams.RemoveAt(0);  // remove 'source' / 'this' parameter
```

### Fix 2 — Add regression test for type-parameter name collision

Create a unit test with a type parameter named `Type` and verify no double-substitution occurs.

### Fix 3 — Suppress default-parameter overloads for `abstract` methods

```csharp
bool hasDefaults = parameters.Any(p => p.Default != null);
bool isAbstract  = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
if (hasDefaults && !isAbstract)
    EmitDefaultParameterOverloads(...);
```

### Fix 4 — Replace `new ExpressionTransformer()` with `ExpressionTransformerFacade.Instance`

```csharp
javaMethod.Body = ExpressionTransformerFacade.Instance.Transform(
    methodDecl.ExpressionBody.Expression, context);
```

### Fix 5 — Emit `UnsupportedOperationException` for `extern` methods

```csharp
if (isExtern)
    javaMethod.Body = $"throw new UnsupportedOperationException(\"P/Invoke: {methodName}\");";
```

### Fix 6 — Skip `partial` declaration-only stubs entirely

```csharp
bool isPartialDeclaration = methodDecl.Body == null && methodDecl.ExpressionBody == null
    && methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
if (isPartialDeclaration)
    return null;  // no-op partial — emit nothing
```

## 4. Commit Changes

```
fix(method): strip this-param in extension method promotion, suppress abstract default-param overloads, use ExpressionTransformerFacade, throw UnsupportedOperationException for extern, and skip partial stubs
```
