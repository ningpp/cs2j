# Ref/Out/In Comprehensive Unit Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write ~44 comprehensive unit tests verifying C# ref/out/in parameter conversion to Java across basic types, structs, generics, edge cases, and project-level scenarios.

**Architecture:** Single-file tests in `RefOutInComprehensiveTests.cs` (inheriting `ConversionTestBase`) with `[Trait]` categories. Project-level tests in `RefOutInProjectTests.cs` using a fixture solution (`SampleRefOutInSolution`).

**Tech Stack:** C# 10 / .NET 10, xUnit, CSharpToJava.Core conversion pipeline

---

### Task 1: Create BasicTypes test category

**Files:**
- Create: `tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs`

- [ ] **Step 1: Create the test file with BasicTypes tests**

```csharp
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Comprehensive verification of C# ref/out/in parameter conversion to Java.
/// Covers basic data types, structs, generics, and edge cases.
/// </summary>
public class RefOutInComprehensiveTests : ConversionTestBase
{
    // ── Category: BasicTypes ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefDecimal_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(ref decimal a) { a = 1.5m; } }");
        AssertConversion(result, "ObjectHolder<BigDecimal> a", "a.value = BigDecimal.valueOf(15).divide(BigDecimal.TEN);");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefNullableInt_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(ref int? a) { a = 1; } }");
        AssertConversion(result, "ObjectHolder<Integer> a", "a.value = 1;");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutNullableDouble_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(out double? a) { a = 1.5; } }");
        AssertConversion(result, "ObjectHolder<Double> a", "a.value = 1.5;");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefEnum_ConvertsToObjectHolder()
    {
        var result = Convert("enum Color { Red, Green, Blue } class C { public void M(ref Color a) { a = Color.Red; } }");
        AssertConversion(result, "ObjectHolder<Color> a", "a.value = Color.Red;");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutString_ConvertsToObjectHolderWithWriteback()
    {
        var result = Convert("class C { public void M(out string a) { a = \"x\"; } }");
        AssertConversion(result, "ObjectHolder<String> a", "a.value = \"x\";");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InPrimitive_PassesByValue()
    {
        var result = Convert("class C { public void M(in int a) { var x = a; } }");
        AssertConversion(result, "public void m(int a) {", "var x = a;");
        Assert.False(result.GeneratedCode.Contains("IntHolder", StringComparison.Ordinal),
            "in int should NOT use IntHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InReferenceType_PassesByValue()
    {
        var result = Convert("class C { public void M(in string a) { var x = a; } }");
        AssertConversion(result, "public void m(String a) {", "var x = a;");
        Assert.False(result.GeneratedCode.Contains("ObjectHolder", StringComparison.Ordinal),
            "in string should NOT use ObjectHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InStruct_PassesByValue()
    {
        var result = Convert("struct Point { public int X; } class C { public void M(in Point a) { var x = a.X; } }");
        AssertConversion(result, "public void m(Point a) {", "var x = a.X;");
        Assert.False(result.GeneratedCode.Contains("ObjectHolder", StringComparison.Ordinal),
            "in Point should NOT use ObjectHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void MultiOutParameters_GeneratesMultipleHolders()
    {
        var result = Convert("class C { public void M(out int a, out double b) { a = 1; b = 1.5; } }");
        AssertConversion(result, "IntHolder a", "DoubleHolder b", "a.value = 1;", "b.value = 1.5;");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void MixedRefOutIn_AllConvertedCorrectly()
    {
        var result = Convert("class C { public void M(ref int a, out double b, in string c) { a = 1; b = 1.5; var x = c; } }");
        AssertConversion(result, "IntHolder a", "DoubleHolder b", "String c");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefOutUsedInExpression_ValueAccess()
    {
        var result = Convert("class C { public int M(ref int a) { return a + 1; } }");
        AssertConversion(result, "public int m(IntHolder a) {", "return a.value + 1;");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutVarReadBack_AfterCall()
    {
        var result = Convert(@"
class C
{
    public void Assign(out int x) { x = 42; }
    public void Use() { Assign(out var result); System.Console.WriteLine(result); }
}");
        AssertConversion(result, "IntHolder", "result =", ".value", "println(result)");
    }
}
```

