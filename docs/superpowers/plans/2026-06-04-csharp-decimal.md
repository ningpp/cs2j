# CSharp Decimal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a C#-compatible `Decimal` runtime class and update converter mappings/literals to emit it.

**Architecture:** Implement an immutable `io.github.ningpp.compat.Decimal` wrapper over `BigDecimal` with C# decimal range and scale validation at every public boundary. Update type mapping and literal conversion so generated Java uses this wrapper instead of raw `BigDecimal`.

**Tech Stack:** Java 25, Maven, JUnit 5, C# Roslyn converter, XUnit.

---

### Task 1: Compat Decimal Test Surface

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/DecimalTest.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java`

- [ ] **Step 1: Write failing Decimal tests**

Create `DecimalTest.java` with tests for constants, parse/tryParse, arithmetic, overflow, rounding, conversions, equality, and formatting.

```java
package io.github.ningpp.compat;

import java.math.BigDecimal;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class DecimalTest {

    @Test
    void constants_matchCSharpBounds() {
        assertEquals("0", Decimal.ZERO.toString());
        assertEquals("1", Decimal.ONE.toString());
        assertEquals("-1", Decimal.MINUS_ONE.toString());
        assertEquals("79228162514264337593543950335", Decimal.MAX_VALUE.toString());
        assertEquals("-79228162514264337593543950335", Decimal.MIN_VALUE.toString());
    }

    @Test
    void parse_validValues() {
        assertEquals("123.45", Decimal.parse("123.45").toString());
        assertEquals("-123.45", Decimal.parse("-123.45").toString());
        assertEquals("1", Decimal.parse("1.00").toString());
        assertEquals("0.0000000000000000000000000001",
            Decimal.parse("0.0000000000000000000000000001").toString());
    }

    @Test
    void parse_rejectsInvalidValues() {
        assertThrows(NumberFormatException.class, () -> Decimal.parse(null));
        assertThrows(NumberFormatException.class, () -> Decimal.parse(""));
        assertThrows(NumberFormatException.class, () -> Decimal.parse("abc"));
        assertThrows(NumberFormatException.class, () -> Decimal.parse("NaN"));
        assertThrows(NumberFormatException.class, () -> Decimal.parse("Infinity"));
        assertEquals("0", Decimal.parse("0.00000000000000000000000000001").toString());
        assertEquals("0.0000000000000000000000000001",
            Decimal.parse("0.00000000000000000000000000006").toString());
        assertThrows(ArithmeticException.class,
            () -> Decimal.parse("79228162514264337593543950336"));
    }

    @Test
    void tryParse_invalidSetsHolderToZero() {
        ObjectHolder<Decimal> holder = new ObjectHolder<>(Decimal.ONE);
        assertFalse(Decimal.tryParse("abc", holder));
        assertSame(Decimal.ZERO, holder.value);
    }

    @Test
    void tryParse_validSetsHolder() {
        ObjectHolder<Decimal> holder = new ObjectHolder<>();
        assertTrue(Decimal.tryParse("42.5", holder));
        assertEquals(Decimal.parse("42.5"), holder.value);
    }

    @Test
    void arithmetic_basicOperations() {
        Decimal a = Decimal.parse("10.5");
        Decimal b = Decimal.parse("2");
        assertEquals("12.5", a.add(b).toString());
        assertEquals("8.5", a.subtract(b).toString());
        assertEquals("21", a.multiply(b).toString());
        assertEquals("5.25", a.divide(b).toString());
        assertEquals("0.5", a.remainder(b).toString());
    }

    @Test
    void arithmetic_overflowThrows() {
        assertThrows(ArithmeticException.class, () -> Decimal.MAX_VALUE.add(Decimal.ONE));
        assertThrows(ArithmeticException.class, () -> Decimal.MIN_VALUE.subtract(Decimal.ONE));
        assertThrows(ArithmeticException.class, () -> Decimal.MAX_VALUE.multiply(Decimal.parse("2")));
    }

    @Test
    void divideByZeroThrows() {
        assertThrows(ArithmeticException.class, () -> Decimal.ONE.divide(Decimal.ZERO));
    }

    @Test
    void divide_nonTerminatingRoundsToTwentyEightPlaces() {
        assertEquals("0.3333333333333333333333333333",
            Decimal.ONE.divide(Decimal.parse("3")).toString());
        assertEquals("0.6666666666666666666666666667",
            Decimal.parse("2").divide(Decimal.parse("3")).toString());
    }

    @Test
    void roundingAndIntegralOperations_matchCSharpStyle() {
        assertEquals("2", Decimal.parse("2.5").round().toString());
        assertEquals("4", Decimal.parse("3.5").round().toString());
        assertEquals("-2", Decimal.parse("-2.5").round().toString());
        assertEquals("1.24", Decimal.parse("1.235").round(2).toString());
        assertEquals("-1", Decimal.parse("-1.9").truncate().toString());
        assertEquals("-2", Decimal.parse("-1.1").floor().toString());
        assertEquals("-1", Decimal.parse("-1.1").ceiling().toString());
    }

    @Test
    void integralConversions_validateFractionAndRange() {
        assertEquals(42, Decimal.parse("42").intValue());
        assertEquals(42L, Decimal.parse("42").longValue());
        assertThrows(ArithmeticException.class, () -> Decimal.parse("42.1").intValue());
        assertThrows(ArithmeticException.class, () -> Decimal.parse("2147483648").intValue());
        assertThrows(ArithmeticException.class, () -> Decimal.parse("9223372036854775808").longValue());
    }

    @Test
    void equalityAndHashCode_ignoreScale() {
        Decimal a = Decimal.parse("1.0");
        Decimal b = Decimal.parse("1.00");
        assertEquals(a, b);
        assertEquals(a.hashCode(), b.hashCode());
        assertEquals(0, a.compareTo(b));
    }

    @Test
    void factories_validateInputs() {
        assertEquals("42", Decimal.valueOf(42).toString());
        assertEquals("42", Decimal.valueOf(42L).toString());
        assertEquals("42.5", Decimal.valueOf(42.5d).toString());
        assertEquals("42.5", Decimal.valueOf(new BigDecimal("42.500")).toString());
        assertThrows(NumberFormatException.class, () -> Decimal.valueOf(Double.NaN));
        assertThrows(NumberFormatException.class, () -> Decimal.valueOf(Double.POSITIVE_INFINITY));
    }
}
```

- [ ] **Step 2: Add a minimal placeholder class**

Create `Decimal.java` with only enough structure for compilation to fail on missing methods meaningfully.

```java
package io.github.ningpp.compat;

