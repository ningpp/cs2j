# Design: Fix TimeSpan→Duration property mapping and Stopwatch.Elapsed conversion

**Date:** 2026-05-05
**Status:** approved

## Problem

When converting C# code that uses `Stopwatch.Elapsed` (returning `TimeSpan`), two bugs produce invalid Java:

```csharp
// C# source
TimeSpan ts = sw.Elapsed;
Console.WriteLine("{0:00}:{1:00}:{2:00}.{3:000}", ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds);
```

```java
// Current (broken) output
Duration ts = sw.getElapsed();                                          // (1) getElapsed() doesn't exist on StopwatchHelper
writeLine("{0:00}:{1:00}:{2:00}.{3:000}", ts.getHours(), ts.getMinutes(), ts.getSeconds(), ts.getMilliseconds()); // (2) getHours() etc. don't exist on Duration
```

### Bug 1: TimeSpan properties map to wrong Duration methods

`TypeMappings.json` has zero `methodMappings` entries for `System.TimeSpan`. The fallback convention generates `get` + PascalCase (e.g., `getHours()`), but `java.time.Duration` uses `toXxxPart()` for component values and `toXxx()` for total values.

### Bug 2: StopwatchHelper lacks `getElapsed()`

The generated `StopwatchHelper` compat class only has `getElapsedMilliseconds()` returning `long`. There is no `getElapsed()` method returning `Duration`, so `sw.Elapsed` → `sw.getElapsed()` produces a call to a non-existent method.

### Current workaround

[Program.cs:1554-1557](src/CSharpToJava.CLI/Program.cs#L1554-1557) has a hardcoded string replacement for one specific file (`ResultVerifierBase.cs`). This doesn't fix the general case.

## Design

Three changes:

### 1. Add TimeSpan methodMappings to TypeMappings.json

Add entries in the `methodMappings` array:

| C# property | Java method | Notes |
|---|---|---|
| `Hours` | `toHoursPart()` | component (0-23) |
| `Minutes` | `toMinutesPart()` | component (0-59) |
| `Seconds` | `toSecondsPart()` | component (0-59) |
| `Milliseconds` | `toMillisPart()` | component (0-999) |
| `Days` | `toDaysPart()` | component |
| `TotalHours` | `toHours()` | total |
| `TotalMinutes` | `toMinutes()` | total |
| `TotalSeconds` | `toSeconds()` | total |
| `TotalMilliseconds` | `toMillis()` | total |
| `TotalDays` | `toDays()` | total |

`Ticks`, `Microseconds`, `Nanoseconds` are out of scope — they require arithmetic (Ticks → nanos/100, Microseconds → nanosPart/1000), not just method renaming.

### 2. Add `getElapsed()` to generated StopwatchHelper

In [CompatibilityClassGenerator.cs](src/CSharpToJava.Core/Pipeline/CompatibilityClassGenerator.cs), add to the generated Java class:

- `import java.time.Duration;`
- A `getElapsed()` method:
  ```java
  public Duration getElapsed() {
      long elapsed = elapsedNanos;
      if (running) elapsed += System.nanoTime() - startNanos;
      return Duration.ofNanos(elapsed);
  }
  ```

### 3. Remove CLI workaround

Delete the `Replace()` call at [Program.cs:1554-1557](src/CSharpToJava.CLI/Program.cs#L1554-1557).

## Affected files

| File | Change |
|---|---|
| `config/TypeMappings.json` | Add 10 methodMappings entries for System.TimeSpan |
| `src/CSharpToJava.Core/Pipeline/CompatibilityClassGenerator.cs` | Add import + `getElapsed()` method |
| `src/CSharpToJava.CLI/Program.cs` | Remove hardcoded Replace() workaround |

## Risks

- `toXxxPart()` methods require Java 9+. The project targets Java 25, so this is safe.
- If users have code that manually works around the current broken output, those workarounds will need updating.