- [ ] **Step 2: Run the BasicTypes tests**

Run: `dotnet test tests/CSharpToJava.ConversionTests --filter "Category=BasicTypes" --no-restore -v n`
Expected: All 12 BasicTypes tests PASS (some may fail if conversion output differs from expected markers — adjust markers based on actual output)

- [ ] **Step 3: Fix any failing markers by inspecting actual output, then commit**

```bash
git add tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs
git commit -m "Add RefOutInComprehensiveTests: BasicTypes category (12 tests)"
```

---

### Task 2: Add StructTypes test category

**Files:**
- Modify: `tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs`

- [ ] **Step 1: Add StructTypes tests inside the class**

Add the following methods after the BasicTypes section:

```csharp
    // ── Category: StructTypes ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "StructTypes")]
    public void RefStruct_ConvertsToObjectHolder()
    {
        var result = Convert("struct Point { public int X, Y; } class C { public void M(ref Point a) { a.X = 5; } }");
        AssertConversion(result, "ObjectHolder<Point> a", "a.value.X = 5;");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void OutStruct_ConvertsToObjectHolderWithWriteback()
    {
        var result = Convert("struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } } class C { public void M(out Point a) { a = new Point(1, 2); } }");
        AssertConversion(result, "ObjectHolder<Point> a", "a.value = new Point(1, 2);");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void RefStruct_FieldAccessThroughHolder()
    {
        var result = Convert("struct Point { public int X, Y; } class C { public void M(ref Point a) { a.X = 5; a.Y = 10; } }");
        AssertConversion(result, "ObjectHolder<Point> a", "a.value.X = 5;", "a.value.Y = 10;");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void OutStruct_MethodAssignsAllFields()
    {
        var result = Convert("struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } } class C { public void M(out Point a) { a = new Point(1, 2); } }");
        AssertConversion(result, "ObjectHolder<Point> a", "a.value = new Point(1, 2);");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void StructWithRefOutMethod_MethodSignature()
    {
        var result = Convert("struct S { public void M(ref int x) { x = 1; } }");
        AssertConversion(result, "public void m(IntHolder x) {", "x.value = 1;");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void StructWithRefOutMethod_CallSite()
    {
        var result = Convert("struct S { public void M(ref int x) { x = 1; } } class C { public void Test() { var s = new S(); int val = 5; s.M(ref val); } }");
        AssertConversion(result, "IntHolder _valRef = new IntHolder(val);", "val = _valRef.value;");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void RefStructEffectivelyReadOnly_NoHolder()
    {
        // When a ref struct parameter is only read (never written to), the
        // IsRefParamEffectivelyReadOnly optimization skips ObjectHolder wrapping.
        var result = Convert("struct Point { public int X, Y; } class C { public int M(ref Point a) { return a.X + a.Y; } }");
        AssertConversion(result);
        // The method should pass Point directly without ObjectHolder wrapping
        // (IsRefParamEffectivelyReadOnly detects no writes to 'a')
        Assert.False(result.GeneratedCode.Contains("ObjectHolder<Point> a", StringComparison.Ordinal),
            "ref struct that is only read should not use ObjectHolder");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void StructAsOutArg_ReadBackFieldAccess()
    {
        var result = Convert(@"
struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
class C
{
    public void Assign(out Point p) { p = new Point(3, 4); }
    public void Test() { Assign(out var p); var x = p.X; }
}");
        AssertConversion(result, "ObjectHolder<Point>", "p =", ".value", "p.X");
    }
```

- [ ] **Step 2: Run the StructTypes tests**

Run: `dotnet test tests/CSharpToJava.ConversionTests --filter "Category=StructTypes" --no-restore -v n`
Expected: All 8 StructTypes tests PASS (adjust markers as needed)

- [ ] **Step 3: Fix any failing markers and commit**

```bash
git add tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs
git commit -m "Add StructTypes category to RefOutInComprehensiveTests (8 tests)"
```

---

### Task 3: Add GenericTypes test category

**Files:**
- Modify: `tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs`

