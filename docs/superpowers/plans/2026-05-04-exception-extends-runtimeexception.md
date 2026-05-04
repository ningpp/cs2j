# Map C# Exception Classes to Java RuntimeException — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Map `System.Exception` to `java.lang.RuntimeException` instead of `java.lang.Exception`, so user-defined C# exception classes get `extends RuntimeException` (unchecked) in Java.

**Architecture:** Three targeted changes: (1) update the type mapping config to remap System.Exception, (2) remove now-redundant special-case code in ObjectCreationTransformer, (3) add a safety-net mapping in ExceptionApiRewriter for semantic-model fallback paths.

**Tech Stack:** C#, JSON config

---

### Task 1: Update TypeMappings.json

**Files:**
- Modify: `config/TypeMappings.json:445-449`

- [ ] **Step 1: Change System.Exception mapping**

```diff
 {
     "csharp":  "System.Exception",
-    "java":  "Exception",
+    "java":  "RuntimeException",
     "imports":  [
-                    "java.lang.Exception"
+                    "java.lang.RuntimeException"
                 ]
 },
```

- [ ] **Step 2: Verify the change with git diff**

```bash
git diff config/TypeMappings.json
```

Expected: the four lines above show the change.

- [ ] **Step 3: Commit**

```bash
git add config/TypeMappings.json
git commit -m "fix: map System.Exception to RuntimeException instead of Exception"
```

---

### Task 2: Clean up ObjectCreationTransformer redundant code

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs:118,278-288`

After the TypeMappings change, `MapType(System.Exception)` returns `"RuntimeException"`, so the `typeName` variable in ObjectCreationTransformer will never be `"Exception"` (when semantic model works). The hardcoded rewrite at lines 278-288 and the `NeedsSpecialCreationHandling` check at line 118 become dead code.

- [ ] **Step 1: Remove NeedsSpecialCreationHandling Exception/ApplicationException check**

Remove line 118:
```diff
-        if (typeName is "Exception" or "ApplicationException") return true;
```

The file is at `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs:118`.

- [ ] **Step 2: Remove TransformObjectCreationWithArgs Exception/ApplicationException rewrite**

Remove lines 278-288 (the entire block plus its comment):
```diff
-        // Exception / ApplicationException → RuntimeException (unchecked in Java)
-        if (typeName is "Exception" or "ApplicationException")
-        {
-            if (argumentList == null || argumentList.Arguments.Count == 0)
-                return "new RuntimeException()";
-            var exArgs = ArgumentTransformer.TransformArgumentList(
-                argumentList, context, ExpressionTransformerFacade.Instance);
-            return string.IsNullOrWhiteSpace(exArgs)
-                ? "new RuntimeException()"
-                : $"new RuntimeException({exArgs})";
-        }
```

The file is at `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs:278-288`.

- [ ] **Step 3: Build to verify no compilation errors**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs
git commit -m "refactor: remove redundant Exception/ApplicationException rewrite in ObjectCreationTransformer"
```

---

### Task 3: Add Exception → RuntimeException safety net in ExceptionApiRewriter

**Files:**
- Modify: `src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs:34`

When the semantic model fails to resolve `Exception` → `System.Exception` (e.g., incomplete code), the type mapper falls back to syntax-only resolution which returns `"Exception"` unchanged. The `ExceptionApiRewriter` runs as an IR-level safety net — add `"Exception"` to its type map so these edge cases still produce `RuntimeException`.

- [ ] **Step 1: Add Exception to ExceptionTypeMap**

Add after line 34 (`["ApplicationException"] = "RuntimeException",`):
```diff
     private static readonly Dictionary<string, string> ExceptionTypeMap = new(StringComparer.Ordinal)
     {
+        ["Exception"] = "RuntimeException",
         ["ApplicationException"] = "RuntimeException",
```

File: `src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs:35` (new line).

- [ ] **Step 2: Build to verify no compilation errors**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java/Rewriters/ExceptionApiRewriter.cs
git commit -m "fix: add Exception→RuntimeException fallback in ExceptionApiRewriter"
```

---

### Task 4: Run tests

**Files:**
- Test: `tests/CSharpToJava.Tests/D4IrRewriterTests.cs`

- [ ] **Step 1: Run full test suite**

```bash
dotnet test
```

Expected: All tests pass. Pay special attention to:
- `ExceptionApi_NewApplicationException_BecomesRuntimeException` — ApplicationException → RuntimeException in new expressions
- `ExceptionApi_CatchClause_ExceptionTypeReplaced` — catch clause type replacement
- `ExceptionApi_RegularException_Unchanged` — RuntimeException stays untouched

- [ ] **Step 2: Review any test failures**

If any test in the old-style pipeline expects `extends Exception` or `new Exception()` output, update the assertion to expect `extends RuntimeException` / `new RuntimeException()`.

- [ ] **Step 3: Commit any test fixes if needed**

```bash
git add -A
git commit -m "test: update test assertions for Exception→RuntimeException mapping"
```