public final class Decimal {
}
```

- [ ] **Step 3: Run tests to verify RED**

Run: `mvn -q -f java/csharptojava-compat/pom.xml test -Dtest=DecimalTest`

Expected: compilation failure listing missing `Decimal` members such as `ZERO`, `parse`, and `valueOf`.

### Task 2: Compat Decimal Implementation

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java`
- Test: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/DecimalTest.java`

- [ ] **Step 1: Implement Decimal**

Replace `Decimal.java` with an immutable `BigDecimal` wrapper implementing all methods used by `DecimalTest`.

```java
package io.github.ningpp.compat;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.util.Objects;

public final class Decimal implements Comparable<Decimal> {
    public static final int MAX_SCALE = 28;

    private static final BigInteger MAX_MANTISSA =
        new BigInteger("79228162514264337593543950335");
    private static final BigDecimal MAX_BIG_DECIMAL = new BigDecimal(MAX_MANTISSA);
    private static final BigDecimal MIN_BIG_DECIMAL = MAX_BIG_DECIMAL.negate();

    public static final Decimal ZERO = new Decimal(BigDecimal.ZERO, false);
    public static final Decimal ONE = new Decimal(BigDecimal.ONE, false);
    public static final Decimal MINUS_ONE = new Decimal(BigDecimal.ONE.negate(), false);
    public static final Decimal MAX_VALUE = new Decimal(MAX_BIG_DECIMAL, false);
    public static final Decimal MIN_VALUE = new Decimal(MIN_BIG_DECIMAL, false);

    private final BigDecimal value;

    public Decimal(int value) {
        this(BigDecimal.valueOf(value), true);
    }

    public Decimal(long value) {
        this(BigDecimal.valueOf(value), true);
    }

    public Decimal(double value) {
        this(fromDouble(value), true);
    }

    public Decimal(BigDecimal value) {
        this(value, true);
    }

    private Decimal(BigDecimal value, boolean validate) {
        if (value == null) {
            throw new NullPointerException("value");
        }
        BigDecimal normalized = canonicalize(value);
        if (validate) {
            validateRange(normalized);
        }
        this.value = normalized;
    }

    public static Decimal valueOf(int value) {
        return new Decimal(value);
    }

    public static Decimal valueOf(long value) {
        return new Decimal(value);
    }

    public static Decimal valueOf(double value) {
        return new Decimal(value);
    }

    public static Decimal valueOf(BigDecimal value) {
        return new Decimal(value);
    }

    public static Decimal parse(String value) {
        if (value == null || value.isEmpty()) {
            throw new NumberFormatException(value == null ? "null" : "empty");
        }
        return new Decimal(new BigDecimal(value), true);
    }

