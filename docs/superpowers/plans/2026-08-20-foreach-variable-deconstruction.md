# Foreach Variable Deconstruction Conversion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert C# deconstruction foreach variables into valid Java tuple loops.

**Architecture:** Extend `StatementTransformer` with a dedicated `ForEachVariableStatementSyntax` path. The path maps the semantic tuple element type, emits an enhanced-for loop, and prepends tuple accessor declarations generated recursively from the designation tree. Existing normal foreach conversion and loop-body transformation remain shared.

**Tech Stack:** .NET/C#, Roslyn syntax and semantic APIs, xUnit, existing `ConversionPipeline`.

---

### Task 1: Add regression tests

**Files:**
- Modify: `tests/CSharpToJava.ConversionTests/Categories/ControlFlowTests.cs`

- [ ] **Step 1: Add failing tests** for named tuple deconstruction, typed tuple deconstruction, and nested designation. Assert success, no `TODO: ForEachVariableStatement`, `Tuple2` mapping, `_1()`/`_2()` accessors, and body references.
- [ ] **Step 2: Run the focused tests** with `dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --filter FullyQualifiedName~ControlFlowTests` and verify the new tests fail on the TODO output.

### Task 2: Implement deconstruction foreach lowering

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs`
- Modify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs`

- [ ] **Step 1: Dispatch `SyntaxKind.ForEachVariableStatement`** to a new transformer method.
- [ ] **Step 2: Implement semantic tuple element type mapping and collection transformation.** Use the foreach statement info/element type when available, falling back to the expression type; map tuple types through `ConversionContext.MapType` so imports are registered.
- [ ] **Step 3: Generate recursive designation declarations.** For each single variable, emit a mapped Java local declaration initialized from the tuple loop variable's `_N()` accessor; nested parenthesized designations recurse against the selected tuple expression.
- [ ] **Step 4: Reuse existing body/stream handling and preserve loop labels.** Ensure the generated body starts with declarations and then passes through `TransformBlock`/`Transform` so normal statements and captures continue to work.

### Task 3: Verify and document

**Files:**
- Review: `README.md`

- [ ] **Step 1: Run focused conversion tests.**
- [ ] **Step 2: Run the full solution test suite with `dotnet test CSharpToJavaConverter.slnx` and inspect failures for regressions.
- [ ] **Step 3: If README contains the reproducer, update its expected output; otherwise leave it unchanged and report that no README edit was needed.
