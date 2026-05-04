# Fix Hex Literal Suffix Mishandling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix `TransformNumericLiteral()` so hex/binary literals ending in `d`, `D`, `f`, or `F` are not incorrectly processed as having C# type suffixes.

**Architecture:** Insert a single early-return guard before the suffix-handling block in `LiteralExpressionTransformer.TransformNumericLiteral()`. Hex (`0x`) and binary (`0b`) literals are already valid Java syntax and need no suffix processing.

**Tech Stack:** C#, Roslyn, xUnit

---

### Task 1: Add hex/binary guard to TransformNumericLiteral

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs:69`

- [ ] **Step 1: Insert guard before suffix checks**

In `TransformNumericLiteral()`, insert this block immediately before line 69 (`// Handle suffixes`):

```csharp
        // C# type suffixes (f, d, m, u, l) only apply to decimal literals.
        // For hex/binary literals, letters A-F are valid digits, not suffixes.
        if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return literal;
```

The result should look like:

```csharp
        // Fix 5: Guard digit separators on Java version (Java 7+ required)
        string literal = ((int)context.Options.TargetJavaVersion < 7 && text.Contains('_'))
            ? text.Replace("_", "")
            : text;

        // C# type suffixes (f, d, m, u, l) only apply to decimal literals.
        // For hex/binary literals, letters A-F are valid digits, not suffixes.
        if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return literal;

        // Handle suffixes
        if (literal.EndsWith("f") || literal.EndsWith("F"))
        ...
```

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs
git commit -m "fix: skip type suffix checks for hex and binary numeric literals

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 2: Add test cases for hex/binary literal preservation

**Files:**
- Modify: `tests/CSharpToJava.Tests/DoubleLiteralSuffixTests.cs`

- [ ] **Step 1: Add test method**

Add this test method inside the `DoubleLiteralSuffixTests` class (before the `Convert` helper):

```csharp
    [Fact]
    public void HexBinaryLiterals_NotAffectedBySuffixProcessing()
    {
        var source = @"
class Sample {
    void M() {
        int a = 0xFFFD;
        int b = 0xFF;
        int c = 0xABCDEF;
        int d = 0b1010;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("0xFFFD", result.GeneratedCode);
        Assert.Contains("0xFF", result.GeneratedCode);
        Assert.Contains("0xABCDEF", result.GeneratedCode);
        Assert.Contains("0b1010", result.GeneratedCode);
        Assert.DoesNotContain("0xFFF.0", result.GeneratedCode);
        Assert.DoesNotContain("0xFFf", result.GeneratedCode);
    }
```

- [ ] **Step 2: Run the new test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~HexBinaryLiterals_NotAffectedBySuffixProcessing"`
Expected: PASS

- [ ] **Step 3: Run all DoubleLiteralSuffixTests to verify no regressions**

Run: `dotnet test --filter "FullyQualifiedName~DoubleLiteralSuffixTests"`
Expected: All 3 tests PASS

- [ ] **Step 4: Commit**

```bash
git add tests/CSharpToJava.Tests/DoubleLiteralSuffixTests.cs
git commit -m "test: add hex/binary literal preservation test cases

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```
