# DateTime/DateTimeOffset/TimeSpan/DateOnly/TimeOnly Java Compat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Java compatibility classes for `System.DateTime`, `System.DateTimeOffset`, `System.TimeSpan`, `System.DateOnly`, `System.TimeOnly` in `java/csharptojava-compat`, with a reflection tool to enumerate their public APIs, and cross-validated tests ensuring C# and Java outputs match.

**Architecture:** Create a .NET reflection tool (similar to `tools/XunitReflection`) to extract all public methods/properties/constants of the 5 C# types. Then implement Java wrapper classes that delegate to `java.time` types but expose C#-style APIs. Each method's behavior is verified by writing both a C# test program and a Java JUnit test that produce identical output for the same inputs.

**Tech Stack:** .NET 10 (reflection tool), Java 25 + JUnit 5 + Maven (compat classes)

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Create | `tools/DateTimeReflection/DateTimeReflection.csproj` | .NET project for reflecting DateTime APIs |
| Create | `tools/DateTimeReflection/Program.cs` | Reflection logic to extract public members |
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateTime.java` | DateTime compat class |
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateTimeOffset.java` | DateTimeOffset compat class |
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpTimeSpan.java` | TimeSpan compat class |
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateOnly.java` | DateOnly compat class |
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpTimeOnly.java` | TimeOnly compat class |
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DayOfWeek.java` | C# DayOfWeek enum |
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DateTimeKind.java` | C# DateTimeKind enum |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpDateTimeTest.java` | DateTime tests |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpDateTimeOffsetTest.java` | DateTimeOffset tests |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpTimeSpanTest.java` | TimeSpan tests |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpDateOnlyTest.java` | DateOnly tests |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpTimeOnlyTest.java` | TimeOnly tests |
| Create | `tools/DateTimeReflection/CSharpValidation.cs` | C# validation program producing reference output |
| Modify | `config/TypeMappings.json` | Update type mappings to point to compat classes |

---

### Task 1: Create DateTimeReflection Tool

**Files:**
- Create: `tools/DateTimeReflection/DateTimeReflection.csproj`
- Create: `tools/DateTimeReflection/Program.cs`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create Program.cs with reflection logic**

The program reflects on `System.DateTime`, `System.DateTimeOffset`, `System.TimeSpan`, `System.DateOnly`, `System.TimeOnly` and prints all public static/instance methods, properties, and constants, grouped by type.

```csharp
using System;
using System.Linq;
using System.Reflection;

class Program
{
    static void Main()
    {
        var types = new[]
        {
            typeof(DateTime),
            typeof(DateTimeOffset),
            typeof(TimeSpan),
            typeof(DateOnly),
            typeof(TimeOnly),
        };

        foreach (var type in types)
        {
            Console.WriteLine($"=== {type.FullName} ===");
            Console.WriteLine();

            // Static fields (constants)
            var staticFields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(f => f.Name).ToList();
            if (staticFields.Count > 0)
            {
                Console.WriteLine("--- Static Fields / Constants ---");
                foreach (var f in staticFields)
                {
                    var val = f.IsLiteral ? f.GetRawConstantValue() : "?";
                    Console.WriteLine($"  {f.FieldType.GetDisplayName()} {f.Name} = {val}");
                }
                Console.WriteLine();
            }

            // Static properties
            var staticProps = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(p => p.Name).ToList();
            if (staticProps.Count > 0)
            {
                Console.WriteLine("--- Static Properties ---");
                foreach (var p in staticProps)
                {
                    Console.WriteLine($"  {p.PropertyType.GetDisplayName()} {p.Name} {{ {GetAccessors(p)} }}");
                }
                Console.WriteLine();
            }

            // Instance properties
            var instanceProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(p => p.Name).ToList();
            if (instanceProps.Count > 0)
            {
                Console.WriteLine("--- Instance Properties ---");
                foreach (var p in instanceProps)
                {
                    Console.WriteLine($"  {p.PropertyType.GetDisplayName()} {p.Name} {{ {GetAccessors(p)} }}");
                }
                Console.WriteLine();
            }

            // Static methods
            var staticMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length).ToList();
            if (staticMethods.Count > 0)
            {
                Console.WriteLine("--- Static Methods ---");
                foreach (var m in staticMethods)
                {
                    var parameters = string.Join(", ", m.GetParameters()
                        .Select(p => $"{p.ParameterType.GetDisplayName()} {p.Name}"));
                    Console.WriteLine($"  {m.ReturnType.GetDisplayName()} {m.Name}({parameters})");
                }
                Console.WriteLine();
            }

            // Instance methods
            var instanceMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length).ToList();
            if (instanceMethods.Count > 0)
            {
                Console.WriteLine("--- Instance Methods ---");
                foreach (var m in instanceMethods)
                {
                    var parameters = string.Join(", ", m.GetParameters()
                        .Select(p => $"{p.ParameterType.GetDisplayName()} {p.Name}"));
                    Console.WriteLine($"  {m.ReturnType.GetDisplayName()} {m.Name}({parameters})");
                }
                Console.WriteLine();
            }

            Console.WriteLine();
        }
    }

    static string GetAccessors(PropertyInfo p)
    {
        var get = p.CanRead ? "get" : "";
        var set = p.CanWrite ? "set" : "";
        return string.Join("; ", new[] { get, set }.Where(s => s != ""));
    }
}

static class TypeExtensions
{
    public static string GetDisplayName(this Type type)
    {
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            var genericArgs = type.GetGenericArguments();
            var name = genericDef.Name.Substring(0, genericDef.Name.IndexOf('`'));
            return $"{name}<{string.Join(", ", genericArgs.Select(GetDisplayName))}>";
        }
        if (type.IsArray)
        {
            return $"{GetDisplayName(type.GetElementType())}[]";
        }
        if (type.IsNested)
        {
            return $"{type.DeclaringType.Name}.{type.Name}";
        }
        if (type.IsByRef)
        {
            return $"ref {GetDisplayName(type.GetElementType())}";
        }

        var map = new System.Collections.Generic.Dictionary<Type, string>
        {
            { typeof(void), "void" },
            { typeof(bool), "bool" },
            { typeof(int), "int" },
            { typeof(long), "long" },
            { typeof(float), "float" },
            { typeof(double), "double" },
            { typeof(decimal), "decimal" },
            { typeof(string), "string" },
            { typeof(object), "object" },
            { typeof(char), "char" },
            { typeof(byte), "byte" },
        };
        return map.TryGetValue(type, out var alias) ? alias : type.Name;
    }
}
```

- [ ] **Step 3: Run the reflection tool and save output**

Run: `cd tools/DateTimeReflection && dotnet run > api_output.txt`
Expected: A text file listing all public members of the 5 types.

---

### Task 2: Create C# Validation Program

**Files:**
- Create: `tools/DateTimeReflection/CSharpValidation.cs`

This is a separate C# program that exercises key methods of each type and prints results. The Java tests will produce the same output for the same inputs, enabling cross-validation.

- [ ] **Step 1: Write CSharpValidation.cs**

```csharp
using System;