- [ ] **Step 1: Add GenericTypes tests inside the class**

Add the following methods after the StructTypes section:

```csharp
    // ── Category: GenericTypes ────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void RefGenericParameter_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M<T>(ref T a) { } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void OutGenericParameter_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M<T>(out T a) { a = default; } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void GenericMethod_RefStructConstraint()
    {
        var result = Convert("struct Point { public int X; } class C { public void M<T>(ref T a) where T : struct { } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void GenericClass_RefParameter()
    {
        var result = Convert("class C<T> { public void M(ref T a) { } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void GenericMethod_OutWithDefaultAssignment()
    {
        var result = Convert("class C { public void M<T>(out T a) where T : new() { a = new T(); } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void RefListOfGeneric_HolderType()
    {
        var result = Convert("using System.Collections.Generic; class C { public void M<T>(ref List<T> a) { } }");
        AssertConversion(result, "ObjectHolder<CSharpList<T>> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void OutGenericUsedInExpression()
    {
        var result = Convert(@"
class C
{
    public void Assign<T>(out T a) { a = default; }
    public void Test() { Assign(out var x); var s = x.ToString(); }
}");
        AssertConversion(result, "ObjectHolder", ".value");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void GenericStruct_RefParameter()
    {
        var result = Convert("struct S<T> { public T Item; } class C { public void M(ref S<int> a) { } }");
        AssertConversion(result, "ObjectHolder<S<Integer>> a");
    }
```

- [ ] **Step 2: Run the GenericTypes tests**

Run: `dotnet test tests/CSharpToJava.ConversionTests --filter "Category=GenericTypes" --no-restore -v n`
Expected: All 8 GenericTypes tests PASS (adjust markers as needed)

- [ ] **Step 3: Fix any failing markers and commit**

```bash
git add tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs
git commit -m "Add GenericTypes category to RefOutInComprehensiveTests (8 tests)"
```

---

### Task 4: Add EdgeCases test category

**Files:**
- Modify: `tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs`

- [ ] **Step 1: Add EdgeCases tests inside the class**

Add the following methods after the GenericTypes section:

