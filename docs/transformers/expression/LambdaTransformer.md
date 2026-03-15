# LambdaTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs`

## 1. C# Language Feature Converted

`LambdaTransformer` converts C# lambda expressions and anonymous method expressions to Java lambda expressions. C# lambdas are used as delegates, `Func<>`, `Action<>`, `Predicate<>`, and LINQ method arguments.

The transformer handles:
- `SimpleLambdaExpression`: `x => x * 2`
- `ParenthesizedLambdaExpression`: `(x, y) => x + y`
- `AnonymousMethodExpression`: `delegate(int x) { return x * 2; }`
- Expression-body and statement-body lambdas
- Lambda capture of local variables (closures)

```csharp
// C#
Func<int, int>    square = x => x * x;
Action<string>    print  = s => Console.WriteLine(s);
Predicate<string> empty  = s => s.Length == 0;

// Java
Function<Integer, Integer> square = x -> x * x;
Consumer<String>           print  = s -> System.out.println(s);
Predicate<String>          empty  = s -> s.length() == 0;
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `async` lambdas are not handled

```csharp
Func<Task<int>> compute = async () => await GetValueAsync();
```

When a lambda has the `async` keyword, the return type changes from `T` to `Task<T>` (or `Task`). The transformer does not detect the `async` modifier on lambdas and emits the lambda body as-is. The `await` inside the body is handled by `ControlFlowTransformer`, but the outer `Func<Task<int>>` → `Supplier<CompletableFuture<Integer>>` type mapping is not applied, and the `async` keyword itself may be emitted literally in Java.

### Issue 2 — Lambda parameter types are omitted (only names emitted)

For `ParenthesizedLambdaExpression`, C# may have explicitly typed parameters:
```csharp
(int x, string y) => x.ToString() + y
```

The transformer emits only the parameter names `(x, y) -> ...`, dropping the types. Java's type inference usually recovers the types from context, but when the lambda target type is ambiguous (multiple overloads), type inference fails and the code does not compile. Emitting explicit types `(int x, String y) -> ...` is always valid Java and safer.

### Issue 3 — Captured non-`final` (mutated) variables are not detected or warned

Java requires that variables captured in lambdas be effectively final. C# has no such restriction:
```csharp
int count = 0;
items.ForEach(x => { count++; });   // mutates captured variable
```

The transformer emits this as-is, producing a Java compile error (`Variable used in lambda expression should be effectively final`). No detection or workaround (e.g., `int[] countRef = {0};`) is provided.

### Issue 4 — `static` lambdas (C# 9) are not handled

C# 9 allows lambdas to be marked `static`, meaning they cannot capture any locals or `this`:
```csharp
Func<int, int> f = static x => x * 2;
```

The `static` keyword on the lambda has no Java equivalent. The transformer does not strip the `static` keyword before emission, potentially producing invalid Java syntax.

### Issue 5 — Anonymous methods with no parameter list are emitted without `()`

```csharp
Action a = delegate { DoSomething(); };
```

`AnonymousMethodExpression` can have a null `ParameterList`. The transformer may emit the lambda with a missing parameter list, producing `->` instead of `() ->`.

## 3. Proposed Fixes

### Fix 1 — Detect `async` lambdas and wrap return in `CompletableFuture.supplyAsync`

```csharp
if (lambda.AsyncKeyword != default)
{
    string body = TransformBody(lambda, context);
    return $"CompletableFuture.supplyAsync(() -> {body})";
}
```

For `async void` lambdas use `CompletableFuture.runAsync(() -> ...)`.

### Fix 2 — Emit explicitly typed parameters when present

```csharp
foreach (var param in parenLambda.ParameterList.Parameters)
{
    string type = param.Type != null
        ? MapType(param.Type, context)
        : /* infer from context */ "";
    javaParams.Add(type.Length > 0 ? $"{type} {param.Identifier.Text}" : param.Identifier.Text);
}
```

Emitting typed params is always safe; untyped params are only safe when the lambda target type is unambiguous.

### Fix 3 — Detect mutation of captured variables and wrap in an array holder

```csharp
var mutatedCaptures = GetMutatedCaptures(lambda, context);
foreach (var cap in mutatedCaptures)
{
    context.PreStatements.Add($"int[] _{cap} = {{ {cap} }};");
    // Replace all usages of `cap` in lambda body with `_{cap}[0]`
}
```

### Fix 4 — Strip `static` keyword from lambda modifier list

```csharp
// Before emitting the lambda, remove the static modifier — no Java equivalent
// Optionally add a comment:
var isStaticLambda = lambda.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
if (isStaticLambda)
    javaLambda.LeadingComment = "// C# static lambda — does not capture outer scope";
```

### Fix 5 — Emit `()` for anonymous methods with null parameter list

```csharp
string paramList = anonymousMethod.ParameterList != null
    ? TransformParameterList(anonymousMethod.ParameterList, context)
    : "()";
```

## 4. Commit Changes

```
fix(lambda): handle async lambdas as CompletableFuture.supplyAsync, emit typed parameters, detect captured-variable mutation and wrap in array holders, strip static modifier, and emit () for null parameter list
```