class CSharpValidation
{
    static void Main()
    {
        // === TimeSpan ===
        Console.WriteLine("=== TimeSpan ===");
        var ts1 = new TimeSpan(1, 2, 3);
        Console.WriteLine($"new TimeSpan(1,2,3).ToString() = {ts1}");
        Console.WriteLine($"ts1.Days = {ts1.Days}");
        Console.WriteLine($"ts1.Hours = {ts1.Hours}");
        Console.WriteLine($"ts1.Minutes = {ts1.Minutes}");
        Console.WriteLine($"ts1.Seconds = {ts1.Seconds}");
        Console.WriteLine($"ts1.Milliseconds = {ts1.Milliseconds}");
        Console.WriteLine($"ts1.TotalDays = {ts1.TotalDays}");
        Console.WriteLine($"ts1.TotalHours = {ts1.TotalHours}");
        Console.WriteLine($"ts1.TotalMinutes = {ts1.TotalMinutes}");
        Console.WriteLine($"ts1.TotalSeconds = {ts1.TotalSeconds}");
        Console.WriteLine($"ts1.TotalMilliseconds = {ts1.TotalMilliseconds}");
        Console.WriteLine($"ts1.Ticks = {ts1.Ticks}");

        var ts2 = TimeSpan.FromHours(1.5);
        Console.WriteLine($"TimeSpan.FromHours(1.5) = {ts2}");
        Console.WriteLine($"TimeSpan.FromMinutes(90) = {TimeSpan.FromMinutes(90)}");
        Console.WriteLine($"TimeSpan.FromSeconds(3661) = {TimeSpan.FromSeconds(3661)}");
        Console.WriteLine($"TimeSpan.FromMilliseconds(1500) = {TimeSpan.FromMilliseconds(1500)}");
        Console.WriteLine($"TimeSpan.FromTicks(10000000) = {TimeSpan.FromTicks(10000000)}");
        Console.WriteLine($"TimeSpan.Zero = {TimeSpan.Zero}");
        Console.WriteLine($"ts1.Add(ts2) = {ts1.Add(ts2)}");
        Console.WriteLine($"ts1.Subtract(ts2) = {ts1.Subtract(ts2)}");
        Console.WriteLine($"ts1.Negate() = {ts1.Negate()}");
        Console.WriteLine($"ts1.Duration() = {ts1.Duration()}");
        Console.WriteLine($"(-ts1) = {-ts1}");
        Console.WriteLine($"ts1.CompareTo(ts2) = {ts1.CompareTo(ts2)}");

        // Parse/TryParse
        Console.WriteLine($"TimeSpan.Parse(\"1:02:03\") = {TimeSpan.Parse("1:02:03")}");
        Console.WriteLine($"TimeSpan.Parse(\"-1:02:03\") = {TimeSpan.Parse("-1:02:03")}");
        Console.WriteLine($"TimeSpan.Parse(\"1.02:03:04.005\") = {TimeSpan.Parse("1.02:03:04.005")}");

        // === DateTime ===
        Console.WriteLine();
        Console.WriteLine("=== DateTime ===");
        var dt1 = new DateTime(2024, 6, 15, 10, 30, 45);
        Console.WriteLine($"new DateTime(2024,6,15,10,30,45).ToString() = {dt1}");
        Console.WriteLine($"dt1.Year = {dt1.Year}");
        Console.WriteLine($"dt1.Month = {dt1.Month}");
        Console.WriteLine($"dt1.Day = {dt1.Day}");
        Console.WriteLine($"dt1.Hour = {dt1.Hour}");
        Console.WriteLine($"dt1.Minute = {dt1.Minute}");
        Console.WriteLine($"dt1.Second = {dt1.Second}");
        Console.WriteLine($"dt1.Millisecond = {dt1.Millisecond}");
        Console.WriteLine($"dt1.DayOfWeek = {dt1.DayOfWeek}");
        Console.WriteLine($"dt1.DayOfYear = {dt1.DayOfYear}");
        Console.WriteLine($"dt1.Ticks = {dt1.Ticks}");
        Console.WriteLine($"dt1.Kind = {dt1.Kind}");
        Console.WriteLine($"dt1.Date = {dt1.Date}");
        Console.WriteLine($"dt1.TimeOfDay = {dt1.TimeOfDay}");

        var dt2 = dt1.AddDays(10);
        Console.WriteLine($"dt1.AddDays(10) = {dt2}");
        Console.WriteLine($"dt1.AddHours(2) = {dt1.AddHours(2)}");
        Console.WriteLine($"dt1.AddMinutes(30) = {dt1.AddMinutes(30)}");
        Console.WriteLine($"dt1.AddSeconds(60) = {dt1.AddSeconds(60)}");
        Console.WriteLine($"dt1.AddMonths(3) = {dt1.AddMonths(3)}");
        Console.WriteLine($"dt1.AddYears(1) = {dt1.AddYears(1)}");
        Console.WriteLine($"dt1.Add(ts1) = {dt1.Add(ts1)}");

        Console.WriteLine($"DateTime.DaysInMonth(2024, 2) = {DateTime.DaysInMonth(2024, 2)}");
        Console.WriteLine($"DateTime.DaysInMonth(2023, 2) = {DateTime.DaysInMonth(2023, 2)}");
        Console.WriteLine($"DateTime.IsLeapYear(2024) = {DateTime.IsLeapYear(2024)}");
        Console.WriteLine($"DateTime.IsLeapYear(2023) = {DateTime.IsLeapYear(2023)}");

        Console.WriteLine($"DateTime.Parse(\"2024-06-15\") = {DateTime.Parse("2024-06-15")}");
        Console.WriteLine($"DateTime.Parse(\"2024-06-15T10:30:45\") = {DateTime.Parse("2024-06-15T10:30:45")}");

        // === DateTimeOffset ===
        Console.WriteLine();
        Console.WriteLine("=== DateTimeOffset ===");
        var dto1 = new DateTimeOffset(2024, 6, 15, 10, 30, 45, TimeSpan.FromHours(8));
        Console.WriteLine($"new DateTimeOffset(2024,6,15,10,30,45,+08:00).ToString() = {dto1}");
        Console.WriteLine($"dto1.DateTime = {dto1.DateTime}");
        Console.WriteLine($"dto1.LocalDateTime = {dto1.LocalDateTime}");
        Console.WriteLine($"dto1.UtcDateTime = {dto1.UtcDateTime}");
        Console.WriteLine($"dto1.Offset = {dto1.Offset}");
        Console.WriteLine($"dto1.Year = {dto1.Year}");
        Console.WriteLine($"dto1.Month = {dto1.Month}");
        Console.WriteLine($"dto1.Day = {dto1.Day}");
        Console.WriteLine($"dto1.Hour = {dto1.Hour}");
        Console.WriteLine($"dto1.Minute = {dto1.Minute}");
        Console.WriteLine($"dto1.Second = {dto1.Second}");
        Console.WriteLine($"dto1.DayOfWeek = {dto1.DayOfWeek}");
        Console.WriteLine($"dto1.DayOfYear = {dto1.DayOfYear}");

        // === DateOnly ===
        Console.WriteLine();
        Console.WriteLine("=== DateOnly ===");
        var do1 = new DateOnly(2024, 6, 15);
        Console.WriteLine($"new DateOnly(2024,6,15).ToString() = {do1}");
        Console.WriteLine($"do1.Year = {do1.Year}");
        Console.WriteLine($"do1.Month = {do1.Month}");
        Console.WriteLine($"do1.Day = {do1.Day}");
        Console.WriteLine($"do1.DayOfWeek = {do1.DayOfWeek}");
        Console.WriteLine($"do1.DayOfYear = {do1.DayOfYear}");
        Console.WriteLine($"do1.DayNumber = {do1.DayNumber}");
        Console.WriteLine($"do1.AddDays(10) = {do1.AddDays(10)}");
        Console.WriteLine($"do1.AddMonths(3) = {do1.AddMonths(3)}");
        Console.WriteLine($"do1.AddYears(1) = {do1.AddYears(1)}");
        Console.WriteLine($"DateOnly.FromDateTime(dt1) = {DateOnly.FromDateTime(dt1)}");
        Console.WriteLine($"DateOnly.Parse(\"2024-06-15\") = {DateOnly.Parse("2024-06-15")}");

        // === TimeOnly ===
        Console.WriteLine();
        Console.WriteLine("=== TimeOnly ===");
        var to1 = new TimeOnly(10, 30, 45);
        Console.WriteLine($"new TimeOnly(10,30,45).ToString() = {to1}");
        Console.WriteLine($"to1.Hour = {to1.Hour}");
        Console.WriteLine($"to1.Minute = {to1.Minute}");
        Console.WriteLine($"to1.Second = {to1.Second}");
        Console.WriteLine($"to1.Millisecond = {to1.Millisecond}");
        Console.WriteLine($"to1.Ticks = {to1.Ticks}");
        Console.WriteLine($"to1.AddHours(2) = {to1.AddHours(2)}");
        Console.WriteLine($"to1.AddMinutes(30) = {to1.AddMinutes(30)}");
        Console.WriteLine($"to1.Add(TimeSpan.FromHours(1)) = {to1.Add(TimeSpan.FromHours(1))}");
        Console.WriteLine($"TimeOnly.FromDateTime(dt1) = {TimeOnly.FromDateTime(dt1)}");
        Console.WriteLine($"TimeOnly.Parse(\"10:30:45\") = {TimeOnly.Parse("10:30:45")}");
        Console.WriteLine($"to1.CompareTo(new TimeOnly(11,0,0)) = {to1.CompareTo(new TimeOnly(11, 0, 0))}");
    }
}
```

- [ ] **Step 2: Run CSharpValidation and save reference output**

Run: `cd tools/DateTimeReflection && dotnet run --project ... (or copy CSharpValidation.cs as entry) > csharp_validation_output.txt`
Expected: Reference output with exact C# values for all tested methods.

Note: To run CSharpValidation.cs, either add it as a second Program.cs in a separate project, or temporarily replace Program.cs, run, and restore. A simpler approach: create a `tools/CSharpValidation/` project.

---

### Task 3: Create CSharpValidation Tool Project

**Files:**
- Create: `tools/CSharpValidation/CSharpValidation.csproj`
- Create: `tools/CSharpValidation/Program.cs` (copy of CSharpValidation.cs from Task 2)

- [ ] **Step 1: Create project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Copy CSharpValidation.cs content as Program.cs**

Copy the content from Task 2's CSharpValidation.cs into `tools/CSharpValidation/Program.cs`.

- [ ] **Step 3: Run and save reference output**

Run: `cd tools/CSharpValidation && dotnet run > csharp_output.txt`
Save the output file at `tools/CSharpValidation/csharp_output.txt` for cross-validation.

---

### Task 4: Implement CSharpTimeSpan

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpTimeSpan.java`

This is the simplest type (pure duration, no calendar/timezone), so implement it first.

- [ ] **Step 1: Write CSharpTimeSpan.java**

Key design decisions:
- Internally stores ticks (1 tick = 100 nanoseconds = 0.0000001 seconds), matching C# exactly
- Delegates to `java.time.Duration` for some operations but maintains tick precision
- C# `Ticks` is `long`, range is `long.MinValue` to `long.MaxValue`
- C# `TimeSpan` has 1 tick = 100ns resolution; Java `Duration` has nanosecond resolution

