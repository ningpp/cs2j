# StatementTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs`

## 1. C# Language Feature Converted

`StatementTransformer` is the single transformer responsible for converting all `StatementSyntax` subtypes in a C# method/property/constructor body to their Java equivalents. It covers the full C# statement surface:

| C# statement | Java equivalent |
|---|---|
| `Block` (`{ }`) | Block |
| `LocalDeclarationStatement` | Local variable declaration |
| `ExpressionStatement` | Expression statement |
| `IfStatement` | `if`/`else` |
| `WhileStatement` | `while` |
| `DoStatement` | `do`/`while` |
| `ForStatement` | `for` |
| `ForEachStatement` | `for (T x : collection)` |
| `SwitchStatement` | `switch` |
| `ReturnStatement` | `return` |
| `ThrowStatement` | `throw` |
| `TryStatement` | `try`/`catch`/`finally` |
| `UsingStatement` | `try`-with-resources |
| `LockStatement` | `synchronized` block |
| `BreakStatement` | `break` |
| `ContinueStatement` | `continue` |
| `GotoStatement` | labeled `break`/`continue` |
| `YieldReturnStatement` | Iterator pattern |
| `LocalFunctionStatement` | Private nested method |
| `CheckedStatement`/`UncheckedStatement` | (no equivalent) |
| `UnsafeStatement` | (no equivalent) |

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `CheckedStatement` and `UncheckedStatement` emit `/* TODO */`

```csharp
checked   { x = a + b; }
unchecked { x = a + b; }
```

The transformer has no handler for these nodes and falls through to a `/* TODO: checkedStatement */` comment. For `checked`, the correct Java equivalent is to use `Math.addExact`, `Math.multiplyExact`, etc. (Java 8+) inside the block. For `unchecked`, the inner block can be emitted as-is (Java arithmetic is always unchecked). At minimum, `unchecked { ... }` should simply emit the inner block without wrapping.

### Issue 2 — Bare `throw;` (rethrow) ancestor search ignores lambda scope boundaries

C# allows rethrowing the current exception in a `catch` block:
```csharp
catch (Exception ex) {
    Log(ex);
    throw;   // rethrow
}
```

The transformer converts bare `throw;` by walking up the syntax tree to find the enclosing `catch` clause and emitting `throw ex;`. However, if the `throw;` is inside a lambda that is inside the `catch`, the ancestor walk will cross the lambda boundary and still find the catch clause — but in Java, the lambda has its own exception scope. The rethrow may produce `throw ex;` pointing to the wrong `ex` variable.

### Issue 3 — Pattern-matching `switch` completeness not verified

C# switch expressions and pattern-matching switch statements may be exhaustive (compiler guarantees all cases are covered). When translated to Java:
```csharp
var result = x switch { int i => i * 2, string s => s.Length, _ => 0 };
```

The `switch` expression requires a default arm in Java as well or will produce a compiler warning. The transformer should check whether a `_` (discard/wildcard) arm already exists and, if not, add a `default: throw new IllegalStateException("Unexpected value: " + x);`.

### Issue 4 — Tuple deconstruction in local declarations not handled

```csharp
var (first, second) = GetPair();
var (x, y, z)       = point;
```

`LocalDeclarationStatement` with a `DeclarationExpressionSyntax` using a `ParenthesizedVariableDesignation` is not handled. The transformer falls through to a generic expression statement and may emit garbled Java. Java 10+ has no built-in tuple deconstruction; the correct translation requires calling `getFirst()`, `getSecond()` (for `Pair`) or using individual assignments.

### Issue 5 — `TryGetValue` pattern (dictionary dictionary out-param) only handled as a statement

```csharp
if (dict.TryGetValue(key, out var value)) { use(value); }
```

When `TryGetValue` appears directly in an `if` condition (not as a standalone `ExpressionStatement`), `StatementTransformer` hands control to `ExpressionTransformerFacade` for the condition, but the `out var` scope injection (declaring `value` before `TryGetValue` and using a holder object) may not be applied properly in the `if`-condition context, producing a Java compilation error (`value` undeclared).

### Issue 6 — `LockStatement` emits `synchronized` on arbitrary expressions

```csharp
lock (someMethod()) { ... }
```

Java `synchronized` blocks require a final or effectively-final lock expression. If the lock target is a method call (which is evaluated fresh on each entry), the generated `synchronized (someMethod()) { ... }` will compile but have incorrect semantics (each iteration obtains a lock on a potentially different object). A warning comment should be emitted when the lock expression is not a simple identifier.

## 3. Proposed Fixes

### Fix 1 — Handle `CheckedStatement` and `UncheckedStatement`

For `unchecked`, simply emit the inner block:
```csharp
case UncheckedStatementSyntax unchecked:
    return TransformBlock(unchecked.Block, context);
```

For `checked`, emit the block with a comment:
```csharp
case CheckedStatementSyntax checked:
    javaBlock.LeadingComment = "// C# checked block: use Math.*Exact() methods for overflow detection.";
    return TransformBlock(checked.Block, context);
```

### Fix 2 — Limit ancestor walk for bare `throw` to current lambda scope

```csharp
var catchClause = node.Ancestors()
    .TakeWhile(a => a is not AnonymousFunctionExpressionSyntax)  // stop at lambda boundary
    .OfType<CatchClauseSyntax>()
    .FirstOrDefault();
```

If no catch clause is found within the current scope, emit `throw;` with a `/* TODO: rethrow outside catch */` comment.

### Fix 3 — Add a `default` arm to exhaustive switch if missing

After emitting all switch arms, check whether a `default` arm (or `_` discard pattern) was emitted. If not, append:
```java
default: throw new IllegalStateException("Unexpected value: " + switchValue);
```

### Fix 4 — Translate tuple deconstruction to individual assignments

```java
// var (first, second) = GetPair();  →
var _t = GetPair();
var first  = _t.getFirst();
var second = _t.getSecond();
```

Or, for named tuple types, use the known component names from the semantic model.

### Fix 5 — Pre-declare `out` variables before `TryGetValue` in `if` condition

Before emitting the `if` statement, detect `out var` parameters in the condition and emit:
```java
Type[] _value = new Type[1];   // holder pattern
if (dict.tryGetValue(key, _value)) { Type value = _value[0]; ... }
```

### Fix 6 — Warn on non-identifier lock expressions

```csharp
if (lockStmt.Expression is not IdentifierNameSyntax)
    javaSync.LeadingComment = "// WARNING: lock expression is not a simple reference; verify lock identity";
```

## 4. Commit Changes

```
fix(statement): handle checked/unchecked blocks, bound bare-throw ancestor walk at lambda boundaries, add default arm to exhaustive switch, translate tuple deconstruction, pre-declare out vars in if conditions, and warn on non-identifier lock expressions
```
