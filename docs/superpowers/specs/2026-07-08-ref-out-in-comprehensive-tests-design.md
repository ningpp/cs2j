# Ref/Out/In Comprehensive Unit Tests Design

## Summary

Write comprehensive unit tests verifying the correctness of C# `ref`/`out`/`in` parameter conversion to Java, covering struct types, basic data types, generics, and project/sln-level conversion scenarios.

## Context

The cs2j converter transforms C# `ref`/`out` parameters into Java holder objects (e.g., `IntHolder`, `ObjectHolder<T>`) and `in` parameters into pass-by-value. Existing tests cover basic holder type mapping and simple usage, but lack coverage for struct types, generics, edge cases, and project-level conversion.

## File Structure

### New Files

1. **`tests/CSharpToJava.ConversionTests/Categories/RefOutInComprehensiveTests.cs`**
   - Inherits from `ConversionTestBase`
   - Uses `[Trait("Category", "...")]` for filtering
   - ~40 test methods across 4 categories

2. **`tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs`**
   - Project-level conversion tests using fixture solution
   - ~4 test methods

3. **`tests/SampleRefOutInSolution/SampleRefOutIn.sln`**
4. **`tests/SampleRefOutInSolution/SampleRefOutInLib/SampleRefOutInLib.csproj`**
5. **`tests/SampleRefOutInSolution/SampleRefOutInLib/Calculator.cs`**
6. **`tests/SampleRefOutInSolution/SampleRefOutInLib/Point.cs`**
7. **`tests/SampleRefOutInSolution/SampleRefOutInLib/GenericHolder.cs`**
8. **`tests/SampleRefOutInSolution/SampleRefOutInApp/SampleRefOutInApp.csproj`**
9. **`tests/SampleRefOutInSolution/SampleRefOutInApp/Program.cs`**

## Test Cases

### Category: BasicTypes (~12 tests)

| # | Test Name | C# Input Pattern | Java Assertion |
|---|-----------|-------------------|----------------|
| 1 | `RefDecimal_ConvertsToObjectHolder` | `ref decimal a` | `ObjectHolder<BigDecimal> a` |
| 2 | `RefNullableInt_ConvertsToObjectHolder` | `ref int? a` | `ObjectHolder<Integer> a` |
| 3 | `OutNullableDouble_ConvertsToObjectHolder` | `out double? a` | `ObjectHolder<Double> a` |
| 4 | `RefEnum_ConvertsToObjectHolder` | `ref MyEnum a` | `ObjectHolder<MyEnum> a` |
| 5 | `OutString_ConvertsToObjectHolderWithWriteback` | `out string a; a = "x"` | `ObjectHolder<String> a; a.value = "x"` |
| 6 | `InPrimitive_PassesByValue` | `in int a` | `int a` (no holder) |
| 7 | `InReferenceType_PassesByValue` | `in string a` | `String a` (no holder) |
| 8 | `InStruct_PassesByValue` | `in Point a` (struct) | `Point a` (no holder) |
| 9 | `MultiOutParameters_GeneratesMultipleHolders` | `out int a, out double b` | `IntHolder a, DoubleHolder b` |
| 10 | `MixedRefOutIn_AllConvertedCorrectly` | `ref int a, out double b, in string c` | `IntHolder a, DoubleHolder b, String c` |
| 11 | `RefOutUsedInExpression_ValueAccess` | `ref int a; return a + 1` | `a.value + 1` |
| 12 | `OutVarReadBack_AfterCall` | `out var result; use(result)` | `result = _resultHolder.value; ... use(result)` |

### Category: StructTypes (~8 tests)

| # | Test Name | C# Input Pattern | Java Assertion |
|---|-----------|-------------------|----------------|
| 1 | `RefStruct_ConvertsToObjectHolder` | `ref Point a` (struct Point) | `ObjectHolder<Point> a` |
| 2 | `OutStruct_ConvertsToObjectHolderWithWriteback` | `out Point a` | `ObjectHolder<Point> a; a.value = ...` |
| 3 | `RefStruct_FieldAccessThroughHolder` | `ref Point a; a.X = 5` | `a.value.X = 5` |
| 4 | `OutStruct_MethodAssignsAllFields` | `out Point a; a = new Point(1,2)` | `a.value = new Point(1,2)` |
| 5 | `StructWithRefOutMethod_MethodSignature` | `struct S { void M(ref int x) }` | `void m(IntHolder x)` |
| 6 | `StructWithRefOutMethod_CallSite` | `S.M(ref val)` | `IntHolder _valRef = new IntHolder(val); ...` |
| 7 | `RefStructEffectivelyReadOnly_NoHolder` | `ref Point p` in a method that only reads `p.X` (never writes) | direct pass, no ObjectHolder wrapping (IsRefParamEffectivelyReadOnly optimization) |
| 8 | `StructAsOutArg_ReadBackFieldAccess` | `out Point p; use(p.X)` | `p = _pOutHolder.value; ... use(p.X)` |

### Category: GenericTypes (~8 tests)