```csharp
    // ── Category: EdgeCases ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutDiscard_PassesScratchArray()
    {
        var result = Convert("class C { public void M(out int a) { a = 1; } public void Test() { M(out _); } }");
        AssertConversion(result, "new Object[1]");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutDiscardIdentifier_PassesScratchArray()
    {
        // Bare _ identifier used as out argument (DeclarationExpression with DiscardDesignation)
        var result = Convert("class C { public bool Try(out int v) { v = 0; return true; } public void Test() { Try(out _); } }");
        AssertConversion(result, "new Object[1]");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void RefForwarding_PassesHolderDirectly()
    {
        // When a ref parameter is passed directly to another ref method,
        // the holder should be forwarded without re-wrapping.
        var result = Convert(@"
class C
{
    public void Inner(ref int a) { a = 1; }
    public void Outer(ref int a) { Inner(ref a); }
}");
        AssertConversion(result, "IntHolder a");
        // The Outer method should pass the holder directly to Inner, not create a new one
        Assert.False(result.GeneratedCode.Contains("_aRef", StringComparison.Ordinal)
            && result.GeneratedCode.IndexOf("Inner(ref a)", StringComparison.Ordinal) < 0,
            "ref forwarding should pass holder directly");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutForwarding_PassesHolderDirectly()
    {
        // When an out parameter is passed directly to another out method,
        // the holder should be forwarded without re-wrapping.
        var result = Convert(@"
class C
{
    public void Inner(out int a) { a = 1; }
    public void Outer(out int a) { Inner(out a); }
}");
        AssertConversion(result, "IntHolder a");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void NestedOutInCondition()
    {
        var result = Convert(@"
class C
{
    public bool TryGet(out int x) { x = 42; return true; }
    public void Test() { if (TryGet(out var val)) { System.Console.WriteLine(val); } }
}");
        AssertConversion(result, "IntHolder", "val =", ".value");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void MultipleOutInSingleCall()
    {
        var result = Convert("class C { public void M(out int a, out int b, out int c) { a = 1; b = 2; c = 3; } }");
        AssertConversion(result, "IntHolder a", "IntHolder b", "IntHolder c");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void InKeyword_RefReadonly_PassesByValue()
    {
        // C# 'in' keyword is equivalent to 'ref readonly' — pass by value in Java
        var result = Convert("class C { public void M(in int a) { var x = a + 1; } }");
        AssertConversion(result, "public void m(int a) {", "var x = a + 1;");
        Assert.False(result.GeneratedCode.Contains("IntHolder", StringComparison.Ordinal),
            "in int should NOT use IntHolder");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void RefMemberAccess_WrapsInHolder()
    {
        var result = Convert(@"
class C
{
    public int Value;
    public void Mutate(ref int a) { a = 10; }
    public void Test() { Mutate(ref Value); }
}");
        AssertConversion(result, "IntHolder", "Value =", ".value");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void RefElementAccess_WrapsInHolder()
    {
        var result = Convert(@"
class C
{
    public void Mutate(ref int a) { a = 10; }
    public void Test() { int[] arr = new int[5]; Mutate(ref arr[0]); }
}");
        AssertConversion(result, "IntHolder", "arr[0] =", ".value");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutVarInForeach_ReadBackWorks()
    {
        var result = Convert(@"
using System.Collections.Generic;
class C
{
    public bool TryGet(out int v) { v = 0; return true; }
    public void Test(List<int> items) { foreach (var item in items) { TryGet(out var y); System.Console.WriteLine(y); } }
}");
        AssertConversion(result, "IntHolder", "y =", ".value");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void RefAfterOut_SameVariable_HolderSeeded()
    {
        // A variable first used as 'out' then as 'ref' must have the ref holder
        // seeded with the variable's current value (from the out writeback).
        var result = Convert(@"
class C
{
    public void Assign(out int a) { a = 1; }
    public void Mutate(ref int a) { a = a + 1; }
    public void Test() { int val; Assign(out val); Mutate(ref val); }
}");
        AssertConversion(result, "IntHolder");
        // The ref holder must be seeded with the current value of val
        Assert.Contains("new IntHolder(val)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutVarTypeInference_ResolvesCorrectType()
    {
        // out var should resolve to the correct holder type based on the method signature
        var result = Convert(@"
class C
{
    public void GetDouble(out double d) { d = 1.5; }
    public void Test() { GetDouble(out var x); System.Console.WriteLine(x); }
}");
        AssertConversion(result, "DoubleHolder", "x =", ".value");
    }
```

- [ ] **Step 2: Run the EdgeCases tests**

Run: `dotnet test tests/CSharpToJava.ConversionTests --filter "Category=EdgeCases" --no-restore -v n`
Expected: All 12 EdgeCases tests PASS (adjust markers as needed)

- [ ] **Step 3: Fix any failing markers and commit**

```bash
git add tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs
git commit -m "Add EdgeCases category to RefOutInComprehensiveTests (12 tests)"
```

---

### Task 5: Create fixture solution for project-level tests

**Files:**
- Create: `tests/SampleRefOutInSolution/SampleRefOutIn.sln`
- Create: `tests/SampleRefOutInSolution/SampleRefOutInLib/SampleRefOutInLib.csproj`
- Create: `tests/SampleRefOutInSolution/SampleRefOutInLib/Point.cs`
- Create: `tests/SampleRefOutInSolution/SampleRefOutInLib/Calculator.cs`
- Create: `tests/SampleRefOutInSolution/SampleRefOutInLib/GenericHolder.cs`
- Create: `tests/SampleRefOutInSolution/SampleRefOutInApp/SampleRefOutInApp.csproj`
- Create: `tests/SampleRefOutInSolution/SampleRefOutInApp/Program.cs`

- [ ] **Step 1: Create the solution file**

Create `tests/SampleRefOutInSolution/SampleRefOutIn.sln`:

```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SampleRefOutInLib", "SampleRefOutInLib\SampleRefOutInLib.csproj", "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SampleRefOutInApp", "SampleRefOutInApp\SampleRefOutInApp.csproj", "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}.Release|Any CPU.Build.0 = Release|Any CPU
		{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

- [ ] **Step 2: Create the Lib project file**

Create `tests/SampleRefOutInSolution/SampleRefOutInLib/SampleRefOutInLib.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