    public static boolean tryParse(String value, ObjectHolder<Decimal> result) {
        try {
            Decimal parsed = parse(value);
            result.value = parsed;
            return true;
        } catch (RuntimeException ex) {
            return false;
        }
    }

    public Decimal add(Decimal other) {
        return checked(value.add(require(other).value));
    }

    public static Decimal add(Decimal left, Decimal right) {
        return require(left).add(right);
    }

    public Decimal subtract(Decimal other) {
        return checked(value.subtract(require(other).value));
    }

    public static Decimal subtract(Decimal left, Decimal right) {
        return require(left).subtract(right);
    }

    public Decimal multiply(Decimal other) {
        return checked(value.multiply(require(other).value));
    }

    public static Decimal multiply(Decimal left, Decimal right) {
        return require(left).multiply(right);
    }

    public Decimal divide(Decimal other) {
        Decimal divisor = require(other);
        if (divisor.value.signum() == 0) {
            throw new ArithmeticException("Division by zero");
        }
        return checked(value.divide(divisor.value, MAX_SCALE, RoundingMode.HALF_EVEN));
    }

    public static Decimal divide(Decimal left, Decimal right) {
        return require(left).divide(right);
    }

    public Decimal remainder(Decimal other) {
        Decimal divisor = require(other);
        if (divisor.value.signum() == 0) {
            throw new ArithmeticException("Division by zero");
        }
        return checked(value.remainder(divisor.value));
    }

    public static Decimal remainder(Decimal left, Decimal right) {
        return require(left).remainder(right);
    }

    public Decimal negate() {
        return checked(value.negate());
    }

    public static Decimal negate(Decimal value) {
        return require(value).negate();
    }

    public Decimal abs() {
        return value.signum() < 0 ? negate() : this;
    }

    public Decimal round() {
        return round(0);
    }

    public Decimal round(int decimals) {
        return round(decimals, RoundingMode.HALF_EVEN);
    }

    public Decimal round(int decimals, RoundingMode mode) {
        validateScaleArgument(decimals);
        return checked(value.setScale(decimals, Objects.requireNonNull(mode, "mode")));
    }

    public Decimal truncate() {
        return checked(value.setScale(0, RoundingMode.DOWN));
    }

    public Decimal floor() {
        return checked(value.setScale(0, RoundingMode.FLOOR));
    }

    public Decimal ceiling() {
        return checked(value.setScale(0, RoundingMode.CEILING));
    }

    public BigDecimal toBigDecimal() {
        return value;
    }

    public int intValue() {
        return value.intValueExact();
    }

    public long longValue() {
        return value.longValueExact();
    }

    public double doubleValue() {
        return value.doubleValue();
    }

    public float floatValue() {
        return value.floatValue();
    }

    public String toStringPlain() {
        return value.toPlainString();
    }

    @Override
    public String toString() {
        return toStringPlain();
    }

    @Override
    public int compareTo(Decimal other) {
        return value.compareTo(require(other).value);
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof Decimal decimal && compareTo(decimal) == 0;
    }

    @Override
    public int hashCode() {
        return canonicalForEquality(value).hashCode();
    }

    private static Decimal checked(BigDecimal value) {
        return new Decimal(value, true);
    }

    private static Decimal require(Decimal value) {
        return Objects.requireNonNull(value, "value");
    }

    private static BigDecimal fromDouble(double value) {
        if (!Double.isFinite(value)) {
            throw new NumberFormatException("Value was either too large or too small for a Decimal.");
        }
        return BigDecimal.valueOf(value);
    }

    private static BigDecimal canonicalize(BigDecimal value) {
        BigDecimal stripped = value.stripTrailingZeros();
        if (stripped.signum() == 0) {
            return BigDecimal.ZERO;
        }
        if (stripped.scale() < 0) {
            stripped = stripped.setScale(0);
        }
        if (stripped.scale() > MAX_SCALE) {
            throw new ArithmeticException("Decimal scale exceeds 28.");
        }
        return stripped;
    }

    private static BigDecimal canonicalForEquality(BigDecimal value) {
        BigDecimal stripped = value.stripTrailingZeros();
        if (stripped.signum() == 0) {
            return BigDecimal.ZERO;
        }
        return stripped.scale() < 0 ? stripped.setScale(0) : stripped;
    }

    private static void validateRange(BigDecimal value) {
        if (value.compareTo(MAX_BIG_DECIMAL) > 0 || value.compareTo(MIN_BIG_DECIMAL) < 0) {
            throw new ArithmeticException("Decimal overflow.");
        }
        BigInteger scaledInteger = value.movePointRight(Math.max(value.scale(), 0)).toBigIntegerExact().abs();
        if (scaledInteger.compareTo(MAX_MANTISSA) > 0) {
            throw new ArithmeticException("Decimal overflow.");
        }
    }

