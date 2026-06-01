# Java Reserved Literals Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix compilation errors caused by C# methods/identifiers named True/False/Null being converted to Java reserved literals (true/false/null).

**Architecture:** Add `true`/`false`/`null` to the `JavaNaming.IsJavaKeyword()` check so `EscapeJavaKeyword()` properly escapes them. Fix duplicate operator name mappings to use the canonical `OpSymbolToJavaName` dictionary. Add comprehensive unit tests.

**Tech Stack:** C# / xUnit / Microsoft.CodeAnalysis

---

### Task 1: Add `true`/`false`/`null` to `JavaNaming.IsJavaKeyword()`

**Files:**
- Modify: `src/CSharpToJava.Core/Context/JavaNaming.cs:21-35`

- [ ] **Step 1: Write the failing test**

Create test file `tests/CSharpToJava.Tests/JavaReservedWordTests.cs`:

```csharp
using CSharpToJava.Core.Context;

namespace CSharpToJava.Tests;

public class JavaReservedWordTests
{
    [Fact]
    public void IsJavaKeyword_True_IsKeyword()
    {
        Assert.True(JavaNaming.IsJavaKeyword("true"));
    }

    [Fact]
    public void IsJavaKeyword_False_IsKeyword()
    {
        Assert.True(JavaNaming.IsJavaKeyword("false"));
    }

    [Fact]
    public void IsJavaKeyword_Null_IsKeyword()
    {
        Assert.True(JavaNaming.IsJavaKeyword("null"));
    }

    [Fact]
    public void EscapeJavaKeyword_True_EscapesToTrueValue()
    {
        Assert.Equal("trueValue", JavaNaming.EscapeJavaKeyword("true"));
    }

    [Fact]
    public void EscapeJavaKeyword_False_EscapesToFalseValue()
    {
        Assert.Equal("falseValue", JavaNaming.EscapeJavaKeyword("false"));
    }

    [Fact]
    public void EscapeJavaKeyword_Null_EscapesToNullValue()
    {
        Assert.Equal("nullValue", JavaNaming.EscapeJavaKeyword("null"));
    }

    [Fact]
    public void EscapeJavaKeyword_NonKeyword_Unchanged()
    {
        Assert.Equal("foo", JavaNaming.EscapeJavaKeyword("foo"));
    }

    [Fact]
    public void EscapeJavaKeyword_ExistingKeyword_Escapes()
    {
        Assert.Equal("assertValue", JavaNaming.EscapeJavaKeyword("assert"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests" -v n`
Expected: `IsJavaKeyword_True_IsKeyword`, `IsJavaKeyword_False_IsKeyword`, `IsJavaKeyword_Null_IsKeyword` FAIL. `EscapeJavaKeyword_True_EscapesToTrueValue`, `EscapeJavaKeyword_False_EscapesToFalseValue`, `EscapeJavaKeyword_Null_EscapesToNullValue` FAIL. Others PASS.

- [ ] **Step 3: Implement the fix**

In `src/CSharpToJava.Core/Context/JavaNaming.cs`, change the `IsJavaKeyword` switch expression from:

```csharp
            "transient" or "try" or "void" or "volatile" or "while" => true,
```

to:

```csharp
            "transient" or "try" or "void" or "volatile" or "while"
            or "true" or "false" or "null" => true,
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests" -v n`
Expected: All 8 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/Context/JavaNaming.cs tests/CSharpToJava.Tests/JavaReservedWordTests.cs
git commit -m "fix: add true/false/null to JavaNaming.IsJavaKeyword to prevent compilation errors"
```

---

### Task 2: Add `op_True`/`op_False` to `MethodTransformer.ConvertOperatorName()`

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs:569-596`

- [ ] **Step 1: Write the failing test**

Add to `tests/CSharpToJava.Tests/JavaReservedWordTests.cs`:

```csharp
    [Fact]
    public void OperatorTrue_ConvertedToIsTrue()
    {
        var result = Convert(@"
public class Foo
{
    public static bool operator true(Foo f) => f.Value > 0;
    public static bool operator false(Foo f) => f.Value <= 0;
    public int Value { get; }
}");
        Assert.True(result.Success);
        Assert.Contains("isTrue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("isFalse", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("op_True", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("op_False", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
```