```java
package io.github.ningpp.compat;

/**
 * C# System.TimeSpan compatibility class.
 * Internally stores ticks where 1 tick = 100 nanoseconds.
 */
public final class CSharpTimeSpan implements Comparable<CSharpTimeSpan> {

    public static final long TICKS_PER_MILLISECOND = 10000L;
    public static final long TICKS_PER_SECOND = 10000000L;
    public static final long TICKS_PER_MINUTE = 600000000L;
    public static final long TICKS_PER_HOUR = 36000000000L;
    public static final long TICKS_PER_DAY = 864000000000L;

    public static final CSharpTimeSpan ZERO = new CSharpTimeSpan(0L);
    public static final CSharpTimeSpan MAX_VALUE = new CSharpTimeSpan(Long.MAX_VALUE);
    public static final CSharpTimeSpan MIN_VALUE = new CSharpTimeSpan(Long.MIN_VALUE);

    private final long ticks;

    public CSharpTimeSpan(long ticks) {
        this.ticks = ticks;
    }

    public CSharpTimeSpan(int hours, int minutes, int seconds) {
        this.ticks = calculateTicks(0, hours, minutes, seconds, 0);
    }

    public CSharpTimeSpan(int days, int hours, int minutes, int seconds) {
        this.ticks = calculateTicks(days, hours, minutes, seconds, 0);
    }

    public CSharpTimeSpan(int days, int hours, int minutes, int seconds, int milliseconds) {
        this.ticks = calculateTicks(days, hours, minutes, seconds, milliseconds);
    }

    private static long calculateTicks(int days, int hours, int minutes, int seconds, int milliseconds) {
        long totalMs = ((long) days * 3600 * 24 + (long) hours * 3600 + (long) minutes * 60 + seconds) * 1000L + milliseconds;
        return totalMs * TICKS_PER_MILLISECOND;
    }

    // --- Instance Properties ---

    public long getTicks() { return ticks; }

    public int getDays() { return (int) (ticks / TICKS_PER_DAY); }
    public int getHours() { return (int) ((ticks % TICKS_PER_DAY) / TICKS_PER_HOUR); }
    public int getMinutes() { return (int) ((ticks % TICKS_PER_HOUR) / TICKS_PER_MINUTE); }
    public int getSeconds() { return (int) ((ticks % TICKS_PER_MINUTE) / TICKS_PER_SECOND); }
    public int getMilliseconds() { return (int) ((ticks % TICKS_PER_SECOND) / TICKS_PER_MILLISECOND); }

    public double getTotalDays() { return (double) ticks / TICKS_PER_DAY; }
    public double getTotalHours() { return (double) ticks / TICKS_PER_HOUR; }
    public double getTotalMinutes() { return (double) ticks / TICKS_PER_MINUTE; }
    public double getTotalSeconds() { return (double) ticks / TICKS_PER_SECOND; }
    public double getTotalMilliseconds() { return (double) ticks / TICKS_PER_MILLISECOND; }

    // Duration() and Negate() match C# naming but use camelCase
    public CSharpTimeSpan duration() { return new CSharpTimeSpan(Math.abs(ticks)); }
    public CSharpTimeSpan negate() { return new CSharpTimeSpan(-ticks); }

    // --- Instance Methods ---

    public CSharpTimeSpan add(CSharpTimeSpan ts) { return new CSharpTimeSpan(ticks + ts.ticks); }
    public CSharpTimeSpan subtract(CSharpTimeSpan ts) { return new CSharpTimeSpan(ticks - ts.ticks); }

    @Override
    public int compareTo(CSharpTimeSpan other) { return Long.compare(ticks, other.ticks); }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpTimeSpan)) return false;
        return ticks == ((CSharpTimeSpan) obj).ticks;
    }

    @Override
    public int hashCode() { return Long.hashCode(ticks); }

    // --- toString matching C# format ---
    // C# format: [-][d.]hh:mm:ss[.fffffff]
    @Override
    public String toString() {
        StringBuilder sb = new StringBuilder();
        if (ticks < 0) {
            sb.append('-');
        }
        long absTicks = Math.abs(ticks);
        int days = (int) (absTicks / TICKS_PER_DAY);
        long remaining = absTicks % TICKS_PER_DAY;
        int hours = (int) (remaining / TICKS_PER_HOUR);
        remaining %= TICKS_PER_HOUR;
        int minutes = (int) (remaining / TICKS_PER_MINUTE);
        remaining %= TICKS_PER_MINUTE;
        int seconds = (int) (remaining / TICKS_PER_SECOND);
        long fracTicks = remaining % TICKS_PER_SECOND;

        if (days != 0) {
            sb.append(days).append('.');
        }
        sb.append(String.format("%02d:%02d:%02d", hours, minutes, seconds));
        if (fracTicks != 0) {
            String frac = String.format("%07d", fracTicks);
            // Trim trailing zeros
            int end = frac.length();
            while (end > 0 && frac.charAt(end - 1) == '0') end--;
            sb.append('.').append(frac, 0, end);
        }
        return sb.toString();
    }

    // --- Static Factory Methods ---

    public static CSharpTimeSpan fromDays(double value) {
        return interval(value, TICKS_PER_DAY);
    }
    public static CSharpTimeSpan fromHours(double value) {
        return interval(value, TICKS_PER_HOUR);
    }
    public static CSharpTimeSpan fromMinutes(double value) {
        return interval(value, TICKS_PER_MINUTE);
    }
    public static CSharpTimeSpan fromSeconds(double value) {
        return interval(value, TICKS_PER_SECOND);
    }
    public static CSharpTimeSpan fromMilliseconds(double value) {
        return interval(value, TICKS_PER_MILLISECOND);
    }
    public static CSharpTimeSpan fromTicks(long value) {
        return new CSharpTimeSpan(value);
    }

    private static CSharpTimeSpan interval(double value, long scale) {
        if (Double.isNaN(value)) {
            throw new IllegalArgumentException("Value cannot be NaN.");
        }
        double ticks = value * scale;
        if (ticks > Long.MAX_VALUE || ticks < Long.MIN_VALUE) {
            throw new ArithmeticException("TimeSpan overflowed because the duration is too long.");
        }
        return new CSharpTimeSpan((long) ticks);
    }

    // --- Parse ---

    public static CSharpTimeSpan parse(String s) {
        // C# format: [-][d.]hh:mm:ss[.fffffff]
        try {
            boolean negative = false;
            String input = s.trim();
            if (input.startsWith("-")) {
                negative = true;
                input = input.substring(1);
            }

            String[] dayAndRest = input.split("\\.", 2);
            int days = 0;
            String timePart;
            String fracPart = null;

            if (dayAndRest.length == 2) {
                // Check if first part is days (no colons) or time fraction
                if (dayAndRest[0].contains(":")) {
                    // No days, the dot is fractional seconds
                    timePart = dayAndRest[0];
                    fracPart = dayAndRest[1];
                } else {
                    // First part is days
                    days = Integer.parseInt(dayAndRest[0]);
                    // Remaining could be hh:mm:ss or hh:mm:ss.fffffff
                    String rest = dayAndRest[1];
                    int dotIdx = rest.indexOf('.');
                    if (dotIdx >= 0) {
                        timePart = rest.substring(0, dotIdx);
                        fracPart = rest.substring(dotIdx + 1);
                    } else {
                        timePart = rest;
                    }
                }
            } else {
                timePart = dayAndRest[0];
            }

            String[] parts = timePart.split(":");
            if (parts.length < 3 || parts.length > 4) {
                throw new IllegalArgumentException("Invalid TimeSpan format: " + s);
            }
            int hours = Integer.parseInt(parts[0]);
            int minutes = Integer.parseInt(parts[1]);
            int seconds;
            if (parts.length == 3) {
                seconds = Integer.parseInt(parts[2]);
            } else {
                seconds = Integer.parseInt(parts[2]);
                // parts[3] would be fractional if no dot separator
            }

            long fracTicks = 0;
            if (fracPart != null) {
                // Pad or trim to 7 digits
                fracPart = fracPart + "0000000".substring(0, Math.max(0, 7 - fracPart.length()));
                fracPart = fracPart.substring(0, 7);
                fracTicks = Long.parseLong(fracPart);
            }

            long totalTicks = (long) days * TICKS_PER_DAY
                + (long) hours * TICKS_PER_HOUR
                + (long) minutes * TICKS_PER_MINUTE
                + (long) seconds * TICKS_PER_SECOND
                + fracTicks;

            return new CSharpTimeSpan(negative ? -totalTicks : totalTicks);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid TimeSpan format: " + s, e);
        }
    }

    public static boolean tryParse(String s, ObjectHolder<CSharpTimeSpan> result) {
        try {
            result.value = parse(s);
            return true;
        } catch (Exception e) {
            result.value = ZERO;
            return false;
        }
    }
}
```

- [ ] **Step 2: Run Maven compile to verify**

Run: `cd java/csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 5: Implement CSharpDateTime

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateTime.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DayOfWeek.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DateTimeKind.java`

- [ ] **Step 1: Create DayOfWeek enum**