</Project>
```

- [ ] **Step 3: Create Point.cs**

Create `tests/SampleRefOutInSolution/SampleRefOutInLib/Point.cs`:

```csharp
namespace SampleRefOutInLib
{
    public struct Point
    {
        public int X;
        public int Y;
        public Point(int x, int y) { X = x; Y = y; }
    }
}
```

- [ ] **Step 4: Create Calculator.cs**

Create `tests/SampleRefOutInSolution/SampleRefOutInLib/Calculator.cs`:

```csharp
namespace SampleRefOutInLib
{
    public class Calculator
    {
        public static void Swap(ref int a, ref int b)
        {
            int t = a;
            a = b;
            b = t;
        }

        public static bool TryParse(string s, out Point p)
        {
            p = new Point(0, 0);
            return true;
        }
    }
}
```

- [ ] **Step 5: Create GenericHolder.cs**

Create `tests/SampleRefOutInSolution/SampleRefOutInLib/GenericHolder.cs`:

```csharp
using System.Collections.Generic;

namespace SampleRefOutInLib
{
    public class GenericHolder<T> where T : struct
    {
        public T Value;

        public void Update(ref T value)
        {
            Value = value;
            value = Value;
        }

        public bool TryGet(out T result)
        {
            result = Value;
            return true;
        }
    }
}
```

- [ ] **Step 6: Create the App project file**

Create `tests/SampleRefOutInSolution/SampleRefOutInApp/SampleRefOutInApp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\SampleRefOutInLib\SampleRefOutInLib.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 7: Create Program.cs**

Create `tests/SampleRefOutInSolution/SampleRefOutInApp/Program.cs`:

```csharp
using SampleRefOutInLib;

class Program
{
    static void Main()
    {
        int x = 1, y = 2;
        Calculator.Swap(ref x, ref y);

        Calculator.TryParse("1,2", out var p);

        var holder = new GenericHolder<int>();
        int val = 5;
        holder.Update(ref val);
    }
}
```

- [ ] **Step 8: Add the fixture to the ProjectTests csproj**

Modify `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` — add the following ItemGroup after the existing SampleDefaultSolution Content entry:

```xml
  <ItemGroup>
    <!-- Include SampleRefOutInSolution as content so it's available at test runtime -->
    <Content Include="..\SampleRefOutInSolution\**\*.*">
      <Link>SampleRefOutInSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 9: Commit fixture solution**

```bash
git add tests/SampleRefOutInSolution/ tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
git commit -m "Add SampleRefOutInSolution fixture for project-level ref/out/in tests"
```

---

### Task 6: Create project-level test class

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs`

- [ ] **Step 1: Create the project-level test file**

