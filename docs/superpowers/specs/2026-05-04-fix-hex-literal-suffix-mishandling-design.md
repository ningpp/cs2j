# Fix: Hex literal suffix mishandling in TransformNumericLiteral

## Problem

C# hex integer literals ending in `D`, `d`, `F`, or `f` are incorrectly converted because the type-suffix detection logic treats trailing hex digits as C# numeric type suffixes.

**Example:** `0xFFFD` becomes `0xFFF.0` instead of `0xFFFD`.

## Root Cause

In `LiteralExpressionTransformer.TransformNumericLiteral()` (line 59), suffix checks for `f`/`F` (float) and `d`/`D` (double) run on ALL numeric literals, including hex (`0x...`) and binary (`0b...`). In C#, type suffixes only apply to decimal literals — for hex/binary literals, letters A-F are valid digit values, not suffixes.

For `0xFFFD`:
1. `EndsWith("D")` matches (D seen as double suffix)
2. `TrimEnd('d', 'D')` strips D → `0xFFF`
3. No `.` found → appends `.0` → `0xFFF.0`

The `f`/`F` suffix check (line 70) has the same latent bug.

## Fix

**File:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs`

Insert a hex/binary guard before line 69 (`// Handle suffixes`):

```csharp
if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
    || literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
    return literal;
```

Hex and binary literals are already valid Java syntax as-is — no suffix processing is needed.

## Tests

Add to `tests/CSharpToJava.Tests/DoubleLiteralSuffixTests.cs`:
- `0xFFFD` → `0xFFFD` (hex ending in D)
- `0xFF` → `0xFF` (hex ending in F)
- `0xABCDEF` → `0xABCDEF` (hex ending in F)
- `0b1010` → `0b1010` (binary, regression check)