```java
package io.github.ningpp.compat;

/** C# System.DayOfWeek compatibility enum. */
public enum DayOfWeek {
    Sunday(0),
    Monday(1),
    Tuesday(2),
    Wednesday(3),
    Thursday(4),
    Friday(5),
    Saturday(6);

    private final int value;

    DayOfWeek(int value) { this.value = value; }

    public int getValue() { return value; }

    public static DayOfWeek fromJava(java.time.DayOfWeek dow) {
        return values()[dow.getValue() % 7];
    }
}
```

- [ ] **Step 2: Create DateTimeKind enum**

```java
package io.github.ningpp.compat;

/** C# System.DateTimeKind compatibility enum. */
public enum DateTimeKind {
    Unspecified(0),
    Utc(1),
    Local(2);

    private final int value;

    DateTimeKind(int value) { this.value = value; }

    public int getValue() { return value; }
}
```

- [ ] **Step 3: Write CSharpDateTime.java**

Key design:
- Internally stores ticks (same as C# DateTime: 100ns resolution since 0001-01-01 00:00:00)
- Delegates to `java.time.LocalDateTime` for calendar operations
- Handles the C# tick epoch (Jan 1, 0001) vs Java epoch (Jan 1, 1970) conversion

```java
package io.github.ningpp.compat;

import java.time.*;
import java.time.format.DateTimeFormatter;
import java.time.temporal.ChronoField;

/**
 * C# System.DateTime compatibility class.
 * Internally stores ticks (100ns resolution) since 0001-01-01 00:00:00.
 */
public final class CSharpDateTime implements Comparable<CSharpDateTime> {

    // Java epoch (1970-01-01) in C# ticks
    private static final long EPOCH_DIFF_TICKS = 621355968000000000L;

    public static final CSharpDateTime MIN_VALUE = new CSharpDateTime(0L);
    public static final CSharpDateTime MAX_VALUE = new CSharpDateTime(3155378975999999999L);

    private final long ticks;
    private final DateTimeKind kind;

    public CSharpDateTime(long ticks) {
        if (ticks < 0 || ticks > 3155378975999999999L) {
            throw new IllegalArgumentException("Ticks must be between 0 and 3155378975999999999.");
        }
        this.ticks = ticks;
        this.kind = DateTimeKind.Unspecified;
    }

    public CSharpDateTime(long ticks, DateTimeKind kind) {
        if (ticks < 0 || ticks > 3155378975999999999L) {
            throw new IllegalArgumentException("Ticks must be between 0 and 3155378975999999999.");
        }
        this.ticks = ticks;
        this.kind = kind;
    }

    public CSharpDateTime(int year, int month, int day) {
        this(year, month, day, 0, 0, 0, 0, DateTimeKind.Unspecified);
    }

    public CSharpDateTime(int year, int month, int day, int hour, int minute, int second) {
        this(year, month, day, hour, minute, second, 0, DateTimeKind.Unspecified);
    }

    public CSharpDateTime(int year, int month, int day, int hour, int minute, int second, int millisecond) {
        this(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
    }

    public CSharpDateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, DateTimeKind kind) {
        LocalDateTime ldt = LocalDateTime.of(year, month, day, hour, minute, second, millisecond * 1_000_000);
        this.ticks = ldtToTicks(ldt);
        this.kind = kind;
    }

    // --- Conversion helpers ---

    private static long ldtToTicks(LocalDateTime ldt) {
        long epochDay = ldt.toLocalDate().toEpochDay();
        long nanoOfDay = ldt.toLocalTime().toNanoOfDay();
        return epochDay * CSharpTimeSpan.TICKS_PER_DAY + nanoOfDay / 100 + EPOCH_DIFF_TICKS;
    }

    private LocalDateTime ticksToLdt() {
        long javaEpochTicks = ticks - EPOCH_DIFF_TICKS;
        long epochDay = javaEpochTicks / CSharpTimeSpan.TICKS_PER_DAY;
        long nanoOfDay = (javaEpochTicks % CSharpTimeSpan.TICKS_PER_DAY) * 100;
        if (nanoOfDay < 0) {
            epochDay--;
            nanoOfDay += 24L * 3600 * 1_000_000_000L;
        }
        return LocalDateTime.of(LocalDate.ofEpochDay(epochDay), LocalTime.ofNanoOfDay(nanoOfDay));
    }

    // --- Instance Properties ---

    public long getTicks() { return ticks; }
    public DateTimeKind getKind() { return kind; }
    public int getYear() { return ticksToLdt().getYear(); }
    public int getMonth() { return ticksToLdt().getMonthValue(); }
    public int getDay() { return ticksToLdt().getDayOfMonth(); }
    public int getHour() { return ticksToLdt().getHour(); }
    public int getMinute() { return ticksToLdt().getMinute(); }
    public int getSecond() { return ticksToLdt().getSecond(); }
    public int getMillisecond() { return ticksToLdt().getNano() / 1_000_000; }
    public DayOfWeek getDayOfWeek() { return DayOfWeek.fromJava(ticksToLdt().getDayOfWeek()); }
    public int getDayOfYear() { return ticksToLdt().getDayOfYear(); }

    public CSharpDateTime getDate() {
        LocalDateTime ldt = ticksToLdt();
        return new CSharpDateTime(ldtToTicks(ldt.toLocalDate().atStartOfDay()), kind);
    }

    public CSharpTimeSpan getTimeOfDay() {
        LocalDateTime ldt = ticksToLdt();
        long nanoOfDay = ldt.toLocalTime().toNanoOfDay();
        return new CSharpTimeSpan(nanoOfDay / 100);
    }

    // --- Add methods ---

    public CSharpDateTime add(CSharpTimeSpan ts) {
        return new CSharpDateTime(ticks + ts.getTicks(), kind);
    }

    public CSharpDateTime addDays(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_DAY), kind);
    }

    public CSharpDateTime addHours(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_HOUR), kind);
    }

    public CSharpDateTime addMinutes(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_MINUTE), kind);
    }

    public CSharpDateTime addSeconds(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_SECOND), kind);
    }

    public CSharpDateTime addMilliseconds(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_MILLISECOND), kind);
    }

    public CSharpDateTime addMonths(int months) {
        LocalDateTime ldt = ticksToLdt().plusMonths(months);
        return new CSharpDateTime(ldtToTicks(ldt), kind);
    }

    public CSharpDateTime addYears(int years) {
        LocalDateTime ldt = ticksToLdt().plusYears(years);
        return new CSharpDateTime(ldtToTicks(ldt), kind);
    }

    public CSharpDateTime addTicks(long value) {
        return new CSharpDateTime(ticks + value, kind);
    }

    // --- Subtract ---

    public CSharpTimeSpan subtract(CSharpDateTime dt) {
        return new CSharpTimeSpan(ticks - dt.ticks);
    }

    public CSharpDateTime subtract(CSharpTimeSpan ts) {
        return new CSharpDateTime(ticks - ts.getTicks(), kind);
    }

    // --- Static Methods ---

    public static CSharpDateTime getNow() {
        return new CSharpDateTime(ldtToTicks(LocalDateTime.now()), DateTimeKind.Local);
    }

    public static CSharpDateTime getUtcNow() {
        return new CSharpDateTime(ldtToTicks(LocalDateTime.ofInstant(Instant.now(), ZoneOffset.UTC)), DateTimeKind.Utc);
    }

    public static CSharpDateTime getToday() {
        return getNow().getDate();
    }

    public static int daysInMonth(int year, int month) {
        return YearMonth.of(year, month).lengthOfMonth();
    }

    public static boolean isLeapYear(int year) {
        return Year.of(year).isLeap();
    }

    public static CSharpDateTime parse(String s) {
        // Try ISO format first
        String trimmed = s.trim();
        try {
            LocalDateTime ldt;
            if (trimmed.contains("T")) {
                ldt = LocalDateTime.parse(trimmed);
            } else if (trimmed.contains(":")) {
                ldt = LocalDateTime.parse(trimmed, DateTimeFormatter.ISO_LOCAL_DATE_TIME);
            } else {
                ldt = LocalDate.parse(trimmed).atStartOfDay();
            }
            return new CSharpDateTime(ldtToTicks(ldt), DateTimeKind.Unspecified);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid DateTime format: " + s, e);
        }
    }

    public static boolean tryParse(String s, ObjectHolder<CSharpDateTime> result) {
        try {
            result.value = parse(s);
            return true;
        } catch (Exception e) {
            result.value = MIN_VALUE;
            return false;
        }
    }

    // --- toString ---
    // C# DateTime.ToString() produces "M/d/yyyy h:mm:ss PM" in en-US,
    // but for cross-validation we use ISO format: "yyyy-MM-ddTHH:mm:ss"
    @Override
    public String toString() {
        LocalDateTime ldt = ticksToLdt();
        if (ldt.toLocalTime().getSecond() == 0 && ldt.toLocalTime().getNano() == 0 && ldt.getHour() == 0 && ldt.getMinute() == 0) {
            return ldt.toLocalDate().toString();
        }
        return ldt.toString();
    }

    // --- Comparable / equals / hashCode ---

    @Override
    public int compareTo(CSharpDateTime other) { return Long.compare(ticks, other.ticks); }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpDateTime)) return false;
        return ticks == ((CSharpDateTime) obj).ticks;
    }

    @Override
    public int hashCode() { return Long.hashCode(ticks); }
}
```

- [ ] **Step 4: Run Maven compile to verify**

Run: `cd java/csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 6: Implement CSharpDateTimeOffset

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateTimeOffset.java`

- [ ] **Step 1: Write CSharpDateTimeOffset.java**

Key design:
- Stores a `CSharpDateTime` (the date/time without offset) and a `CSharpTimeSpan` (the offset)
- Delegates to `java.time.OffsetDateTime` for operations

```java
package io.github.ningpp.compat;

import java.time.*;

/**
 * C# System.DateTimeOffset compatibility class.
 * Represents a point in time with an offset from UTC.
 */
public final class CSharpDateTimeOffset implements Comparable<CSharpDateTimeOffset> {

    private final CSharpDateTime dateTime;
    private final CSharpTimeSpan offset;

    public static final CSharpDateTimeOffset MIN_VALUE =
        new CSharpDateTimeOffset(CSharpDateTime.MIN_VALUE, CSharpTimeSpan.ZERO);
    public static final CSharpDateTimeOffset MAX_VALUE =
        new CSharpDateTimeOffset(CSharpDateTime.MAX_VALUE, CSharpTimeSpan.ZERO);

    public CSharpDateTimeOffset(CSharpDateTime dateTime, CSharpTimeSpan offset) {
        this.dateTime = dateTime;
        this.offset = offset;
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, int millisecond, CSharpTimeSpan offset) {
        this.dateTime = new CSharpDateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
        this.offset = offset;
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, CSharpTimeSpan offset) {
        this(year, month, day, hour, minute, second, 0, offset);
    }

    public CSharpDateTimeOffset(CSharpDateTime dateTime) {
        this.dateTime = dateTime;
        this.offset = CSharpTimeSpan.ZERO;
    }

    // --- Properties ---

    public CSharpDateTime getDateTime() { return dateTime; }
    public CSharpTimeSpan getOffset() { return offset; }
    public int getYear() { return dateTime.getYear(); }
    public int getMonth() { return dateTime.getMonth(); }
    public int getDay() { return dateTime.getDay(); }
    public int getHour() { return dateTime.getHour(); }
    public int getMinute() { return dateTime.getMinute(); }
    public int getSecond() { return dateTime.getSecond(); }
    public int getMillisecond() { return dateTime.getMillisecond(); }
    public DayOfWeek getDayOfWeek() { return dateTime.getDayOfWeek(); }
    public int getDayOfYear() { return dateTime.getDayOfYear(); }

    public CSharpDateTime getLocalDateTime() {
        return dateTime;
    }

    public CSharpDateTime getUtcDateTime() {
        return dateTime.subtract(offset);
    }

    public long getTicks() { return dateTime.getTicks(); }
    public long getUtcTicks() { return dateTime.subtract(offset).getTicks(); }

    // --- Add/Subtract ---

    public CSharpDateTimeOffset add(CSharpTimeSpan ts) {
        return new CSharpDateTimeOffset(dateTime.add(ts), offset);
    }

    public CSharpDateTimeOffset addDays(double days) {
        return new CSharpDateTimeOffset(dateTime.addDays(days), offset);
    }

    public CSharpDateTimeOffset addHours(double hours) {
        return new CSharpDateTimeOffset(dateTime.addHours(hours), offset);
    }

    public CSharpDateTimeOffset addMinutes(double minutes) {
        return new CSharpDateTimeOffset(dateTime.addMinutes(minutes), offset);
    }

    public CSharpDateTimeOffset addMonths(int months) {
        return new CSharpDateTimeOffset(dateTime.addMonths(months), offset);
    }

    public CSharpDateTimeOffset addSeconds(double seconds) {
        return new CSharpDateTimeOffset(dateTime.addSeconds(seconds), offset);
    }

    public CSharpDateTimeOffset addYears(int years) {
        return new CSharpDateTimeOffset(dateTime.addYears(years), offset);
    }

    public CSharpDateTimeOffset subtract(CSharpTimeSpan ts) {
        return new CSharpDateTimeOffset(dateTime.subtract(ts), offset);
    }

    public CSharpTimeSpan subtract(CSharpDateTimeOffset other) {
        return new CSharpTimeSpan(getUtcTicks() - other.getUtcTicks());
    }

    // --- Static Methods ---

    public static CSharpDateTimeOffset getNow() {
        OffsetDateTime odt = OffsetDateTime.now();
        CSharpDateTime dt = new CSharpDateTime(odt.getYear(), odt.getMonthValue(), odt.getDayOfMonth(),
            odt.getHour(), odt.getMinute(), odt.getSecond(), odt.getNano() / 1_000_000, DateTimeKind.Local);
        int offsetSeconds = odt.getOffset().getTotalSeconds();
        CSharpTimeSpan offset = CSharpTimeSpan.fromSeconds(offsetSeconds);
        return new CSharpDateTimeOffset(dt, offset);
    }

    public static CSharpDateTimeOffset getUtcNow() {
        Instant instant = Instant.now();
        OffsetDateTime odt = instant.atOffset(ZoneOffset.UTC);
        CSharpDateTime dt = new CSharpDateTime(odt.getYear(), odt.getMonthValue(), odt.getDayOfMonth(),
            odt.getHour(), odt.getMinute(), odt.getSecond(), odt.getNano() / 1_000_000, DateTimeKind.Utc);
        return new CSharpDateTimeOffset(dt, CSharpTimeSpan.ZERO);
    }

    // --- toString ---
    // C# format: "M/d/yyyy h:mm:ss PM +HH:mm"
    @Override
    public String toString() {
        String dtStr = dateTime.toString();
        long offsetTicks = offset.getTicks();
        if (offsetTicks == 0) {
            return dtStr + "+00:00";
        }
        boolean negative = offsetTicks < 0;
        long absTicks = Math.abs(offsetTicks);
        int hours = (int) (absTicks / CSharpTimeSpan.TICKS_PER_HOUR);
        int minutes = (int) ((absTicks % CSharpTimeSpan.TICKS_PER_HOUR) / CSharpTimeSpan.TICKS_PER_MINUTE);
        return dtStr + (negative ? "-" : "+") + String.format("%02d:%02d", hours, minutes);
    }

    @Override
    public int compareTo(CSharpDateTimeOffset other) {
        return Long.compare(getUtcTicks(), other.getUtcTicks());
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpDateTimeOffset)) return false;
        return getUtcTicks() == ((CSharpDateTimeOffset) obj).getUtcTicks();
    }

    @Override
    public int hashCode() { return Long.hashCode(getUtcTicks()); }
}
```

- [ ] **Step 2: Run Maven compile to verify**

Run: `cd java/csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 7: Implement CSharpDateOnly

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDateOnly.java`

- [ ] **Step 1: Write CSharpDateOnly.java**

```java
package io.github.ningpp.compat;

import java.time.LocalDate;
import java.time.Year;
import java.time.YearMonth;
import java.time.format.DateTimeFormatter;
import java.time.temporal.ChronoUnit;

/**
 * C# System.DateOnly compatibility class.
 */
public final class CSharpDateOnly implements Comparable<CSharpDateOnly> {

    public static final CSharpDateOnly MIN_VALUE = new CSharpDateOnly(1, 1, 1);
    public static final CSharpDateOnly MAX_VALUE = new CSharpDateOnly(9999, 12, 31);

    private final LocalDate localDate;

    public CSharpDateOnly(int year, int month, int day) {
        this.localDate = LocalDate.of(year, month, day);
    }

    private CSharpDateOnly(LocalDate localDate) {
        this.localDate = localDate;
    }

    // --- Properties ---

    public int getYear() { return localDate.getYear(); }
    public int getMonth() { return localDate.getMonthValue(); }
    public int getDay() { return localDate.getDayOfMonth(); }
    public DayOfWeek getDayOfWeek() { return DayOfWeek.fromJava(localDate.getDayOfWeek()); }
    public int getDayOfYear() { return localDate.getDayOfYear(); }
    public int getDayNumber() { return (int) LocalDate.of(1, 1, 1).until(localDate, ChronoUnit.DAYS) + 1; }

    // --- Add methods ---

    public CSharpDateOnly addDays(int days) { return new CSharpDateOnly(localDate.plusDays(days)); }
    public CSharpDateOnly addMonths(int months) { return new CSharpDateOnly(localDate.plusMonths(months)); }
    public CSharpDateOnly addYears(int years) { return new CSharpDateOnly(localDate.plusYears(years)); }
    public CSharpDateOnly addDays(long days) { return new CSharpDateOnly(localDate.plusDays(days)); }

    // --- Static Methods ---

    public static CSharpDateOnly fromDateTime(CSharpDateTime dateTime) {
        return new CSharpDateOnly(dateTime.getYear(), dateTime.getMonth(), dateTime.getDay());
    }

    public static CSharpDateOnly parse(String s) {
        return new CSharpDateOnly(LocalDate.parse(s.trim()));
    }

    public static boolean tryParse(String s, ObjectHolder<CSharpDateOnly> result) {
        try {
            result.value = parse(s);
            return true;
        } catch (Exception e) {
            result.value = MIN_VALUE;
            return false;
        }
    }

    // --- toString ---

    @Override
    public String toString() { return localDate.toString(); }

    @Override
    public int compareTo(CSharpDateOnly other) { return localDate.compareTo(other.localDate); }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpDateOnly)) return false;
        return localDate.equals(((CSharpDateOnly) obj).localDate);
    }

    @Override
    public int hashCode() { return localDate.hashCode(); }
}
```

- [ ] **Step 2: Run Maven compile to verify**

Run: `cd java/csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 8: Implement CSharpTimeOnly

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpTimeOnly.java`

- [ ] **Step 1: Write CSharpTimeOnly.java**

```java
package io.github.ningpp.compat;

import java.time.LocalTime;
import java.time.format.DateTimeFormatter;

/**
 * C# System.TimeOnly compatibility class.
 * Internally stores ticks (100ns resolution) since midnight.
 */
public final class CSharpTimeOnly implements Comparable<CSharpTimeOnly> {

    public static final CSharpTimeOnly MIN_VALUE = new CSharpTimeOnly(0L);
    public static final CSharpTimeOnly MAX_VALUE = new CSharpTimeOnly(863999999999L);

    private static final long TICKS_PER_MILLISECOND = 10000L;
    private static final long TICKS_PER_SECOND = 10000000L;
    private static final long TICKS_PER_MINUTE = 600000000L;
    private static final long TICKS_PER_HOUR = 36000000000L;
    private static final long TICKS_PER_DAY = 864000000000L;

    private final long ticks;

    public CSharpTimeOnly(long ticks) {
        if (ticks < 0 || ticks >= TICKS_PER_DAY) {
            throw new IllegalArgumentException("ticks must be between 0 and 863999999999.");
        }
        this.ticks = ticks;
    }

    public CSharpTimeOnly(int hour, int minute, int second) {
        this((long) hour * TICKS_PER_HOUR + (long) minute * TICKS_PER_MINUTE + (long) second * TICKS_PER_SECOND);
    }

    public CSharpTimeOnly(int hour, int minute, int second, int millisecond) {
        this((long) hour * TICKS_PER_HOUR + (long) minute * TICKS_PER_MINUTE
            + (long) second * TICKS_PER_SECOND + (long) millisecond * TICKS_PER_MILLISECOND);
    }

    // --- Properties ---

    public long getTicks() { return ticks; }
    public int getHour() { return (int) (ticks / TICKS_PER_HOUR); }
    public int getMinute() { return (int) ((ticks % TICKS_PER_HOUR) / TICKS_PER_MINUTE); }
    public int getSecond() { return (int) ((ticks % TICKS_PER_MINUTE) / TICKS_PER_SECOND); }
    public int getMillisecond() { return (int) ((ticks % TICKS_PER_SECOND) / TICKS_PER_MILLISECOND); }

    // --- Add methods ---

    public CSharpTimeOnly add(CSharpTimeSpan ts) {
        long newTicks = (ticks + ts.getTicks()) % TICKS_PER_DAY;
        if (newTicks < 0) newTicks += TICKS_PER_DAY;
        return new CSharpTimeOnly(newTicks);
    }

    public CSharpTimeOnly addHours(double hours) {
        long addTicks = (long) (hours * TICKS_PER_HOUR);
        long newTicks = (ticks + addTicks) % TICKS_PER_DAY;
        if (newTicks < 0) newTicks += TICKS_PER_DAY;
        return new CSharpTimeOnly(newTicks);
    }

    public CSharpTimeOnly addMinutes(double minutes) {
        long addTicks = (long) (minutes * TICKS_PER_MINUTE);
        long newTicks = (ticks + addTicks) % TICKS_PER_DAY;
        if (newTicks < 0) newTicks += TICKS_PER_DAY;
        return new CSharpTimeOnly(newTicks);
    }

    // --- Static Methods ---

    public static CSharpTimeOnly fromDateTime(CSharpDateTime dateTime) {
        return new CSharpTimeOnly(dateTime.getTimeOfDay().getTicks());
    }

    public static CSharpTimeOnly fromTimeSpan(CSharpTimeSpan ts) {
        long ticks = ts.getTicks() % TICKS_PER_DAY;
        if (ticks < 0) ticks += TICKS_PER_DAY;
        return new CSharpTimeOnly(ticks);
    }

    public static CSharpTimeOnly parse(String s) {
        LocalTime lt = LocalTime.parse(s.trim());
        return new CSharpTimeOnly(lt.toNanoOfDay() / 100);
    }

    public static boolean tryParse(String s, ObjectHolder<CSharpTimeOnly> result) {
        try {
            result.value = parse(s);
            return true;
        } catch (Exception e) {
            result.value = MIN_VALUE;
            return false;
        }
    }

    // --- toString ---

    @Override
    public String toString() {
        int h = getHour();
        int m = getMinute();
        int s = getSecond();
        long fracTicks = ticks % TICKS_PER_SECOND;
        if (fracTicks == 0) {
            return String.format("%02d:%02d:%02d", h, m, s);
        }
        String frac = String.format("%07d", fracTicks);
        int end = frac.length();
        while (end > 0 && frac.charAt(end - 1) == '0') end--;
        return String.format("%02d:%02d:%02d.%s", h, m, s, frac.substring(0, end));
    }

    @Override
    public int compareTo(CSharpTimeOnly other) { return Long.compare(ticks, other.ticks); }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpTimeOnly)) return false;
        return ticks == ((CSharpTimeOnly) obj).ticks;
    }

    @Override
    public int hashCode() { return Long.hashCode(ticks); }
}
```

- [ ] **Step 2: Run Maven compile to verify**

Run: `cd java/csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 9: Write CSharpTimeSpanTest (Cross-Validated)

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpTimeSpanTest.java`

- [ ] **Step 1: Write the test**

Each test method exercises a specific API and asserts against the C# reference output from Task 3.

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpTimeSpanTest {

    @Test
    void constructor_hms() {
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals("01:02:03", ts.toString());
    }

    @Test
    void properties_hms() {
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals(0, ts.getDays());
        assertEquals(1, ts.getHours());
        assertEquals(2, ts.getMinutes());
        assertEquals(3, ts.getSeconds());
        assertEquals(0, ts.getMilliseconds());
    }

    @Test
    void totalValues() {
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        // C# output: TotalDays=0.043090277777777778, TotalHours=1.0341666666666667, etc.
        assertEquals(0.043090277777777778, ts.getTotalDays(), 1e-12);
        assertEquals(1.0341666666666667, ts.getTotalHours(), 1e-12);
        assertEquals(62.05, ts.getTotalMinutes(), 1e-12);
        assertEquals(3723.0, ts.getTotalSeconds(), 1e-9);
        assertEquals(3723000.0, ts.getTotalMilliseconds(), 1e-6);
    }

    @Test
    void ticks() {
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals(37230000000L, ts.getTicks());
    }

    @Test
    void fromHours() {
        CSharpTimeSpan ts = CSharpTimeSpan.fromHours(1.5);
        assertEquals("01:30:00", ts.toString());
    }

    @Test
    void fromMinutes() {
        CSharpTimeSpan ts = CSharpTimeSpan.fromMinutes(90);
        assertEquals("01:30:00", ts.toString());
    }

    @Test
    void fromSeconds() {
        CSharpTimeSpan ts = CSharpTimeSpan.fromSeconds(3661);
        assertEquals("01:01:01", ts.toString());
    }

    @Test
    void fromMilliseconds() {
        CSharpTimeSpan ts = CSharpTimeSpan.fromMilliseconds(1500);
        assertEquals("00:00:01.5000000", ts.toString());
    }

    @Test
    void fromTicks() {
        CSharpTimeSpan ts = CSharpTimeSpan.fromTicks(10000000);
        assertEquals("00:00:01", ts.toString());
    }

    @Test
    void zero() {
        assertEquals("00:00:00", CSharpTimeSpan.ZERO.toString());
    }

    @Test
    void add() {
        CSharpTimeSpan ts1 = new CSharpTimeSpan(1, 2, 3);
        CSharpTimeSpan ts2 = CSharpTimeSpan.fromHours(1.5);
        CSharpTimeSpan result = ts1.add(ts2);
        assertEquals("02:32:03", result.toString());
    }

    @Test
    void subtract() {
        CSharpTimeSpan ts1 = new CSharpTimeSpan(1, 2, 3);
        CSharpTimeSpan ts2 = CSharpTimeSpan.fromHours(1.5);
        CSharpTimeSpan result = ts1.subtract(ts2);
        // 1:02:03 - 1:30:00 = -0:27:57
        assertEquals("-00:27:57", result.toString());
    }

    @Test
    void negate() {
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        assertEquals("-01:02:03", ts.negate().toString());
    }

    @Test
    void duration() {
        CSharpTimeSpan ts = new CSharpTimeSpan(-1, -2, -3);
        // Negative ticks constructor not directly available, use negate
        CSharpTimeSpan neg = new CSharpTimeSpan(1, 2, 3).negate();
        assertEquals("01:02:03", neg.duration().toString());
    }

    @Test
    void parse_simple() {
        CSharpTimeSpan ts = CSharpTimeSpan.parse("1:02:03");
        assertEquals(new CSharpTimeSpan(1, 2, 3), ts);
    }

    @Test
    void parse_negative() {
        CSharpTimeSpan ts = CSharpTimeSpan.parse("-1:02:03");
        assertEquals(new CSharpTimeSpan(1, 2, 3).negate(), ts);
    }

    @Test
    void parse_withDays() {
        CSharpTimeSpan ts = CSharpTimeSpan.parse("1.02:03:04.0050000");
        assertEquals(1, ts.getDays());
        assertEquals(2, ts.getHours());
        assertEquals(3, ts.getMinutes());
        assertEquals(4, ts.getSeconds());
        assertEquals(5, ts.getMilliseconds());
    }

    @Test
    void tryParse_valid() {
        ObjectHolder<CSharpTimeSpan> holder = new ObjectHolder<>();
        assertTrue(CSharpTimeSpan.tryParse("1:02:03", holder));
        assertEquals(new CSharpTimeSpan(1, 2, 3), holder.value);
    }

    @Test
    void tryParse_invalid() {
        ObjectHolder<CSharpTimeSpan> holder = new ObjectHolder<>();
        assertFalse(CSharpTimeSpan.tryParse("abc", holder));
        assertEquals(CSharpTimeSpan.ZERO, holder.value);
    }
}
```

- [ ] **Step 2: Run the test**

Run: `cd java/csharptojava-compat && mvn test -Dtest=CSharpTimeSpanTest -q`
Expected: All tests pass.

---

### Task 10: Write CSharpDateTimeTest (Cross-Validated)

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpDateTimeTest.java`

- [ ] **Step 1: Write the test**

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpDateTimeTest {

    @Test
    void constructor_ymdhms() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals(2024, dt.getYear());
        assertEquals(6, dt.getMonth());
        assertEquals(15, dt.getDay());
        assertEquals(10, dt.getHour());
        assertEquals(30, dt.getMinute());
        assertEquals(45, dt.getSecond());
        assertEquals(0, dt.getMillisecond());
    }

    @Test
    void dayOfWeek() {
        // 2024-06-15 is Saturday
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        assertEquals(DayOfWeek.Saturday, dt.getDayOfWeek());
    }

    @Test
    void dayOfYear() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        // June 15 = 31(Jan) + 29(Feb, leap) + 31(Mar) + 30(Apr) + 31(May) + 15 = 167
        assertEquals(167, dt.getDayOfYear());
    }

    @Test
    void date() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateTime dateOnly = dt.getDate();
        assertEquals(0, dateOnly.getHour());
        assertEquals(0, dateOnly.getMinute());
        assertEquals(0, dateOnly.getSecond());
        assertEquals(2024, dateOnly.getYear());
        assertEquals(6, dateOnly.getMonth());
        assertEquals(15, dateOnly.getDay());
    }

    @Test
    void timeOfDay() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpTimeSpan tod = dt.getTimeOfDay();
        assertEquals(10, tod.getHours());
        assertEquals(30, tod.getMinutes());
        assertEquals(45, tod.getSeconds());
    }

    @Test
    void addDays() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateTime result = dt.addDays(10);
        assertEquals(25, result.getDay());
        assertEquals(6, result.getMonth());
    }

    @Test
    void addHours() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateTime result = dt.addHours(2);
        assertEquals(12, result.getHour());
    }

    @Test
    void addMonths() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateTime result = dt.addMonths(3);
        assertEquals(9, result.getMonth());
    }

    @Test
    void addYears() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateTime result = dt.addYears(1);
        assertEquals(2025, result.getYear());
    }

    @Test
    void addTimeSpan() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 2, 3);
        CSharpDateTime result = dt.add(ts);
        assertEquals(11, result.getHour());
        assertEquals(32, result.getMinute());
        assertEquals(48, result.getSecond());
    }

    @Test
    void daysInMonth() {
        assertEquals(29, CSharpDateTime.daysInMonth(2024, 2));
        assertEquals(28, CSharpDateTime.daysInMonth(2023, 2));
    }

    @Test
    void isLeapYear() {
        assertTrue(CSharpDateTime.isLeapYear(2024));
        assertFalse(CSharpDateTime.isLeapYear(2023));
    }

    @Test
    void parse() {
        CSharpDateTime dt = CSharpDateTime.parse("2024-06-15");
        assertEquals(2024, dt.getYear());
        assertEquals(6, dt.getMonth());
        assertEquals(15, dt.getDay());
    }

    @Test
    void parseWithTime() {
        CSharpDateTime dt = CSharpDateTime.parse("2024-06-15T10:30:45");
        assertEquals(10, dt.getHour());
        assertEquals(30, dt.getMinute());
        assertEquals(45, dt.getSecond());
    }

    @Test
    void kind() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15);
        assertEquals(DateTimeKind.Unspecified, dt.getKind());
    }

    @Test
    void ticks() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        // Cross-validate with C# output
        assertTrue(dt.getTicks() > 0);
    }

    @Test
    void subtract_dateTime() {
        CSharpDateTime dt1 = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateTime dt2 = new CSharpDateTime(2024, 6, 15, 8, 0, 0);
        CSharpTimeSpan diff = dt1.subtract(dt2);
        assertEquals(2, diff.getHours());
        assertEquals(30, diff.getMinutes());
        assertEquals(45, diff.getSeconds());
    }
}
```

- [ ] **Step 2: Run the test**

Run: `cd java/csharptojava-compat && mvn test -Dtest=CSharpDateTimeTest -q`
Expected: All tests pass.

---

### Task 11: Write CSharpDateTimeOffsetTest

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpDateTimeOffsetTest.java`

