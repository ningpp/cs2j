# Foreach Deconstruction Preprocessor Design

## Goal

Add a C#→C# source-level preprocessor `ForeachDeconstruction` that lowers
deconstruction `foreach` statements over KeyValuePair collections into plain
`foreach (var __entry in expr)` + explicit local declarations **before** the
C#→Java conversion pipeline runs. This removes the `ForEachVariableStatementSyntax`
burden from the downstream converter for the KV family.

```csharp
// before
foreach ((int x, int y) in dict) { ... }

// after
foreach (var __entry in dict) {
    int x = __entry.Key;
    int y = __entry.Value;
    ...
}
```

## Hard Constraints

- **Pure C#→C# source rewrite.** Never modify any Java conversion-side code
  (`Transformers/`, type mapping, compat library).
- Output must be **valid, compilable, behavior-equivalent** C#. Final
  validation: `dotnet build -c Release` / `dotnet test -c Release` zero errors.
- **Clean files** (no lowerable deconstruction foreach) must be returned
  **byte-for-byte identical** to the input.

## Execution Order

In `ConvertProject`, `ForeachDeconstruction` is placed **after TupleToStruct**
and **before StripDefault**:

```
LocalFunctionLift → TupleToStruct → ForeachDeconstruction → StripDefault → UnsignedType → GenericPreprocess → RecordReadOnly → Goto → 主转换
```

**Rationale:** `TupleToStruct` already has `VisitForEachVariableStatement`
(`TupleToStructPreprocessor.cs:1312`) that handles `elementType.IsTupleType: true`
deconstruction foreach — rewriting them to `foreach(var temp in ...)` + `temp.ItemN`
declarations. After TupleToStruct runs, the Tuple family of deconstruction foreach
no longer exists. `ForeachDeconstruction` therefore only needs to handle the **KV
family** (and skip the Deconstruct family).

## Architecture

### Core — `src/CSharpToJava.Core/ForeachDeconstruction/`

Five files, following the `GenericPreprocess` / `GotoEliminator` pattern:

| File | Responsibility |
|------|----------------|
| `ForeachDeconstructionOptions.cs` | Options record (`ReportInfo`, `Strict`) |
| `ForeachDeconstructionStatistics.cs` | Statistics class (`FilesScanned`, `FilesTransformed`, `FilesSkippedClean`, `ForeachDeconstructionsLowered`) |
| `ForeachDeconstructionDiagnostics.cs` | `ForeachDeconstructionSeverity` enum (`Info`, `Warning`, `Error`) + `ForeachDeconstructionDiagnostic` record |
| `ForeachDeconstructionRewriter.cs` | `CSharpSyntaxRewriter` subclass holding `SemanticModel`, `Changed` flag, `Diagnostics` list, `FreshName` state; overrides `VisitForEachVariableStatement` |
| `ForeachDeconstructionPreprocessor.cs` | Entry: `Preprocess(string sourceCode, SemanticModel model, ForeachDeconstructionOptions? options) -> ForeachDeconstructionResult` |

Entry flow: `ParseText` → `new Rewriter(model, opts).Visit(root)` → if
`!Changed` return original string; else `Formatter.Format(rewritten, new
AdhocWorkspace()).ToFullString()`.

### CLI — `src/CSharpToJava.CLI/ProjectForeachDeconstructionPreprocessor.cs`

Follows `ProjectUnsignedTypePreprocessor.cs`:

- `ProjectForeachDeconstructionPreprocessRequest` / `ProjectForeachDeconstructionPreprocessResult` records.
- `IntermediateDirectoryName = ".cs2j-foreach-deconstruction-src"`.
- `PreprocessAsync`: reuse `ProjectPreprocessSource.*`; build `CSharpCompilation`
  with all `.cs` + `GetBasicReferences()` (reuse
  `ProjectUnsignedTypePreprocessor.GetBasicReferences()`); for each `.cs` call
  `compilation.GetSemanticModel(tree)` then Core; non-`.cs` direct copy;
  `Changed=false` direct copy (preserve BOM via `FileStartsWithBom`).
- Post-check: verify no residual lowerable KV deconstruction foreach in output
  (or at least `Statistics.ForeachDeconstructionsLowered > 0` iff file was rewritten).

### Program.cs Integration

