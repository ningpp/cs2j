# StringExpressionTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs`

## 1. C# Language Feature Converted

`StringExpressionTransformer` converts C# interpolated string expressions to Java string operations. C# has rich string literal forms:

| C# form | Java equivalent |
|---|---|
| `$"Hello {name}"` | `"Hello " + name` or `String.format(...)` |
| `$"Value: {x:D4}"` | `String.format("Value: %04d", x)` |
| `@"line1\nline2"` | `"line1\\nline2"` (verbatim) |
| `$@"tag {x}"` | `String.format(...)` |
| Raw interpolated `$"""..."""` | Java text block (Java 15+) |

```csharp
// C#
string msg = $"Point({X}, {Y}) distance={Math.Sqrt(X*X+Y*Y):F2}";

// Java
String msg = String.format("Point(%s, %s) distance=%.2f", X, Y, Math.sqrt(X*X+Y*Y));
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `ConvertCSharpFormatToJava` is incomplete for common format specifiers

The format-specifier conversion function handles only a few cases. Many common C# format specifiers are not translated:

| C# specifier | Expected Java | Current output |
|---|---|---|
| `D4` or `d4` | `%04d` | `D4` (literal) |
| `F2` or `f2` | `%.2f` | `F2` |
| `X` or `x` | `%X` or `%x` | `X` |
| `E3` or `e3` | `%.3e` or `%.3E` | `E3` |
| `N0` or `n0` | `%,.0f` | `N0` |
| `P` or `p` | `%.0f%%` | `P` |
| `B8` (binary, C# 8) | no Java equivalent | `B8` |

Unhandled specifiers are passed through literally, producing invalid Java format strings.

### Issue 2 — Multi-part interpolated strings use `+` concatenation instead of `StringBuilder`

For interpolated strings with many parts (e.g., 6+ segments):
```csharp
string s = $"{a} {b} {c} {d} {e} {f} {g}";
```

The transformer emits:
```java
String s = a + " " + b + " " + c + " " + d + " " + e + " " + f + " " + g;
```

Java's `+` string concatenation creates intermediate `String` objects for each concatenation. For strings composed of many parts, `StringBuilder` (or `String.format`) is more efficient and avoids excessive object allocation. This is especially significant in hot paths.

### Issue 3 — Multi-line raw interpolated strings (C# 11) are not handled

```csharp
string tpl = $"""
    Hello {name},
    Your score: {score:F1}
    """;
```

Raw interpolated strings combine the raw-string and interpolation features. The transformer has no branch for `InterpolatedRawStringLiteralExpression`. These fall through to a `/* TODO */` or produce garbled output.

### Issue 4 — Verbatim string `@"..."` escaping is not fully handled

C# verbatim strings use `""` to represent a literal double-quote. The transformer must convert `""` → `\"` when emitting standard Java strings. If this substitution is missed or applied incorrectly, the generated Java string literal will have unbalanced quotes.

### Issue 5 — `String.format` is chosen over concatenation even for single-variable strings

For simple single-variable interpolations (`$"prefix{x}suffix"`), `String.format("prefix%ssuffix", x)` produces an allocation and a format parse on every call. Plain concatenation (`"prefix" + x + "suffix"`) is faster and avoids the format overhead. The transformer should prefer concatenation for strings with no format specifiers.

## 3. Proposed Fixes

### Fix 1 — Extend `ConvertCSharpFormatToJava` with a complete specifier table

```csharp
private static string ConvertCSharpFormatToJava(string specifier) =>
    specifier.ToUpperInvariant() switch {
        var s when s.StartsWith("D") => FormatInteger(s[1..], padded: true),
        var s when s.StartsWith("F") => $"%.{s[1..]}f",
        var s when s.StartsWith("E") => $"%.{s[1..]}e",
        var s when s.StartsWith("X") => "%" + (s.Length > 1 ? s[1..] : "") + "X",
        var s when s.StartsWith("N") => $"%,.{s[1..]}f",
        "P" => "%.0f%%",
        _ => $"/* TODO: format specifier {specifier} */ %s"
    };
```

### Fix 2 — Use `StringBuilder` for strings with 4+ concatenation parts

```csharp
if (parts.Count >= 4)
{
    string appends = string.Join("", parts.Select(p => $".append({p})"));
    return $"new StringBuilder(){appends}.toString()";
}
return string.Join(" + ", parts);
```

### Fix 3 — Handle `InterpolatedRawStringLiteralExpression`

Check for raw-string kinds alongside standard `InterpolatedStringExpressionSyntax` and apply the same interpolation logic, but emit a Java text block for the non-interpolated segments when `JavaVersion >= Java15`.

### Fix 4 — Normalise verbatim string escaping

In the verbatim-string branch, replace `""` with `\"` before emitting:
```csharp
string normalized = verbatimContent.Replace("\"\"", "\\\"");
```

### Fix 5 — Use concatenation for zero-format-specifier interpolated strings

```csharp
bool hasFormatSpecifiers = interpolatedParts.Any(p => p is InterpolationSyntax i && i.FormatClause != null);
if (!hasFormatSpecifiers)
    return ConcatenateOnly(interpolatedParts, context);
else
    return EmitStringFormat(interpolatedParts, context);
```

## 4. Commit Changes

```
fix(string): complete format specifier conversion table, use StringBuilder for 4+ parts, handle raw interpolated strings, fix verbatim quote escaping, and prefer concatenation when no format specifiers
```