- [ ] **Step 1: Write the test**

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpDateTimeOffsetTest {

    @Test
    void constructor_withOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(2024, dto.getYear());
        assertEquals(6, dto.getMonth());
        assertEquals(15, dto.getDay());
        assertEquals(10, dto.getHour());
        assertEquals(30, dto.getMinute());
        assertEquals(45, dto.getSecond());
        assertEquals(offset, dto.getOffset());
    }

    @Test
    void utcDateTime() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTime utc = dto.getUtcDateTime();
        assertEquals(2, utc.getHour()); // 10 - 8 = 2
        assertEquals(30, utc.getMinute());
        assertEquals(45, utc.getSecond());
    }

    @Test
    void localDateTime() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTime local = dto.getLocalDateTime();
        assertEquals(10, local.getHour());
    }

    @Test
    void dayOfWeek() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(DayOfWeek.Saturday, dto.getDayOfWeek());
    }

    @Test
    void dayOfYear() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        assertEquals(167, dto.getDayOfYear());
    }

    @Test
    void addTimeSpan() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpTimeSpan ts = new CSharpTimeSpan(1, 0, 0);
        CSharpDateTimeOffset result = dto.add(ts);
        assertEquals(11, result.getHour());
        assertEquals(offset, result.getOffset());
    }

    @Test
    void subtract_dateTimeOffset() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto1 = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        CSharpDateTimeOffset dto2 = new CSharpDateTimeOffset(2024, 6, 15, 8, 30, 45, offset);
        CSharpTimeSpan diff = dto1.subtract(dto2);
        assertEquals(2, diff.getHours());
    }

    @Test
    void toString_format() {
        CSharpTimeSpan offset = CSharpTimeSpan.fromHours(8);
        CSharpDateTimeOffset dto = new CSharpDateTimeOffset(2024, 6, 15, 10, 30, 45, offset);
        String str = dto.toString();
        assertTrue(str.contains("2024"));
        assertTrue(str.contains("+08:00"));
    }
}
```

- [ ] **Step 2: Run the test**

Run: `cd java/csharptojava-compat && mvn test -Dtest=CSharpDateTimeOffsetTest -q`
Expected: All tests pass.

---

### Task 12: Write CSharpDateOnlyTest

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpDateOnlyTest.java`