    private static void validateScaleArgument(int decimals) {
        if (decimals < 0 || decimals > MAX_SCALE) {
            throw new IllegalArgumentException("decimals must be between 0 and 28");
        }
    }
}
```

- [ ] **Step 2: Run Decimal tests to verify GREEN**

Run: `mvn -q -f java/csharptojava-compat/pom.xml test -Dtest=DecimalTest`

Expected: all `DecimalTest` tests pass.

- [ ] **Step 3: Run full compat Maven tests**

Run: `mvn -q -f java/csharptojava-compat/pom.xml test`

Expected: all compat tests pass.

### Task 3: Converter Decimal Mapping Tests

**Files:**
- Create: `tests/CSharpToJava.Tests/DecimalMappingTests.cs`

- [ ] **Step 1: Write failing converter mapping tests**

Create `DecimalMappingTests.cs` using the existing conversion pipeline test style.

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class DecimalMappingTests
{
    [Fact]
    public void DecimalLocalDeclaration_UsesCompatDecimal()
    {
        const string source = """
            using System;
            class Test
            {
                decimal GetValue()
                {
                    decimal value = 1.23m;
                    return value;
                }
            }
            """;

        string java = Convert(source);

        Assert.Contains("import io.github.ningpp.compat.Decimal;", java);
        Assert.Contains("Decimal getValue()", java);
        Assert.Contains("Decimal value = Decimal.parse(\"1.23\")", java);
        Assert.DoesNotContain("BigDecimal", java);
        Assert.DoesNotContain("double getValue()", java);
    }

    [Fact]
    public void SystemDecimalReference_UsesCompatDecimal()
    {
        const string source = """
            using System;
            class Test
            {
                Decimal GetValue()
                {
                    return Decimal.Parse("42.5");
                }
            }
            """;

        string java = Convert(source);

        Assert.Contains("import io.github.ningpp.compat.Decimal;", java);
        Assert.Contains("Decimal getValue()", java);
        Assert.Contains("Decimal.parse(\"42.5\")", java);
    }

    private static string Convert(string source)
    {
        var pipeline = new ConversionPipeline(new ConversionOptions());
        var result = pipeline.ConvertSource(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.JavaCode;
    }
}
```

- [ ] **Step 2: Run tests to verify RED**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~DecimalMappingTests`

Expected: tests fail because generated code still uses `BigDecimal` and/or `double`.

### Task 4: Converter Mapping Implementation

**Files:**
- Modify: `config/TypeMappings.json`
- Modify: `src/CSharpToJava.Core/Context/TypeMappingService.cs`
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs`
- Test: `tests/CSharpToJava.Tests/DecimalMappingTests.cs`

- [ ] **Step 1: Update TypeMappings.json**

Change the existing `System.Decimal` type mapping from `BigDecimal` to `Decimal` and imports from `java.math.BigDecimal` to `io.github.ningpp.compat.Decimal`.

Expected JSON entry:

```json
{
  "csharp": "System.Decimal",
  "java": "Decimal",
  "imports": [
    "io.github.ningpp.compat.Decimal"
  ]
}
```

- [ ] **Step 2: Update primitive fallback mapping**

In `TypeMappingService.cs`, update the `decimal` fallback case from `double` to `Decimal`.

Expected relevant code:

```csharp
"decimal" => "Decimal",
```

Ensure the code path also adds the `io.github.ningpp.compat.Decimal` import if primitive mappings are handled outside `TypeMappings.json`.

- [ ] **Step 3: Update decimal literal conversion**

In `LiteralExpressionTransformer.cs`, change decimal literal generation from `new BigDecimal("...")` to `Decimal.parse("...")` and import `io.github.ningpp.compat.Decimal`.

Expected relevant code:

```csharp
context.AddImport("io.github.ningpp.compat.Decimal");
return $"Decimal.parse(\"{decStr}\")";
```

- [ ] **Step 4: Run converter mapping tests to verify GREEN**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~DecimalMappingTests`

Expected: all `DecimalMappingTests` pass.

### Task 5: Final Verification

**Files:**
- All modified files from previous tasks

- [ ] **Step 1: Run compat tests**

Run: `mvn -q -f java/csharptojava-compat/pom.xml test`

Expected: all Maven tests pass.

- [ ] **Step 2: Run focused .NET tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter FullyQualifiedName~DecimalMappingTests`

Expected: all Decimal mapping tests pass.

- [ ] **Step 3: Run full .NET test suite if focused tests are clean**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`

Expected: all tests pass. If unrelated pre-existing null-conditional tests fail due to the dirty worktree, record the failure and rerun Decimal-focused tests as the completion gate.