| # | Test Name | C# Input Pattern | Java Assertion |
|---|-----------|-------------------|----------------|
| 1 | `RefGenericParameter_ConvertsToObjectHolder` | `ref T a` | `ObjectHolder<T> a` |
| 2 | `OutGenericParameter_ConvertsToObjectHolder` | `out T a` | `ObjectHolder<T> a` |
| 3 | `GenericMethod_RefStructConstraint` | `void M<T>(ref T a) where T : struct` | `ObjectHolder<T> a` |
| 4 | `GenericClass_RefParameter` | `class C<T> { void M(ref T a) }` | `ObjectHolder<T> a` |
| 5 | `GenericMethod_OutWithDefaultAssignment` | `void M<T>(out T a) where T : new()` | `ObjectHolder<T> a` |
| 6 | `RefListOfGeneric_HolderType` | `ref List<T> a` | `ObjectHolder<CSharpList<T>> a` |
| 7 | `OutGenericUsedInExpression` | `out T a; return a.ToString()` | `a.value.toString()` |
| 8 | `GenericStruct_RefParameter` | `struct S<T> {}; ref S<int> a` | `ObjectHolder<S<Integer>> a` |

### Category: EdgeCases (~12 tests)

| # | Test Name | C# Input Pattern | Java Assertion |
|---|-----------|-------------------|----------------|
| 1 | `OutDiscard_PassesScratchArray` | `out _` | `new Object[1]` |
| 2 | `OutDiscardIdentifier_PassesScratchArray` | `out _` (IdentifierNameSyntax) | `new Object[1]` |
| 3 | `RefForwarding_PassesHolderDirectly` | method takes `ref int a`, passes to another `ref int` method | passes holder directly, no re-wrap |
| 4 | `OutForwarding_PassesHolderDirectly` | method takes `out int a`, passes to another `out int` method | passes holder directly |
| 5 | `NestedOutInCondition` | `if (TryGet(out var x)) { Use(x); }` | Holder pre-statement + readback |
| 6 | `MultipleOutInSingleCall` | `M(out int a, out int b, out int c)` | 3 IntHolders |
| 7 | `InKeyword_RefReadonly_PassesByValue` | `in int a` (C# `in` = `ref readonly`) | `int a` (passes by value, no holder) |
| 8 | `RefMemberAccess_WrapsInHolder` | `ref obj.Field` | `Holder h = new Holder(obj.field); ... obj.field = h.value` |
| 9 | `RefElementAccess_WrapsInHolder` | `ref arr[i]` | `Holder h = new Holder(arr[i]); ... arr[i] = h.value` |
| 10 | `OutVarInForeach_ReadBackWorks` | `foreach (var x in items) { M(out var y); Use(y); }` | y declared before use |
| 11 | `RefAfterOut_SameVariable_HolderSeeded` | `out int a; ... ref int a` | ref holder seeded with current value |
| 12 | `OutVarTypeInference_ResolvesCorrectType` | `out var x` where method declares `out double` | `DoubleHolder`, readback as `double` |

### Project-Level Tests (~4 tests)

| # | Test Name | What It Verifies |
|---|-----------|------------------|
| 1 | `LibProject_RefOutInMethods_ConvertCorrectly` | Library's ref/out methods generate correct holder signatures |
| 2 | `AppProject_RefCallAcrossProjects_GeneratesHolderAtCallSite` | App calling lib's ref method generates holder + writeback |
| 3 | `SlnProject_FullConversion_AllFilesSucceed` | Convert entire solution, all files succeed with no errors |
| 4 | `SlnProject_RefOutStruct_GeneratesObjectHolder` | Struct passed as ref/out across project boundary uses ObjectHolder |

## Fixture Solution Structure

### SampleRefOutInLib

**Point.cs:**
```csharp
namespace SampleRefOutInLib
{
    public struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
}
```

**Calculator.cs:**
```csharp
namespace SampleRefOutInLib
{
    public class Calculator
    {
        public static void Swap(ref int a, ref int b) { int t = a; a = b; b = t; }
        public static bool TryParse(string s, out Point p) { p = new Point(0, 0); return true; }
    }
}
```

**GenericHolder.cs:**
```csharp
namespace SampleRefOutInLib
{
    public class GenericHolder<T> where T : struct
    {
        public T Value;
        public void Update(ref T value) { Value = value; value = Value; }
    }
}
```

### SampleRefOutInApp

**Program.cs:**
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

## Test Infrastructure

- Single-file tests use `ConversionTestBase.Convert(string sourceCode)` + `AssertConversion(result, markers...)`
- Project-level tests use `ConversionPipeline.ConvertProjectWithPartialMergeAsync(dir, options)`
- All tests use xUnit `[Fact]` / `[Theory]`
- `[Trait("Category", "...")]` enables filtering: `dotnet test --filter "Category=StructTypes"`

## Success Criteria

1. All ~44 tests pass with `dotnet test`
2. Tests cover ref/out/in with: all primitive types, struct types, generic types
3. Edge cases verified: discard, forwarding, nesting, member/element access, readonly optimization
4. Project-level conversion verified for csproj and sln scenarios
5. No test produces false positives (each test validates specific Java output patterns)