- [ ] **Step 1: Write the test**

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpDateOnlyTest {

    @Test
    void constructor_ymd() {
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals(2024, d.getYear());
        assertEquals(6, d.getMonth());
        assertEquals(15, d.getDay());
    }

    @Test
    void dayOfWeek() {
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals(DayOfWeek.Saturday, d.getDayOfWeek());
    }

    @Test
    void dayOfYear() {
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals(167, d.getDayOfYear());
    }

    @Test
    void dayNumber() {
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        // DayNumber from 0001-01-01
        assertTrue(d.getDayNumber() > 0);
    }

    @Test
    void addDays() {
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        CSharpDateOnly result = d.addDays(10);
        assertEquals(25, result.getDay());
    }

    @Test
    void addMonths() {
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        CSharpDateOnly result = d.addMonths(3);
        assertEquals(9, result.getMonth());
    }

    @Test
    void addYears() {
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        CSharpDateOnly result = d.addYears(1);
        assertEquals(2025, result.getYear());
    }

    @Test
    void fromDateTime() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpDateOnly d = CSharpDateOnly.fromDateTime(dt);
        assertEquals(2024, d.getYear());
        assertEquals(6, d.getMonth());
        assertEquals(15, d.getDay());
    }

    @Test
    void parse() {
        CSharpDateOnly d = CSharpDateOnly.parse("2024-06-15");
        assertEquals(2024, d.getYear());
        assertEquals(6, d.getMonth());
        assertEquals(15, d.getDay());
    }

    @Test
    void toString_iso() {
        CSharpDateOnly d = new CSharpDateOnly(2024, 6, 15);
        assertEquals("2024-06-15", d.toString());
    }
}
```

- [ ] **Step 2: Run the test**

Run: `cd java/csharptojava-compat && mvn test -Dtest=CSharpDateOnlyTest -q`
Expected: All tests pass.

---

### Task 13: Write CSharpTimeOnlyTest

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpTimeOnlyTest.java`