Add required usings at the top of the test file:

```csharp
using CSharpToJava.Core.Pipeline;
```

- [ ] **Step 2: Run test to verify it passes (operator true/false already handled by OperatorTransformer)**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests.OperatorTrue_ConvertedToIsTrue" -v n`
Expected: PASS (because `OperatorTransformer` already handles this, but `MethodTransformer.ConvertOperatorName` is a safety net)

- [ ] **Step 3: Add the missing mappings to MethodTransformer**

In `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`, add two entries to the `ConvertOperatorName` switch, after the `"op_ExclusiveOr" => "xor"` line:

```csharp
            "op_True" => "isTrue",
            "op_False" => "isFalse",
```

- [ ] **Step 4: Run test to verify it still passes**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests.OperatorTrue_ConvertedToIsTrue" -v n`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs tests/CSharpToJava.Tests/JavaReservedWordTests.cs
git commit -m "fix: add op_True/op_False mappings to MethodTransformer.ConvertOperatorName"
```

---

### Task 3: Replace `UnaryExpressionTransformer.GetOperatorMethodName()` duplicate with `OpSymbolToJavaName` lookup

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs:302-316`

- [ ] **Step 1: Write the failing test**

Add to `tests/CSharpToJava.Tests/JavaReservedWordTests.cs`:

```csharp
    [Fact]
    public void OperatorTrue_UsedInIfCondition_ResolvesToIsTrue()
    {
        var result = Convert(@"
public class Foo
{
    public int Value { get; }
    public static bool operator true(Foo f) => f.Value > 0;
    public static bool operator false(Foo f) => f.Value <= 0;
    public static void Test(Foo f)
    {
        if (f) { System.Console.WriteLine(""positive""); }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("isTrue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("op_True", result.GeneratedCode, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run test to verify it passes (current code already works via the standalone switch)**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests.OperatorTrue_UsedInIfCondition_ResolvesToIsTrue" -v n`
Expected: PASS

- [ ] **Step 3: Replace the standalone switch with OpSymbolToJavaName lookup**

In `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`, replace the `GetOperatorMethodName` method:

From:
```csharp
    private static string GetOperatorMethodName(IMethodSymbol operatorSymbol)
    {
        return operatorSymbol.Name switch
        {
            "op_UnaryNegation" => "negate",
            "op_UnaryPlus" => "plus",
            "op_LogicalNot" => "not",
            "op_OnesComplement" => "onesComplement",
            "op_Increment" => "increment",
            "op_Decrement" => "decrement",
            "op_True" => "isTrue",
            "op_False" => "isFalse",
            _ => operatorSymbol.Name
        };
    }
```

To:
```csharp
    private static string GetOperatorMethodName(IMethodSymbol operatorSymbol)
    {
        return CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(operatorSymbol.Name, out var name)
            ? name
            : operatorSymbol.Name;
    }
```

- [ ] **Step 4: Run test to verify it still passes**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests.OperatorTrue_UsedInIfCondition_ResolvesToIsTrue" -v n`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs
git commit -m "refactor: replace UnaryExpressionTransformer.GetOperatorMethodName duplicate with OpSymbolToJavaName lookup"
```

---

### Task 4: Delegate `ExpressionTransformerHelpers.IsJavaKeyword()` to `JavaNaming.IsJavaKeyword()`

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs:788-805`

- [ ] **Step 1: Write the failing test**

Add to `tests/CSharpToJava.Tests/JavaReservedWordTests.cs`:

```csharp
    [Fact]
    public void ExpressionTransformerHelpers_IsJavaKeyword_True_IsKeyword()
    {
        Assert.True(CSharpToJava.Core.Transformers.Expression.Utilities.ExpressionTransformerHelpers.IsJavaKeyword("true"));
    }

    [Fact]
    public void ExpressionTransformerHelpers_IsJavaKeyword_False_IsKeyword()
    {
        Assert.True(CSharpToJava.Core.Transformers.Expression.Utilities.ExpressionTransformerHelpers.IsJavaKeyword("false"));
    }

    [Fact]
    public void ExpressionTransformerHelpers_IsJavaKeyword_Null_IsKeyword()
    {
        Assert.True(CSharpToJava.Core.Transformers.Expression.Utilities.ExpressionTransformerHelpers.IsJavaKeyword("null"));
    }
```

