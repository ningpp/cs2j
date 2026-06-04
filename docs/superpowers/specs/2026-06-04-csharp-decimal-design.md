# CSharp Decimal Design

## Goal

Add `io.github.ningpp.compat.Decimal` to the Java compatibility library and update converter mappings so C# `decimal` / `System.Decimal` translates to this class instead of raw `java.math.BigDecimal`.

## Problem

The current converter maps `System.Decimal` to `BigDecimal` and decimal literals to `new BigDecimal("...")`. `BigDecimal` is useful as an implementation detail, but it does not enforce C# `decimal` semantics:

| C# Decimal Requirement | Raw BigDecimal Behavior | Issue |
|---|---|---|
| 96-bit integer payload | Arbitrary precision | Missing overflow checks |
| Scale limited to 0..28 | Arbitrary scale | Invalid decimal values can exist |
| Results must fit decimal range | Arithmetic can grow freely | Missing overflow semantics |
| C# midpoint rounding APIs | Java defaults differ by API | Rounding semantics drift |
| `decimal.Parse` / `TryParse` boundaries | BigDecimal parsing is wider | Invalid C# decimals may be accepted |

The compatibility runtime needs a dedicated type that can use `BigDecimal` internally while exposing C#-compatible behavior at every public boundary.

## Approach: Decimal Wrapper with C# Boundary Enforcement

`Decimal` is an immutable `final` class in `io.github.ningpp.compat`. It stores a canonical `BigDecimal` internally and validates all constructors, factories, parse methods, and arithmetic results against C# decimal limits. Performance is not a goal, so implementation can prefer simple `BigDecimal` operations followed by normalization and range checks.

The class should not extend `BigDecimal`. Composition keeps the public API under control and prevents callers from bypassing C# decimal validation through inherited arbitrary-precision methods.

## Class: `io.github.ningpp.compat.Decimal`

### Representation

- `private static final int MAX_SCALE = 28`
- `private static final BigInteger MAX_MANTISSA = new BigInteger("79228162514264337593543950335")`
- `private static final BigDecimal MAX_VALUE_BIG_DECIMAL`
- `private static final BigDecimal MIN_VALUE_BIG_DECIMAL`
- `private final BigDecimal value`

Canonicalization rules:

- Values must have scale `0..28` after construction or operation.
- Inputs with more than 28 fractional digits are rounded to the nearest representable decimal when they can still fit in range, matching `Decimal.Parse`.
- Trailing zeroes may be removed for equality and hashing, but zero must canonicalize to scale `0`.
- Any non-zero value whose absolute unscaled integer exceeds `MAX_MANTISSA` after scale normalization must throw `ArithmeticException`.
- Negative zero is not preserved; C# decimal treats it as numerically zero for ordinary arithmetic and comparison.

### Constants

- `ZERO`
- `ONE`
- `MINUS_ONE`
- `MAX_VALUE`
- `MIN_VALUE`

### Factories and Constructors

- `Decimal(int value)`
- `Decimal(long value)`
- `Decimal(double value)`
- `Decimal(BigDecimal value)`
- `static Decimal valueOf(int value)`
- `static Decimal valueOf(long value)`
- `static Decimal valueOf(double value)`
- `static Decimal valueOf(BigDecimal value)`
- `static Decimal parse(String value)`
- `static boolean tryParse(String value, ObjectHolder<Decimal> result)`

`double` construction should use `BigDecimal.valueOf(double)` and then validate decimal range. `NaN` and infinities must throw `NumberFormatException` or `IllegalArgumentException`.

### Arithmetic

Instance methods:

- `add(Decimal other)`
- `subtract(Decimal other)`
- `multiply(Decimal other)`
- `divide(Decimal other)`
- `remainder(Decimal other)`
- `negate()`
- `abs()`

Static aliases:

- `add(Decimal left, Decimal right)`
- `subtract(Decimal left, Decimal right)`
- `multiply(Decimal left, Decimal right)`
- `divide(Decimal left, Decimal right)`
- `remainder(Decimal left, Decimal right)`
- `negate(Decimal value)`

Division must throw `ArithmeticException` for division by zero. If the exact quotient has more than 28 fractional digits, round to 28 fractional digits using C# decimal division behavior: nearest representable decimal, ties to even.

### Rounding and Integral Operations

- `round()`
- `round(int decimals)`
- `round(int decimals, RoundingMode mode)`
- `truncate()`
- `floor()`
- `ceiling()`

`round()` and `round(int)` should use midpoint-to-even by default, matching `decimal.Round`. `decimals` must be `0..28`.

### Conversions and Formatting

- `toBigDecimal()`
- `intValue()`
- `longValue()`
- `doubleValue()`
- `floatValue()`
- `toString()`
- `toStringPlain()`

Integral conversions must throw `ArithmeticException` if fractional digits would be lost or if the target type overflows. `toString()` should emit a plain decimal representation without scientific notation, matching common C# decimal formatting behavior for converted source output.

### Comparison and Equality

- Implements `Comparable<Decimal>`
- `compareTo(Decimal other)` compares numeric value.
- `equals(Object other)` treats `1.0m` and `1.00m` as equal.
- `hashCode()` must be consistent with numeric equality.

## TypeMappings.json Changes

Replace the existing decimal type mapping:

```json
{
  "csharp": "System.Decimal",
  "java": "Decimal",
  "imports": [
    "io.github.ningpp.compat.Decimal"
  ]
}
```

The converter's primitive fallback for `decimal` should also return `Decimal`, not `double`.

## Converter-Side Literal Changes

Decimal literals should no longer emit `new BigDecimal("...")`. They should emit:

```java
Decimal.parse("123.45")
```

The literal transformer must import `io.github.ningpp.compat.Decimal` for decimal literals. This keeps generated code inside the new compatibility semantics.

## Test Plan

`DecimalTest.java` in `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/`:

1. Constants expose C# decimal min, max, zero, one, and minus one.
2. Constructors and factories accept valid int, long, double, string, and BigDecimal inputs.
3. Parse accepts valid decimal strings, rounds excess fractional precision to the nearest representable decimal, and rejects null, empty, non-numeric, NaN, infinity, and out-of-range values.
4. TryParse returns false and writes `Decimal.ZERO` to the holder on invalid input, matching C# `out` parameter behavior.
5. Add, subtract, multiply, divide, and remainder match expected decimal values.
6. Arithmetic overflows throw `ArithmeticException`.
7. Division by zero throws `ArithmeticException`.
8. Division rounds non-terminating quotients to 28 digits using midpoint-to-even behavior.
9. Round, truncate, floor, and ceiling match C# decimal examples for positive and negative values.
10. Integral conversions throw on fractional values and overflow.
11. Equality and hash code treat numerically equal values with different scales as equal.
12. `toString()` emits plain decimal text.

Converter tests in `tests/CSharpToJava.Tests/`:

1. `decimal` local declarations use `Decimal` and import `io.github.ningpp.compat.Decimal`.
2. `System.Decimal` references use `Decimal`.
3. Decimal literals emit `Decimal.parse("...")` instead of `new BigDecimal("...")`.

## Files to Create/Modify

| Action | File |
|---|---|
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java` |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/DecimalTest.java` |
| Modify | `config/TypeMappings.json` |
| Modify | `src/CSharpToJava.Core/Context/TypeMappingService.cs` |
| Modify | `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs` |
| Create or modify | `tests/CSharpToJava.Tests/DecimalMappingTests.cs` |