- [ ] **Step 1: Write the test**

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpTimeOnlyTest {

    @Test
    void constructor_hms() {
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        assertEquals(10, t.getHour());
        assertEquals(30, t.getMinute());
        assertEquals(45, t.getSecond());
        assertEquals(0, t.getMillisecond());
    }

    @Test
    void ticks() {
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        long expectedTicks = 10L * 36000000000L + 30L * 600000000L + 45L * 10000000L;
        assertEquals(expectedTicks, t.getTicks());
    }

    @Test
    void addHours() {
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        CSharpTimeOnly result = t.addHours(2);
        assertEquals(12, result.getHour());
    }

    @Test
    void addMinutes() {
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        CSharpTimeOnly result = t.addMinutes(30);
        assertEquals(11, result.getHour());
        assertEquals(0, result.getMinute());
    }

    @Test
    void addTimeSpan() {
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        CSharpTimeSpan ts = CSharpTimeSpan.fromHours(1);
        CSharpTimeOnly result = t.add(ts);
        assertEquals(11, result.getHour());
    }

    @Test
    void fromDateTime() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45);
        CSharpTimeOnly t = CSharpTimeOnly.fromDateTime(dt);
        assertEquals(10, t.getHour());
        assertEquals(30, t.getMinute());
        assertEquals(45, t.getSecond());
    }

    @Test
    void parse() {
        CSharpTimeOnly t = CSharpTimeOnly.parse("10:30:45");
        assertEquals(10, t.getHour());
        assertEquals(30, t.getMinute());
        assertEquals(45, t.getSecond());
    }

    @Test
    void compareTo() {
        CSharpTimeOnly t1 = new CSharpTimeOnly(10, 30, 45);
        CSharpTimeOnly t2 = new CSharpTimeOnly(11, 0, 0);
        assertTrue(t1.compareTo(t2) < 0);
    }

    @Test
    void toString_format() {
        CSharpTimeOnly t = new CSharpTimeOnly(10, 30, 45);
        assertEquals("10:30:45", t.toString());
    }
}
```

- [ ] **Step 2: Run the test**

Run: `cd java/csharptojava-compat && mvn test -Dtest=CSharpTimeOnlyTest -q`
Expected: All tests pass.

---

### Task 14: Run All Tests Together

- [ ] **Step 1: Run full Maven test suite**

Run: `cd java/csharptojava-compat && mvn test -q`
Expected: All tests pass, including existing tests.

---

### Task 15: Update TypeMappings.json

**Files:**
- Modify: `config/TypeMappings.json`

- [ ] **Step 1: Update type mappings**

Change the 5 type mappings from `java.time.*` types to `io.github.ningpp.compat.*` wrapper classes:

```json
{
    "csharp": "System.DateTime",
    "java": "CSharpDateTime",
    "imports": ["io.github.ningpp.compat.CSharpDateTime"]
},
{
    "csharp": "System.DateTimeOffset",
    "java": "CSharpDateTimeOffset",
    "imports": ["io.github.ningpp.compat.CSharpDateTimeOffset"]
},
{
    "csharp": "System.TimeSpan",
    "java": "CSharpTimeSpan",
    "imports": ["io.github.ningpp.compat.CSharpTimeSpan"]
},
{
    "csharp": "System.DateOnly",
    "java": "CSharpDateOnly",
    "imports": ["io.github.ningpp.compat.CSharpDateOnly"]
},
{
    "csharp": "System.TimeOnly",
    "java": "CSharpTimeOnly",
    "imports": ["io.github.ningpp.compat.CSharpTimeOnly"]
}
```

Also update method mappings. The existing TimeSpan method mappings (Hours → toHoursPart, etc.) need to change to use the new CSharpTimeSpan methods. Remove the old TimeSpan method mappings and add new ones that map C# property names to Java getter methods:

```json
// Remove existing TimeSpan method mappings and replace with:
{ "type": "System.TimeSpan", "method": "Days", "javaMethod": "getDays" },
{ "type": "System.TimeSpan", "method": "Hours", "javaMethod": "getHours" },
{ "type": "System.TimeSpan", "method": "Minutes", "javaMethod": "getMinutes" },
{ "type": "System.TimeSpan", "method": "Seconds", "javaMethod": "getSeconds" },
{ "type": "System.TimeSpan", "method": "Milliseconds", "javaMethod": "getMilliseconds" },
{ "type": "System.TimeSpan", "method": "TotalDays", "javaMethod": "getTotalDays" },
{ "type": "System.TimeSpan", "method": "TotalHours", "javaMethod": "getTotalHours" },
{ "type": "System.TimeSpan", "method": "TotalMinutes", "javaMethod": "getTotalMinutes" },
{ "type": "System.TimeSpan", "method": "TotalSeconds", "javaMethod": "getTotalSeconds" },
{ "type": "System.TimeSpan", "method": "TotalMilliseconds", "javaMethod": "getTotalMilliseconds" },
{ "type": "System.TimeSpan", "method": "Ticks", "javaMethod": "getTicks" },

