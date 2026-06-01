# Fix: Java Reserved Literals (true/false/null) Cause Compilation Errors

## Problem

When C# methods named `True()`, `False()`, or `Null()` are converted to Java, the camelCase conversion produces `true()`, `false()`, `null()` — which are Java reserved literals and cannot be used as identifiers, causing compilation errors.

**Root cause:** `JavaNaming.IsJavaKeyword()` does not include `true`, `false`, `null` as reserved words. The `EscapeJavaKeyword()` function therefore does not escape them, and the generated Java code is invalid.

**Secondary issues:**
1. `MethodTransformer.ConvertOperatorName()` is missing `op_True`/`op_False` mappings (falls through to raw Roslyn name)
2. `UnaryExpressionTransformer.GetOperatorMethodName()` is a standalone duplicate of `OperatorTransformer.OpSymbolToJavaName`, creating a sync hazard
3. `ExpressionTransformerHelpers.IsJavaKeyword()` is a duplicate of `JavaNaming.IsJavaKeyword()` with different content (includes `true`/`false`/`null`), violating single source of truth

## Design

### Change 1: Add `true`/`false`/`null` to `JavaNaming.IsJavaKeyword()`

**File:** `src/CSharpToJava.Core/Context/JavaNaming.cs`

Add `"true" or "false" or "null"` to the switch expression in `IsJavaKeyword()`.

**Effect:** `EscapeJavaKeyword("true")` → `"trueValue"`, `EscapeJavaKeyword("false")` → `"falseValue"`, `EscapeJavaKeyword("null")` → `"nullValue"`. All ~100 call sites of `EscapeJavaKeyword` automatically benefit.

### Change 2: Add `op_True`/`op_False` to `MethodTransformer.ConvertOperatorName()`

**File:** `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`

Add `"op_True" => "isTrue"` and `"op_False" => "isFalse"` to the switch expression. (Note: `op_Equality` maps to `"equals"` here vs `"valueEquals"` in `OpSymbolToJavaName` — this is a known inconsistency but out of scope for this fix as changing it could break existing working code.)

### Change 3: Replace `UnaryExpressionTransformer.GetOperatorMethodName()` duplicate with `OpSymbolToJavaName` lookup

**File:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`

Replace the standalone switch with a lookup against `OperatorTransformer.OpSymbolToJavaName`, matching the pattern used by `BinaryExpressionTransformer`.

### Change 4: Delegate `ExpressionTransformerHelpers.IsJavaKeyword()` to `JavaNaming.IsJavaKeyword()`

**File:** `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`

Replace the standalone implementation with `=> JavaNaming.IsJavaKeyword(word)` to ensure single source of truth.

### Change 5: Unit tests

**File:** `tests/CSharpToJava.Tests/JavaReservedWordTests.cs`

Test cases:

| Test | C# Input | Expected Java |
|------|----------|---------------|
| Method `True()` | `public bool True() => true;` | `public boolean trueValue()` |
| Method `False()` | `public bool False() => false;` | `public boolean falseValue()` |
| Method `Null()` | `public object Null() => null;` | `public Object nullValue()` |
| Field `true`/`false` | `public int true, false;` | `public int trueValue, falseValue;` |
| Parameter `true` | `void Foo(bool true) {}` | `void foo(boolean trueValue)` |
| Local var `true` | `var true = 1;` | `var trueValue = 1;` |
| Operator `true` | `public static bool operator true(Foo f)` | `public static boolean isTrue(Foo f)` |
| Operator `false` | `public static bool operator false(Foo f)` | `public static boolean isFalse(Foo f)` |
| Invocation `True()` | `x.True()` | `x.trueValue()` |
| Regression: keyword `Assert` | `void Assert()` | `void assertValue()` |

## Impact Analysis

- **No breaking changes for existing correct conversions:** The only identifiers affected are those that currently produce invalid Java (compilation errors). Adding `true`/`false`/`null` to the keyword list only changes behavior for identifiers that would already fail to compile.
- **`ExpressionTransformerHelpers.IsJavaKeyword()` behavior change:** Now returns `true` for `"true"`, `"false"`, `"null"` — this is correct behavior, as these are reserved in Java.
- **All ~100 `EscapeJavaKeyword` call sites** automatically benefit from the fix without individual changes.