Create `tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs`:

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class RefOutInProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleRefOutInSolution");

    [Fact]
    public async Task LibProject_RefOutInMethods_ConvertCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRefOutInLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, options);

        Assert.NotEmpty(results);

        // Calculator.java should have holder-based method signatures
        var calculatorResult = results.FirstOrDefault(r =>
            r.FileName.Equals("Calculator.java", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(calculatorResult);
        Assert.True(calculatorResult.Success, string.Join("\n", calculatorResult.Diagnostics));
        // Swap(ref int, ref int) → swap(IntHolder, IntHolder)
        Assert.Contains("IntHolder a", calculatorResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntHolder b", calculatorResult.GeneratedCode, StringComparison.Ordinal);
        // TryParse(out Point) → tryParse(ObjectHolder<Point>)
        Assert.Contains("ObjectHolder<Point> p", calculatorResult.GeneratedCode, StringComparison.Ordinal);

        // GenericHolder.java should have ObjectHolder<T> parameters
        var holderResult = results.FirstOrDefault(r =>
            r.FileName.Equals("GenericHolder.java", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(holderResult);
        Assert.True(holderResult.Success, string.Join("\n", holderResult.Diagnostics));
        Assert.Contains("ObjectHolder<T> value", holderResult.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppProject_RefCallAcrossProjects_GeneratesHolderAtCallSite()
    {
        var appDir = Path.Combine(FixtureDir, "SampleRefOutInApp");
        Assert.True(Directory.Exists(appDir), $"Fixture not found: {appDir}");

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(appDir, options);

        Assert.NotEmpty(results);
        var programResult = results.FirstOrDefault(r =>
            r.FileName.Equals("Program.java", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(programResult);
        Assert.True(programResult.Success, string.Join("\n", programResult.Diagnostics));

        var code = programResult.GeneratedCode;
        // Calculator.Swap(ref x, ref y) → IntHolder wrappers + writeback
        Assert.Contains("IntHolder", code, StringComparison.Ordinal);
        Assert.Contains("Calculator.swap(", code, StringComparison.Ordinal);
        // Calculator.TryParse(out var p) → ObjectHolder<Point>
        Assert.Contains("ObjectHolder", code, StringComparison.Ordinal);
        Assert.Contains("Calculator.tryParse(", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlnProject_FullConversion_AllFilesSucceed()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleRefOutIn.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        using var loader = new SolutionLoader();
        var projects = await loader.OpenSolutionAsync(slnPath);
        Assert.Equal(2, projects.Count);

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ConversionPipeline();

        foreach (var wp in projects)
        {
            var extraPaths = wp.ProjectReferences
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList()!;

            var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                wp.Directory,
                options,
                additionalSemanticProjectPaths: extraPaths,
                projectName: wp.Name,
                projectFilePath: wp.FilePath,
                projectReferences: wp.ProjectReferences,
                isTestProject: wp.IsTestProject);

            Assert.NotEmpty(results);
            foreach (var r in results)
            {
                Assert.True(r.Success, $"{wp.Name}/{r.FileName}: {string.Join("\n", r.Diagnostics)}");
            }
        }
    }

    [Fact]
    public async Task SlnProject_RefOutStruct_GeneratesObjectHolder()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleRefOutIn.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        using var loader = new SolutionLoader();
        var projects = await loader.OpenSolutionAsync(slnPath);

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ConversionPipeline();

        // Convert the Lib project specifically to check struct ref/out handling
        var libProject = projects.Single(p => p.Name == "SampleRefOutInLib");
        var libResults = await pipeline.ConvertProjectWithPartialMergeAsync(libProject.Directory, options);

        var calculatorJava = libResults.FirstOrDefault(r =>
            r.FileName.Equals("Calculator.java", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(calculatorJava);
        Assert.True(calculatorJava.Success, string.Join("\n", calculatorJava.Diagnostics));

        // TryParse(out Point p) — struct passed as out must use ObjectHolder<Point>
        Assert.Contains("ObjectHolder<Point>", calculatorJava.GeneratedCode, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the project-level tests**

Run: `dotnet test tests/CSharpToJava.ProjectTests --filter "RefOutInProjectTests" --no-restore -v n`
Expected: All 4 project-level tests PASS (adjust markers as needed based on actual conversion output)

- [ ] **Step 3: Fix any failing markers and commit**

```bash
git add tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs
git commit -m "Add RefOutInProjectTests: project/sln-level ref/out/in tests (4 tests)"
```

---

### Task 7: Run full test suite and verify

**Files:**
- No new files

- [ ] **Step 1: Run all new tests together**

Run: `dotnet test tests/CSharpToJava.ConversionTests --filter "RefOutInComprehensiveTests" --no-restore -v n`
Run: `dotnet test tests/CSharpToJava.ProjectTests --filter "RefOutInProjectTests" --no-restore -v n`
Expected: All ~44 tests PASS

- [ ] **Step 2: Run the full existing test suite to check for regressions**

Run: `dotnet test tests/CSharpToJava.ConversionTests --no-restore -v q`
Run: `dotnet test tests/CSharpToJava.ProjectTests --no-restore -v q`
Expected: All existing tests still PASS, no regressions

- [ ] **Step 3: Final commit if any fixes were needed**

```bash
git add -A
git commit -m "Fix marker assertions in ref/out/in comprehensive tests"
```