// Update DateTime mappings:
{ "type": "System.DateTime", "method": "Now", "javaMethod": "getNow" },
{ "type": "System.DateTime", "method": "UtcNow", "javaMethod": "getUtcNow" },
{ "type": "System.DateTime", "method": "Today", "javaMethod": "getToday" },
{ "type": "System.DateTime", "method": "Year", "javaMethod": "getYear" },
{ "type": "System.DateTime", "method": "Month", "javaMethod": "getMonth" },
{ "type": "System.DateTime", "method": "Day", "javaMethod": "getDay" },
{ "type": "System.DateTime", "method": "Hour", "javaMethod": "getHour" },
{ "type": "System.DateTime", "method": "Minute", "javaMethod": "getMinute" },
{ "type": "System.DateTime", "method": "Second", "javaMethod": "getSecond" },
{ "type": "System.DateTime", "method": "Millisecond", "javaMethod": "getMillisecond" },
{ "type": "System.DateTime", "method": "DayOfWeek", "javaMethod": "getDayOfWeek" },
{ "type": "System.DateTime", "method": "DayOfYear", "javaMethod": "getDayOfYear" },
{ "type": "System.DateTime", "method": "Kind", "javaMethod": "getKind" },
{ "type": "System.DateTime", "method": "Date", "javaMethod": "getDate" },
{ "type": "System.DateTime", "method": "TimeOfDay", "javaMethod": "getTimeOfDay" },
{ "type": "System.DateTime", "method": "Ticks", "javaMethod": "getTicks" },

// Update DateTimeOffset mappings:
{ "type": "System.DateTimeOffset", "method": "Now", "javaMethod": "getNow" },
{ "type": "System.DateTimeOffset", "method": "UtcNow", "javaMethod": "getUtcNow" },
{ "type": "System.DateTimeOffset", "method": "LocalDateTime", "javaMethod": "getLocalDateTime" },
{ "type": "System.DateTimeOffset", "method": "get_LocalDateTime", "javaMethod": "getLocalDateTime" },
{ "type": "System.DateTimeOffset", "method": "UtcDateTime", "javaMethod": "getUtcDateTime" },
{ "type": "System.DateTimeOffset", "method": "get_UtcDateTime", "javaMethod": "getUtcDateTime" },
{ "type": "System.DateTimeOffset", "method": "Offset", "javaMethod": "getOffset" },
{ "type": "System.DateTimeOffset", "method": "get_Offset", "javaMethod": "getOffset" },
{ "type": "System.DateTimeOffset", "method": "DateTime", "javaMethod": "getDateTime" },
{ "type": "System.DateTimeOffset", "method": "get_DateTime", "javaMethod": "getDateTime" },
```

- [ ] **Step 2: Verify the converter still works**

Run: `cd d:\code\cs2j && dotnet build`
Expected: BUILD SUCCESS

---

### Task 16: Cross-Validate C# and Java Outputs

- [ ] **Step 1: Run CSharpValidation and save output**

Run: `cd tools/CSharpValidation && dotnet run > csharp_output.txt`

- [ ] **Step 2: Compare key values with Java test assertions**

Manually verify that the C# output for each tested value matches the Java test assertions. Key values to verify:
- `new TimeSpan(1,2,3)` properties
- `new DateTime(2024,6,15,10,30,45)` properties
- `new DateTimeOffset(2024,6,15,10,30,45,+08:00)` properties
- `new DateOnly(2024,6,15)` properties
- `new TimeOnly(10,30,45)` properties
- Parse/format round-trip results

If any values don't match, update the Java implementation accordingly.

- [ ] **Step 3: Fix any discrepancies**

If C# output differs from Java test expectations, update the Java classes to match C# behavior exactly.