1. **Standalone verb** `foreach-deconstruction` (file + directory mode), aligned
   with `unsigned-type` verb. Register in `Parser.Default.ParseArguments<...>`.
2. **`ConvertProjectOptions`** new switch:
   ```csharp
   [Option("no-foreach-deconstruction", Default = false,
       HelpText = "Disable the foreach deconstruction preprocessing step")]
   public bool NoForeachDeconstruction { get; set; }
   public bool ForeachDeconstruction => !NoForeachDeconstruction;
   ```
3. **`ConvertProject`** insert step after TupleToStruct, before StripDefault.
   Update `opts.Source` / `FingerprintSourcePath` / `FingerprintInputRoot`.
4. **`GetPreservedDestinationDirectories`**: register
   `ProjectForeachDeconstructionPreprocessor.IntermediateDirectoryName`.
5. **Fingerprint tokens**: add
   `$"foreach-deconstruction={opts.ForeachDeconstruction}"`.

## Core Rewriter — KV Family Lowering Rules

### Target Node

`ForEachVariableStatementSyntax` — Roslyn's syntax for deconstruction foreach.

### Element Type Determination

```csharp
var elementType = model.GetForEachStatementInfo(stmt).ElementType as INamedTypeSymbol;
```

### Family Classification

Given element type `T`:

1. **KV family**: `T.Name == "KeyValuePair" && T.TypeArguments.Length == 2`, or
   `T.AllInterfaces` contains `IEnumerable<KeyValuePair<K,V>>`. Covers
   `Dictionary`, `SortedDictionary`, `ConcurrentDictionary`, `IReadOnlyDictionary`,
   `ImmutableDictionary`, `HashSet<KeyValuePair<,>>`, `ImmutableArray<KeyValuePair<,>>`,
   and any type that enumerates `KeyValuePair<K,V>`.
   → **Lower**: position 0 → `.Key` (type K), position 1 → `.Value` (type V).

2. **Deconstruct family**: type has instance/extension `Deconstruct(out ...)`
   method, is not KV.
   → **Skip**, emit `Info` diagnostic: "Deconstruct-based deconstruction skipped;
   handled by converter."

3. **Tuple family**: `T` is `System.ValueTuple<...>` or `System.Tuple<...>` or
   implements `System.Runtime.CompilerServices.ITuple`.
   → **Skip**, emit `Info` diagnostic: "Tuple-based deconstruction skipped; run
   TupleToStruct first." In `convert-project` mode this case never arises
   (TupleToStruct has already eliminated it). In standalone verb mode
   (`foreach-deconstruction -i file.cs` without prior TupleToStruct), the user
   is advised to run `tuple-to-struct` first.

4. **Other**: not a deconstruction target → skip.

### Variable Node Forms

`stmt.Variable` can be:

| Form | C# syntax | Inner structure |
|------|-----------|-----------------|
| `DeclarationExpressionSyntax` | `var (x, y)` | `Designation` is `ParenthesizedVariableDesignationSyntax` |
| `TupleExpressionSyntax` | `(int x, int y)` or `(var x, var y)` | Each argument's `Expression` carries type annotation |
| `IdentifierNameSyntax` | rare, whole-binding | Treat as single variable |

### Unpack Recursion

`Unpack(designation, accessorRoot, currentType)` generates `<unpack-statements>`:

- **Leaf: single variable** (`SingleVariableDesignationSyntax` or `IdentifierNameSyntax`, identifier ≠ `_`):
  - If the designation carries a type annotation (e.g. `int x`), use that type; otherwise use `var`.
  - Generate: `<declType> <name> = <accessorRoot>.Key;` or `.Value`

- **Leaf: discard `_`**: generate nothing, but consume one position.

- **Composite: parenthesized designation** (`ParenthesizedVariableDesignationSyntax.Variables`):
  - KV → components `[(".Key", K), (".Value", V)]`
  - For each component `(suffix, subType)` with corresponding sub-designation `c`:
    - If `c` is a single variable (≠ `_`): `<declType(c)> <name> = <accessorRoot><suffix>;`
    - If `c` is nested parenthesized: introduce intermediate variable `mid`,
      `var mid = <accessorRoot><suffix>;`, then recurse `Unpack(c, mid, subType)`.
    - Discard: skip.