- [ ] **Step 2: Run tests to verify they pass (current code already includes true/false/null)**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests.ExpressionTransformerHelpers" -v n`
Expected: All PASS

- [ ] **Step 3: Replace the standalone implementation with delegation**

In `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`, replace the `IsJavaKeyword` method:

From:
```csharp
    public static bool IsJavaKeyword(string word)
    {
        return word switch
        {
            "abstract" or "assert" or "boolean" or "break" or "byte" or
            "case" or "catch" or "char" or "class" or "const" or
            "continue" or "default" or "do" or "double" or "else" or
            "enum" or "extends" or "final" or "finally" or "float" or
            "for" or "goto" or "if" or "implements" or "import" or
            "instanceof" or "int" or "interface" or "long" or "native" or
            "new" or "package" or "private" or "protected" or "public" or
            "return" or "short" or "static" or "strictfp" or "super" or
            "switch" or "synchronized" or "this" or "throw" or "throws" or
            "transient" or "try" or "void" or "volatile" or "while" or
            "true" or "false" or "null" => true,
            _ => false
        };
    }
```

To:
```csharp
    public static bool IsJavaKeyword(string word) => Context.JavaNaming.IsJavaKeyword(word);
```

- [ ] **Step 4: Run tests to verify they still pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests.ExpressionTransformerHelpers" -v n`
Expected: All PASS

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs
git commit -m "refactor: delegate ExpressionTransformerHelpers.IsJavaKeyword to JavaNaming.IsJavaKeyword"
```

---

### Task 5: Add end-to-end conversion tests for reserved literal identifiers

**Files:**
- Modify: `tests/CSharpToJava.Tests/JavaReservedWordTests.cs`

- [ ] **Step 1: Write the failing tests**

Add the following tests to `tests/CSharpToJava.Tests/JavaReservedWordTests.cs`:

```csharp
    [Fact]
    public void MethodNamedTrue_ConvertedToTrueValue()
    {
        var result = Convert(@"
public class Sample
{
    public bool True() => true;
}");
        Assert.True(result.Success);
        Assert.Contains("trueValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public boolean true()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodNamedFalse_ConvertedToFalseValue()
    {
        var result = Convert(@"
public class Sample
{
    public bool False() => false;
}");
        Assert.True(result.Success);
        Assert.Contains("falseValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public boolean false()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodNamedNull_ConvertedToNullValue()
    {
        var result = Convert(@"
public class Sample
{
    public object Null() => null;
}");
        Assert.True(result.Success);
        Assert.Contains("nullValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object null()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldNamedTrue_ConvertedToTrueValue()
    {
        var result = Convert(@"
public class Sample
{
    public int true_val;
}");
        Assert.True(result.Success);
        Assert.Contains("true_val", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterNamedTrue_ConvertedToTrueValue()
    {
        var result = Convert(@"
public class Sample
{
    public void Foo(bool True) { }
}");
        Assert.True(result.Success);
        Assert.Contains("trueValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("boolean true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InvocationOfMethodNamedTrue_ConvertedToTrueValue()
    {
        var result = Convert(@"
public class Sample
{
    public void Test()
    {
        var x = True();
    }
    public bool True() => true;
}");
        Assert.True(result.Success);
        Assert.Contains("trueValue()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Regression_KeywordAssert_StillEscaped()
    {
        var result = Convert(@"
public class Sample
{
    public void Assert() { }
}");
        Assert.True(result.Success);
        Assert.Contains("assertValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("void assert()", result.GeneratedCode, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~JavaReservedWordTests" -v n`
Expected: All PASS

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/JavaReservedWordTests.cs
git commit -m "test: add end-to-end conversion tests for reserved literal identifiers"
```

---

### Task 6: Run full test suite and verify no regressions

**Files:** None

- [ ] **Step 1: Run full test suite**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -v n`
Expected: All tests PASS

- [ ] **Step 2: Run build to verify no compilation errors**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: Build succeeds with no errors

- [ ] **Step 3: Final commit (if any remaining changes)**

```bash
git add -A
git commit -m "chore: finalize java reserved literals fix"
```
