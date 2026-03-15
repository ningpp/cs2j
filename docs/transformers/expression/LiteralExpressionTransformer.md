# LiteralExpressionTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs`

## 1. C# Language Feature Converted

`LiteralExpressionTransformer` converts C# literal expressions to Java literal equivalents. It handles:

| C# literal | Java equivalent |
|---|---|
| `42` | `42` |
| `42L` | `42L` |
| `3.14f` | `3.14f` |
| `3.14` or `3.14d` | `3.14` |
| `'A'` | `'A'` |
| `"hello"` | `"hello"` |
| `true` / `false` | `true` / `false` |
| `null` | `null` |
| `0xFF` | `0xFF` |
| `0b1010` | `0b1010` |
| `1_000_000` | `1_000_000` |
| `1.5m` (decimal) | `/* TODO: decimal */` |

```csharp
// C#
long  x = 100L;
float f = 1.5f;
char  c = '\n';
bool  b = true;

// Java
long  x = 100L;
float f = 1.5f;
char  c = '\n';
boolean b = true;
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `decimal` literals emit `/* TODO: decimal */`

```csharp
decimal price = 9.99m;
```

`DecimalLiteralExpression` (the `m`/`M` suffix) produces `/* TODO: decimal */`. The correct Java mapping is `java.math.BigDecimal`:
```java
BigDecimal price = new BigDecimal("9.99");
```

The string form of the literal must be used (not the floating-point value) to preserve exact decimal representation. `import java.math.BigDecimal` must also be added.

### Issue 2 — `uint`/`ulong` values beyond Java max silently overflow

```csharp
uint  maxU   = uint.MaxValue;    // 4294967295 — exceeds Java int range (2147483647)
ulong bigUL  = 18446744073709551615UL;  // exceeds Java long range
```

When a literal of type `uint` or `ulong` exceeds the corresponding Java signed range, the transformer emits the literal as-is. Java parses it as an `int` or `long` literal and produces a compile error, or silently wraps to a negative value for `(int)` casts. The transformer should detect overflow and:
1. For `uint` values > `int.MaxValue`: emit as `long` with `L` suffix.
2. For `ulong` values > `long.MaxValue`: emit as `BigInteger` or emit a signed reinterpretation comment.

### Issue 3 — Raw string literals (C# 11 `"""..."""`) not distinguished from regular strings

C# 11 raw string literals:
```csharp
string json = """
{
  "key": "value"
}
""";
```

These map to Java 15+ text blocks:
```java
String json = """
        {
          "key": "value"
        }
        """;
```

The transformer currently processes all string literal tokens identically without checking whether the token is a raw-string-literal kind. Raw strings may contain unescaped backslashes or quotes that are valid in C# but produce invalid Java regular string literals.

### Issue 4 — UTF-8 string literals (C# 11 `u8"..."`) not handled

```csharp
ReadOnlySpan<byte> bytes = "hello"u8;
```

These are byte-array literals in C#. The transformer has no handler for `Utf8StringLiteralExpression`. This node is silently dropped or mishandled, producing incorrect Java.

### Issue 5 — `_` digit separators preserved but not validated for Java version

C# 7 digit separators (`1_000_000`) are also valid in Java 7+. The transformer preserves them, which is correct. However, if the target Java version is below 7 the separator is invalid. No version guard is applied.

## 3. Proposed Fixes

### Fix 1 — Map `decimal` to `BigDecimal`

```csharp
case SyntaxKind.NumericLiteralExpression when token.Text.EndsWith("m", StringComparison.OrdinalIgnoreCase):
    string decStr = token.Text.TrimEnd('m', 'M');
    context.AddImport("java.math.BigDecimal");
    return $"new BigDecimal(\"{decStr}\")";
```

### Fix 2 — Detect `uint`/`ulong` overflow and widen or warn

```csharp
if (typeSymbol?.SpecialType == SpecialType.System_UInt32 && longValue > int.MaxValue)
    return $"{longValue}L";   // widen to long
if (typeSymbol?.SpecialType == SpecialType.System_UInt64 && ulongValue > long.MaxValue)
{
    context.AddImport("java.math.BigInteger");
    return $"new BigInteger(\"{ulongValue}\")";
}
```

### Fix 3 — Detect raw string literals and emit Java text blocks

```csharp
if (token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) ||
    token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken))
{
    if (context.Options.JavaVersion >= JavaVersion.Java15)
    {
        string content = ExtractRawStringContent(token);
        return $"\"\"\"\n{content}\"\"\"";
    }
    // Fallback: escape and emit as regular string
}
```

### Fix 4 — Emit a byte-array literal for UTF-8 strings

```csharp
case SyntaxKind.Utf8StringLiteralExpression:
    byte[] bytes = Encoding.UTF8.GetBytes(ExtractStringValue(token));
    string byteArr = string.Join(", ", bytes.Select(b => $"(byte)0x{b:X2}"));
    return $"new byte[]{{ {byteArr} }}";
```

### Fix 5 — Guard digit separators on Java version

```csharp
if (context.Options.JavaVersion < JavaVersion.Java7 && literal.Contains('_'))
    literal = literal.Replace("_", "");
```

## 4. Commit Changes

```
fix(literal): map decimal to BigDecimal, widen uint/ulong overflow to long/BigInteger, emit Java text blocks for raw strings, translate u8 literals to byte arrays, and guard digit separators on Java version
```