### Transform Output

```
foreach (var __entry in <expr>) {     // preserve await: await foreach (var __entry in <expr>) { ... }
    <unpack-statements>
    <body>
}
```

### Body Wrapping

- If `stmt.Statement` is `BlockSyntax`:
  `block.WithStatements(unpackStatements.Concat(block.Statements))`.
- If `stmt.Statement` is not a block (e.g. `foreach ((int x,int y) in d) Foo(x,y);`):
  wrap in `Block`, then prepend unpack statements.

### Nested Example

```csharp
// before  (dict : IEnumerable<KeyValuePair<int, (int, int)>>)
// After TupleToStruct, (int,int) is a generated struct with Item1/Item2.
foreach ((int k, (int a, int b)) in dict) { ... }

// after ForeachDeconstruction
foreach (var __entry in dict) {
    int k = __entry.Key;
    var __entry_inner = __entry.Value;
    int a = __entry_inner.Item1;
    int b = __entry_inner.Item2;
    ...
}
```

### Whole-Binding Example

```csharp
// before  (dict : Dictionary<int, Point>)
foreach ((int k, var whole) in dict) { ... }

// after
foreach (var __entry in dict) {
    int k = __entry.Key;
    var whole = __entry.Value;
    ...
}
```

## Naming Rules

- Loop variable name defaults to `__entry`.
- `FreshName(baseName, scopeNode)`: per-method counter + existence check against
  all `IdentifierNameSyntax` and `VariableDeclaratorSyntax` in the enclosing
  method declaration (or nearest `BlockSyntax`).
- On conflict: numeric suffix `__entry1`, `__entry2`.
- Nested intermediate variables: `__entry_inner`, `__entry_inner1`, etc.
- Deconstruction variable names (`x`/`y`) preserved as-is (original body
  references now point to the new local declarations — semantically equivalent).

## Trivia Handling

- Use `.WithTriviaFrom(node)` to preserve original indentation and comments.
- Generated nodes have no trivia by default — acceptable.
- Final `Formatter.Format(rewritten, new AdhocWorkspace())` to normalize.
- `Changed = true` only when at least one foreach was lowered.

## Clean File Byte Fidelity

- Cheap pre-check: scan for any `ForEachVariableStatementSyntax`; if none, return
  original string immediately.
- If `ForEachVariableStatementSyntax` exists but all are unlowerable (e.g. all
  Deconstruct family): `Changed = false`, return original `sourceCode` string.
- `Changed = true` → `Formatter.Format` + `ToFullString()`.
- CLI layer preserves BOM: `FileStartsWithBom` check; clean files copied
  byte-for-byte; rewritten files written with matching encoding.

## Downstream Compatibility

- Lowered foreach becomes plain `foreach (var __entry in expr)` + explicit locals.
  `TransformForEachStatement` (`StatementTransformer.Loops.cs:219`) already
  handles `Dictionary`/`KeyValuePair` correctly (→ `CSharpKeyValuePair`).
- Deconstruct family left untouched; downstream `TransformForEachVariableStatement`
  handles it via Vavr tuple.
- No Java conversion-side code modified.

## ref/ref readonly Deconstruction foreach (Known Boundary)

- Rare; not specially handled. Emit `Info` diagnostic and skip.
- Test explicitly verifies such constructs are not incorrectly rewritten.

## Testing

### Unit Tests — `tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs`

Each test constructs `CSharpCompilation` + `SemanticModel` (like `Program.cs`
`UnsignedType` single-file mode), asserts `Changed` and output fragments.

