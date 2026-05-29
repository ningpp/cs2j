# CSharpRandom Design

## Goal

Add `io.github.ningpp.compat.CSharpRandom` to the compat library so that C# code using `System.Random` can be correctly converted to Java with full semantic fidelity, especially boundary conditions.

## Problem

Current `TypeMappings.json` maps `System.Random` → `java.util.Random` with method-level mappings like `Next` → `nextInt`. This produces incorrect behavior:

| C# Method | Current Mapping | Issue |
|---|---|---|
| `Next()` → [0, int.MaxValue) | `nextInt()` | Returns full int range including negatives |
| `Next(int maxValue)` | unmapped | Missing |
| `Next(int, int)` | unmapped | Missing |
| `NextSingle()` → [0.0f, 1.0f) | `nextFloat()` | May return 1.0f |
| `NextInt64()` → [0, long.MaxValue) | `nextLong()` | Returns full long range including negatives |
| `NextBytes(byte[])` → fills [0..255] via `(byte)Next()` | `nextBytes(byte[])` | Fills [-128..127] (signed bytes) |

## Approach: Wrap java.util.Random

`CSharpRandom` uses composition (not inheritance) over `java.util.Random`. Each public method strictly follows C# semantics. Same seed does NOT produce the same sequence as .NET (acceptable per user requirement).

## Class: `io.github.ningpp.compat.CSharpRandom`

### Fields

- `private final java.util.Random random`

### Constructors

- `CSharpRandom()` — delegates to `new java.util.Random()`
- `CSharpRandom(int seed)` — delegates to `new java.util.Random(seed)`

### Methods

#### `public int next()`
Returns [0, Integer.MAX_VALUE). Implementation: `random.nextInt(Integer.MAX_VALUE)`.

#### `public int next(int maxValue)`
Returns [0, maxValue). Throws `IllegalArgumentException` if `maxValue < 0`. Implementation: `random.nextInt(maxValue)` after validation.

Boundary: `maxValue == 0` throws (matches C# `ArgumentOutOfRangeException`); `maxValue == 1` always returns 0.

#### `public int next(int minValue, int maxValue)`
Returns [minValue, maxValue). Throws `IllegalArgumentException` if `minValue > maxValue`.

Logic (faithfully replicating .NET source `Random.cs` L195-211):

```
range = (long)maxValue - minValue
if range <= Integer.MAX_VALUE:
    return (int)(sample() * range) + minValue
else:
    return (int)((long)(getSampleForLargeRange() * range) + minValue)
```

Boundary: `minValue == maxValue` returns `minValue` (range=0, result=minValue).

#### `protected double sample()`
Returns [0.0, 1.0). Delegates to `random.nextDouble()`. Protected for subclass override.

#### `private double getSampleForLargeRange()`
Faithfully replicates .NET source `Random.cs` L168-186:

```java
int result = next();
boolean negative = (next() % 2 == 0);
if (negative) result = -result;
double d = result;
d += (Integer.MAX_VALUE - 1);
d /= 2.0 * (long)Integer.MAX_VALUE - 1;  // 4294967293.0
return d;
```

Key: `2 * (uint)int.MaxValue - 1` in C# equals `2L * Integer.MAX_VALUE - 1` = `4294967293L` in Java. Must use long arithmetic.

#### `public double nextDouble()`
Returns [0.0, 1.0). Delegates to `random.nextDouble()`.

#### `public float nextSingle()`
Returns [0.0f, 1.0f). Implementation: `(float)random.nextDouble()`. NOT `random.nextFloat()` to avoid the theoretical 1.0f boundary.

#### `public long nextInt64()`
Returns [0, Long.MAX_VALUE). Implementation: `(long)(sample() * Long.MAX_VALUE)`.

#### `public void nextBytes(byte[] buffer)`
Throws `NullPointerException` if `buffer == null`. Fills each byte with `(byte)next()`, which takes the low 8 bits of a [0, MAX_VALUE) int. This matches .NET's `(byte)InternalSample()` behavior.

## TypeMappings.json Changes

### Type mapping

```json
{
    "csharp": "System.Random",
    "java": "CSharpRandom",
    "imports": ["io.github.ningpp.compat.CSharpRandom"]
}
```

### Method mappings (replace existing)

```json
{ "type": "System.Random", "method": "Next", "javaMethod": "next" },
{ "type": "System.Random", "method": "NextDouble", "javaMethod": "nextDouble" },
{ "type": "System.Random", "method": "NextSingle", "javaMethod": "nextSingle" },
{ "type": "System.Random", "method": "NextInt64", "javaMethod": "nextInt64" },
{ "type": "System.Random", "method": "NextBytes", "javaMethod": "nextBytes" }
```

Note: `Next(int)` and `Next(int, int)` are overloads resolved by the converter's normal overload resolution — they map to `next(int)` and `next(int, int)` by name matching.

## Test Plan

`CSharpRandomTest.java` in `src/test/java/io/github/ningpp/compat/`:

1. Default constructor produces values in expected ranges
2. Seeded constructor produces repeatable sequences
3. `next()` always in [0, Integer.MAX_VALUE) — 10k samples
4. `next(maxValue)` with maxValue=1 always returns 0
5. `next(maxValue)` with maxValue<0 throws IllegalArgumentException
6. `next(minValue, maxValue)` with minValue==maxValue returns that value
7. `next(minValue, maxValue)` with minValue>maxValue throws IllegalArgumentException
8. `next(minValue, maxValue)` large range (Integer.MIN_VALUE to Integer.MAX_VALUE)
9. `nextDouble()` always in [0.0, 1.0) — 10k samples
10. `nextSingle()` always in [0.0f, 1.0f) — 10k samples
11. `nextInt64()` always in [0, Long.MAX_VALUE) — 10k samples
12. `nextBytes(byte[])` fills all bytes, null buffer throws NullPointerException

## Files to Create/Modify

| Action | File |
|---|---|
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpRandom.java` |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpRandomTest.java` |
| Modify | `config/TypeMappings.json` — type mapping + method mappings |