| # | Scenario | Assertion |
|---|----------|-----------|
| 1 | Basic KV: `foreach ((int x, int y) in dict)` (`Dictionary<K,V>`) | `Changed=true`, contains `__entry.Key`/`__entry.Value` |
| 2 | var form: `foreach (var (x, y) in dict)` | Same, declarations use `var` |
| 3 | Tuple-expr var: `foreach ((var x, var y) in dict)` | Same |
| 4 | Inferred: `foreach ((x, y) in dict)` | Declarations use `var` |
| 5 | discard: `foreach ((int x, _) in dict)` | Only `x = __entry.Key` |
| 6 | Whole-binding: `foreach ((int k, var whole) in dict)` | `whole = __entry.Value` |
| 7 | `await foreach ((x, y) in asyncStream)` | `await` preserved |
| 8 | Deconstruct family: `foreach (var (x, y) in points)` | `Changed=false`, Info diagnostic |
| 9 | Non-deconstruction: `foreach (var item in list)` | `Changed=false` |
| 10 | No deconstruction in file | Output == input (byte identical) |
| 11 | Body is single statement (non-block) | Wrapped to block, unpack prepended |
| 12 | `__entry` already in scope | Unique suffix generated |
| 13 | Nested deconstruction foreach in body | Independent `__entry` names |
| 14 | `IEnumerable<KeyValuePair<K,V>>` source | KV lowering |
| 15 | `IReadOnlyDictionary` / `SortedDictionary` / `ConcurrentDictionary` | KV lowering |
| 16 | ref/ref readonly deconstruction foreach | Not rewritten, Info diagnostic |

### Integration Tests — `tests/CSharpToJava.Tests/ProjectForeachDeconstructionPreprocessorTests.cs`

Follows `ReadOnlyStructPreprocessorVerifyTests`:
- `IsDotNetAvailable` guard.
- `CreateTempDirectory` fixture solution.
- `PreprocessAsync` → `dotnet build -c Release` / `dotnet test -c Release`.
- Verify clean file byte fidelity.
- Verify intermediate directory structure.

### D:\csharp-sdk Validation

- Standalone verb `foreach-deconstruction` on `D:\csharp-sdk`.
- Intermediate directory `.cs2j-foreach-deconstruction-src`.
- `dotnet build -c Release` zero errors, `dotnet test -c Release` all pass.
- Save logs.

## Validation Loop (Repeat Until Green)

1. **Red**: write `ForeachDeconstructionTests` asserting basic KV pattern lowered;
   run, confirm failure.
2. **Green**: implement Core rewriter; unit tests pass; `dotnet test -c Release`
   no regression.
3. **SDK level**: standalone verb on `D:\csharp-sdk`; verify structure and
   only `.cs` files changed.
4. **Product compilation**: intermediate dir `dotnet build -c Release` /
   `dotnet test -c Release` zero errors.
5. **Combined order**: run TupleToStruct → ForeachDeconstruction on fixture;
   confirm product still compiles.
6. **Commit**.

## Acceptance Criteria

1. `ForeachDeconstructionTests` and `ProjectForeachDeconstructionPreprocessorTests` all pass.
2. `D:\csharp-sdk` intermediate product: `dotnet build -c Release` zero errors,
   `dotnet test -c Release` all pass.
3. `dotnet test -c Release` (entire cs2j repo) no regression.
4. All test scenarios covered (KV / Deconstruct skip / discard / await / naming
   uniqueness / clean fidelity / non-deconstruction unchanged).
5. `foreach-deconstruction` standalone verb available; integrated into
   `convert-project` after TupleToStruct.
6. No Java conversion-side code modified.
7. Code style consistent with `GotoEliminator` / `GenericPreprocess` (Options /
   Statistics / Diagnostics / Rewriter split files, `Formatter.Format`, clean
   byte fidelity).

## Reference Files

| File | Role |
|------|------|
| `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs` | **Main template**: project wrapper with SemanticModel, `CSharpCompilation`, `GetBasicReferences`, `FileStartsWithBom` |
| `src/CSharpToJava.CLI/ProjectGotoPreprocessor.cs` | Project wrapper pattern, post-check `ContainsGotoOrLabel` |
| `src/CSharpToJava.CLI/ProjectPreprocessSource.cs` | Shared source tree preparation (must reuse) |
| `src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs` | SemanticModel-accepting entry pattern |
| `src/CSharpToJava.Core/GotoEliminator/GotoEliminator.cs` | Pure syntax rewrite entry + `IsDirty` + `Formatter.Format` |
| `src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs:1312` | Existing Tuple deconstruction foreach handler (eliminates Tuple family before this preprocessor) |
| `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs:569-672` | Existing KV/Deconstruct foreach converter (downstream) |
| `src/CSharpToJava.CLI/Program.cs` | Verb registration, `ConvertProjectOptions` `No*` switches, `ConvertProject` order, `GetPreservedDestinationDirectories` / fingerprint tokens |
