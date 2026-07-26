# Implementation Plan: ReadOnly Struct Maker

Date: 2026-07-26
Spec: `docs/superpowers/specs/2026-07-26-readonly-struct-maker-design.md`

## Goal & Scope

Add a Roslyn-based transform that converts mutable C# `struct` types into `readonly struct`
types (immutability prep for Java translation), exposed both:
- as a new CLI verb `make-readonly` (single file or project directory), and
- as an optional phase in `convert-project` (`--no-make-readonly` to disable, on by default).

Hard invariant (SafeRewrite): **the produced C# must still compile** — every instance field is
`readonly` and assigned exactly once before every constructor returns.

Non-goals for v1 (documented in diagnostics, no code change):
- `record struct` / `readonly record struct` (already effectively immutable; skipped).
- Manual mutating instance methods are left untouched — a struct with such a method is **not**
  transformed (see Safety below).

> **Note on Strategy Divergence**: This plan uses an **active rewrite** approach (adds `readonly`
> to fields, changes `set` to `init`, injects `field = default;` in constructors) rather than the
> spec's conservative filter approach. This divergence is intentional — the active approach transforms
> more structs while maintaining the SafeRewrite invariant. The spec will be updated to reflect this.
>
> **Spec items intentionally dropped in this plan (v1):**
> - `CallGraphBuilder.cs` — the spec builds a full method call graph to detect indirect field
>   mutation. This plan replaces it with a simpler static check: "struct has no instance
>   `MethodDeclarationSyntax` lacking `readonly`" + "`ref this`/`out this` escape scan." The
>   `CallGraphBuilder.cs` file is NOT created.
> - `[DoNotMakeReadOnly]` / `OptOutAttributeName` opt-out attribute — not implemented in v1.
>   `SkipTypes` (name-based) covers the same use case at the CLI level. The spec's
>   `ReadOnlyStructMakerOptions.OptOutAttributeName` and `ReportSkipped` fields are replaced by
>   `Verbose`/`ContinueOnError`/`SkipTypes`/`ExtraValueTypes`.
> - Spec's `MakeReadOnly(SyntaxTree, SemanticModel, ...)` single-tree overload is replaced by
>   `MakeReadOnlyAsync(string source, ...)` which builds an ad-hoc compilation internally.

## Investigation Summary (existing patterns to mirror)

- **`src/CSharpToJava.Core/GotoEliminator/`** — the closest existing analog. It is a pure
  syntax transformer with `GotoEliminator.cs` (entry + `Result`/`Statistics`),
  `GotoEliminatorOptions.cs`, `GotoEliminatorDiagnostics.cs`
  (`GotoEliminatorDiagnostic`/`GotoEliminatorSeverity`). The new module mirrors this shape.
- **`src/CSharpToJava.CLI/ProjectGotoPreprocessor.cs`** — copies a project to
  `<dest>/.cs2j-no-goto-src`, restores, and rewrites in place. The new
  `ProjectReadOnlyStructPreprocessor` mirrors it but needs a **compilation** (SemanticModel),
  so it uses `SolutionLoader` from `src/CSharpToJava.Core/Workspace/SolutionLoader.cs`.
- **`src/CSharpToJava.CLI/Program.cs`** — `Parser.Default.ParseArguments<...>()` +
  `result.MapResult(...)`; `ConvertProjectOptions` carries `NoEliminateGoto`/`EliminateGoto`;
  `ConvertProject` runs the goto block and rewires `opts.Source`; `GetPreservedDestinationDirectories`
  returns `null` or a set of intermediate dir names that must be preserved during `--force` cleanup.
  Integration points are well defined there.
- Tests: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs` (xUnit, `CSharpSyntaxTree.ParseText`,
  `CSharpSyntaxTree.ParseText(...).GetRoot()`) and `ProjectGotoPreprocessorTests.cs` (temp dirs,
  tests `ResolveRestoreTarget` layout resolution — does NOT exercise `SolutionLoader`). Build/test
  via `dotnet test` (CLAUDE.md).

Key finding — `readonly struct` field-initializer rule (verified against Microsoft Learn): a
`readonly struct` **may** have instance field initializers / property initializers; the compiler
emits the initializer inside each non-delegating constructor. This changes the rewriter design
(see Field Initializers below) — we keep initializers and only add `field = default;` for fields
that are *not* covered by an initializer or a constructor assignment.

## Files To Create

### 1. `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs`
Mirror `GotoEliminatorOptions` shape but extend for this module's needs:
- `bool Strict` — treat unconvertible structs as fatal (CLI `--strict`)
- `bool Verbose` — show detailed progress
- `bool ContinueOnError` — continue processing on single-struct failures
- `string? RootNamespace` — default namespace for single-file mode
- `string? SkipTypes` — semicolon-separated list of type names to skip. Supports:
  - `Name` (simple name, no namespace)
  - `Namespace.Type` (fully qualified)
  - `Namespace.*` (glob, matches all types in that namespace)
- `string? ExtraValueTypes` — semicolon-separated list of metadata names to additionally treat as
  "known struct types" (extends the hardcoded `KnownStructTypes` set)
- Helper: `ReadOnlyStructSkipSet ParseSkipSet()` — parses `SkipTypes` for efficient matching.
- Helper: `bool IsKnownStructType(ITypeSymbol type, ImmutableHashSet<string> knownStructTypes)` —
  checks whether a field's type is a trigger (struct kind / in `KnownStructTypes` / in
  `ExtraValueTypes`). This is distinct from `SkipTypes` filtering (which decides whether a
  **struct being analyzed** should be skipped), so the two concerns are kept in separate methods.

```csharp
public sealed class ReadOnlyStructMakerOptions
{
    public bool Strict { get; init; }
    public bool Verbose { get; init; }
    public bool ContinueOnError { get; init; }
    public string? RootNamespace { get; init; }
    public string? SkipTypes { get; init; }
    public string? ExtraValueTypes { get; init; }

    /// <summary>Parses SkipTypes into a set of (kind, value) for matching.</summary>
    public ReadOnlyStructSkipSet ParseSkipSet() { /* ... */ }

    /// <summary>Determines whether a field's type is a trigger (struct kind or known struct).</summary>
    public bool IsKnownStructType(ITypeSymbol type, ImmutableHashSet<string> knownStructTypes) { /* ... */ }
}

/// <summary>Parsed representation of SkipTypes for efficient matching.</summary>
public sealed class ReadOnlyStructSkipSet
{
    public bool SkipBySimpleName { get; }
    public bool SkipByFullName { get; }
    public bool SkipByNamespaceGlob { get; }
    // Internal: HashSet<string> _simpleNames, _fullNames, _namespaces
    public bool Matches(ISymbol type);
}
```

### 2. `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs`
Mirror `GotoEliminatorDiagnostics`:
- `enum ReadOnlyStructSeverity { Info, Warning, Error }`
- `record ReadOnlyStructMakerDiagnostic(ReadOnlyStructSeverity Severity, string StructName, string Reason, string? FilePath = null, int? Line = null)`
- `class ReadOnlyStructMakerStatistics` — tracks both file-level and struct-level counts:
  ```csharp
  public sealed class ReadOnlyStructMakerStatistics
  {
      public int FilesScanned;
      public int FilesTransformed;
      public int StructsScanned;
      public int StructsConverted;
      public int StructsSkipped;      // already readonly / ref / partial
      public int StructsFailed;       // safety check failed (non-readonly mutating method)
      public int FieldsMadeReadonly;
      public int PropertiesMadeInitOnly;
      public int ConstructorsUpdated;
  }
  ```
- `record ReadOnlyStructMakerResult(string? OutputCode, bool Changed, IReadOnlyDictionary<string, string> ChangedFiles, IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics, ReadOnlyStructMakerStatistics Statistics)`

### 3. `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`
Pure analysis of one `StructDeclarationSyntax` given a `SemanticModel`. Returns `StructAnalysis`:
```csharp
record FieldInfo(string Name, bool HasInitializer, bool IsReadonly, bool IsStatic);
record CtorInfo(ConstructorDeclarationSyntax Node, bool DelegatesThis, IMethodSymbol? Target);
class StructAnalysis {
    bool IsCandidate;          // has >=1 trigger field AND safe
    bool AlreadyReadonly;
    bool IsRefStruct;
    bool IsPartialStruct;
    bool IsRecordStruct;
    List<FieldInfo> InstanceFields;
    List<CtorInfo> Ctors;
    HashSet<string> FieldsWithInitializer;     // instance fields that have an initializer
    Dictionary<IMethodSymbol, HashSet<string>> EffectiveAssigned; // ctor symbol -> field names assigned
}
```
Candidate rule:
- Already `readonly` → `IsCandidate=false` (Info: already readonly struct).
- Is `ref struct` → `IsCandidate=false` (Info: ref struct skipped).
- Is `partial struct` → `IsCandidate=false` (Info: partial struct skipped).
- Is `record struct` → `IsCandidate=false` (Info: record struct skipped).
- A field is a **trigger** if its resolved type (`model.GetTypeInfo(fieldType).Type`) is
  `TypeKind.Struct` **and not a primitive special type** (excludes `int`/`double`/`bool`/etc.) and
  not an enum — OR the type's metadata name is in `KnownStructTypes` set
  (`System.DateTime`, `System.TimeSpan`, `System.Guid`, `System.Decimal`, `System.Numerics.BigInteger`,
  `System.Numerics.Complex`, `System.Drawing.Point/PointF/Size/SizeF/Rectangle/RectangleF`,
  `UnityEngine.Vector3/Vector2/Vector4/Quaternion`, etc.) combined with `ExtraValueTypes`.
- `IsCandidate = contains >=1 trigger field`.
- **Safety (keeps compilation valid):** also require the struct has **no instance non-constructor
  method lacking a `readonly` modifier** (a non-`readonly` method may mutate; making the whole
  struct `readonly` would break it). If such a method exists, `IsCandidate=false` and record
  `StructsFailed` with a Warning diagnostic.  
  **Scope:** this check iterates over `MethodDeclarationSyntax` nodes only — it does **not**
  examine property accessors (`AccessorDeclarationSyntax`) or operator overloads, because
  auto-property getters are not explicitly `readonly` in source and would otherwise cause every
  struct with any auto-property to be rejected. Property `set` accessors are handled separately
  by the rewriter (changed to `init`).
- **Escape analysis:** check for `ref this` / `out this` passing in any instance method. If found,
  `IsCandidate=false` (Warning: this-escape makes safety guarantee impossible).

Effective-assigned computation (fixpoint over the `: this(...)` delegation graph):
```
effectiveAssigned(ctor) =
    bodyAssignedFields(ctor)                                  // 'x = ...' or 'this.x = ...' in body
    ∪ (ctor.DelegatesThis ? effectiveAssigned(ctor.Target) : FieldsWithInitializer)
```
Resolve `ctor.Target` via `model.GetSymbolInfo(ctor.Initializer)`. Memoize; cycles are impossible
(a `: this(...)` chain must terminate at a non-delegating ctor).

### 4. `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs`
`CSharpSyntaxRewriter` (overrides `VisitStructDeclaration`). Holds `SemanticModel` + options.
For a struct: run `StructAnalyzer`; if not candidate → return node unchanged.
Otherwise rewrite (trivia-preserving):
- **Struct keyword:** add `readonly` token immediately after `struct` keyword (keep surrounding
  whitespace/`ElasticMarker`). Skip if already present.
  - If modifiers exist: `node.AddModifiers(readonlyToken.WithTrailingTrivia(SyntaxFactory.Space))`
  - If no modifiers: `readonlyToken` takes the keyword's leading trivia; keyword trivia cleared.
- **Instance fields** (non-static, non-const): if not already `readonly`, prepend `readonly`
  modifier (keep `volatile`/`required` as-is). Keep field initializers untouched.
- **Instance properties** with a `set` accessor that is neither `readonly` nor `init`:
  change `set` → `init` (covers auto-properties per spec; also manual `set` — safe because init
  setters may assign readonly fields; required for the struct to compile). Record count.
- **Constructors:** preserve existing visibility (do NOT force `public` — changing visibility
  would break API contracts and can cause compile errors when callers rely on internal/private
  constructors). For each constructor, compute `defaultFields =
  InstanceFields.Where(f => !f.HasInitializer && !effectiveAssigned(ctor).Contains(f.Name))`.
  For each such field, prepend `f = default;` as the **first statement** of the constructor body.
  - Block-bodied ctor: prepend to `Block.Statements`.
  - Expression-bodied ctor (`C() => x = 1;`): convert to block body
    `{ /*defaults*/ x = 1; }` — defaults go first so user assignments take precedence.
  - `: this(...)` ctor with empty body `{}`: insert the defaults inside the block.
  Record `ConstructorsUpdated` when any default statement is added.
- `record struct` / `readonly struct` / `ref struct`: return unchanged (skip).

Field-initializer nuance: because `readonly struct` allows initializers and they run in
non-delegating ctors, we **never** emit `field = default;` for a field that has an initializer
(it is already assigned exactly once by the compiler-generated init for that ctor). For a
delegating ctor, the initializer runs in the target, so again no `default` is added. This keeps
the exactly-once invariant and compiles.

### 5. `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs`
Orchestrator with two entry points returning `ReadOnlyStructMakerResult`:
```csharp
public sealed class ReadOnlyStructMaker
{
    /// <summary>Project-level entry: analyze all structs in the compilation and transform.</summary>
    public async Task<ReadOnlyStructMakerResult> MakeReadOnlyAsync(
        CSharpCompilation compilation,
        ReadOnlyStructMakerOptions options,
        CancellationToken ct = default);

    /// <summary>Single-file entry: build an ad-hoc compilation with TPA references.</summary>
    public async Task<ReadOnlyStructMakerResult> MakeReadOnlyAsync(
        string source,
        ReadOnlyStructMakerOptions options,
        CancellationToken ct = default);
}
```

- `MakeReadOnlyAsync(CSharpCompilation, ...)`:
  for each `tree` in `compilation.SyntaxTrees`, build `model = compilation.GetSemanticModel(tree)`,
  `root = (CompilationUnitSyntax)new ReadOnlyStructRewriter(model, options).Visit(tree.GetRoot())`.
  Collect `(tree.FilePath, root.ToFullString(), changed)` into `ChangedFiles`. Aggregate diagnostics/statistics.
- `MakeReadOnlyAsync(string, ...)`:
  build a throwaway `CSharpCompilation` from `source` with references gathered from
  `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` (filter to existing metadata files), so
  `System.*` trigger types resolve. Run the same rewriter, set `OutputCode`. (User-defined structs
  won't resolve here — full fidelity is the project path; single-file is for quick CLI use/tests.)

A helper `BuildReferences()` (cached) returns the `MetadataReference[]` from TPA.

### 6. `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs`
Mirror `ProjectGotoPreprocessor` shape (request/result objects, not loose parameters):
```csharp
internal sealed class ProjectReadOnlyStructPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public bool Strict { get; init; }
    public string? SkipTypes { get; init; }
    public string? ExtraValueTypes { get; init; }
}

internal sealed class ProjectReadOnlyStructPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required ReadOnlyStructMakerStatistics Statistics { get; init; }
    public required IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectReadOnlyStructPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-readonly-src";

    public static async Task<ProjectReadOnlyStructPreprocessResult> PreprocessAsync(
        ProjectReadOnlyStructPreprocessRequest request);
}
```

> Note: `PreprocessAsync` intentionally takes **no `CancellationToken`** — this mirrors
> `ProjectGotoPreprocessor.PreprocessAsync` exactly, and `ConvertProject` has no `ct` parameter
> to pass through. If cancellation support is needed later, both preprocessors and
> `ConvertProject` should be updated together.

Implementation:
- Copy `request.SourcePath` files to `<destinationRoot>/.cs2j-readonly-src` (preserve layout; mirror the
  inline copy loop in `ProjectGotoPreprocessor.PreprocessAsync` — there is no extracted
  `CopyProjectFilesAsync` helper to call).
- Resolve the restore target via the same `ResolveRestoreTarget` pattern as
  `ProjectGotoPreprocessor` (finds the `.sln`/`.csproj` in the intermediate dir, not the raw
  directory), then `RunDotNetRestore(restoreTarget)` (idempotent; the goto pass will restore again
  if needed). If restore fails, add Error diagnostic and return with `Success = false`.
- `SolutionLoader.EnsureMSBuildRegistered()`; load via `SolutionLoader.TryOpenAsync` against the
  **copied** project to get a resolved `CSharpCompilation`.
  If loading fails, add Error diagnostic and return with `Success = false`.
- `await ReadOnlyStructMaker.MakeReadOnlyAsync(compilation, options)`.
- Write each transformed tree back to its `FilePath` in the readonly dir (only when `Changed`).
- Return the readonly dir path.
- Constructor `: this(...)` chains and `record` types are handled by the rewriter/analyzer; the
  preprocessor just writes results.

## Files To Edit

### 7. `src/CSharpToJava.CLI/Program.cs`

#### 7a. ParseArguments — add 5th type parameter and MapResult
```csharp
// BEFORE:
return await Parser.Default.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions>(args)
    .MapResult(
        (ConvertOptions opts) => ConvertFile(opts),
        (ConvertProjectOptions opts) => ConvertProject(opts),
        (AnalyzeOptions opts) => AnalyzeProject(opts),
        (EliminateGotoOptions opts) => EliminateGoto(opts),
        errs => Task.FromResult(1)
    );

// AFTER:
return await Parser.Default.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions>(args)
    .MapResult(
        (ConvertOptions opts) => ConvertFile(opts),
        (ConvertProjectOptions opts) => ConvertProject(opts),
        (AnalyzeOptions opts) => AnalyzeProject(opts),
        (EliminateGotoOptions opts) => EliminateGoto(opts),
        (MakeReadOnlyOptions opts) => HandleMakeReadOnly(opts),
        errs => Task.FromResult(1)
    );
```

#### 7b. MakeReadOnlyOptions class (at end of file, alongside other option classes)
```csharp
[Verb("make-readonly", HelpText = "Convert eligible structs to readonly structs")]
class MakeReadOnlyOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unconvertible structs as fatal")]
    public bool Strict { get; set; }
    [Option("skip-types", Required = false, HelpText = "Semicolon-separated list of type names to skip")]
    public string? SkipTypes { get; set; }
    [Option("extra-value-types", Required = false, HelpText = "Semicolon-separated list of metadata names to treat as struct types")]
    public string? ExtraValueTypes { get; set; }
}
```

#### 7c. ConvertProjectOptions — add new options
```csharp
[Option("no-make-readonly", Default = false, HelpText = "Disable the default readonly-struct preprocessing step")]
public bool NoMakeReadOnly { get; set; }

[Option("strict", Default = false, HelpText = "Treat unconvertible structs as fatal during readonly preprocessing")]
public bool Strict { get; set; }

[Option("skip-types", Required = false, HelpText = "Semicolon-separated list of type names to skip during readonly preprocessing")]
public string? SkipTypes { get; set; }

[Option("extra-value-types", Required = false, HelpText = "Semicolon-separated list of metadata names to treat as struct types during readonly preprocessing")]
public string? ExtraValueTypes { get; set; }

public bool MakeReadOnly => !NoMakeReadOnly;

// Tracks whether the readonly preprocessing produced an intermediate source directory
// that must be preserved during --force cleanup.
internal bool UsingReadOnlyPreprocessedSource { get; set; }
```

> Note: `Strict`, `SkipTypes`, and `ExtraValueTypes` are added to `ConvertProjectOptions` (not
> just `MakeReadOnlyOptions`) because the `convert-project` readonly block (7d) and fingerprint
> tokens (7e) reference `opts.Strict`, `opts.SkipTypes`, and `opts.ExtraValueTypes` on the
> `ConvertProjectOptions` instance.

#### 7d. ConvertProject — add readonly preprocessing block (BEFORE goto block)
```csharp
if (opts.MakeReadOnly)
{
    var originalSource = opts.Source;
    var preprocessResult = await ProjectReadOnlyStructPreprocessor.PreprocessAsync(
        new ProjectReadOnlyStructPreprocessRequest
        {
            SourcePath = originalSource,
            DestinationRoot = opts.Destination,
            Force = opts.Force,
            Verbose = opts.Verbose,
            Strict = opts.Strict,
            SkipTypes = opts.SkipTypes,
            ExtraValueTypes = opts.ExtraValueTypes,
        });

    if (opts.Verbose)
    {
        Console.WriteLine($"Readonly source: {originalSource} -> {preprocessResult.PreprocessedSourcePath}");
    }

    Console.WriteLine(
        $"make-readonly: {preprocessResult.Statistics.StructsConverted} converted, " +
        $"{preprocessResult.Statistics.StructsSkipped} skipped, " +
        $"{preprocessResult.Statistics.StructsFailed} failed");

    foreach (var diagnostic in preprocessResult.Diagnostics)
    {
        Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.StructName}: {diagnostic.Reason}");
    }

    if (!preprocessResult.Success)
    {
        return 1;
    }

    opts.Source = preprocessResult.PreprocessedSourcePath;
    opts.FingerprintSourcePath = originalSource;
    opts.FingerprintInputRoot = preprocessResult.SourceRoot;
    opts.UsingReadOnlyPreprocessedSource = true;
}
```

> **Critical: goto block fingerprint fix.** When both readonly and goto preprocessing run, the
> readonly block rewires `opts.Source` to the `.cs2j-readonly-src` intermediate dir. The existing
> goto block (lines ~149-184 in `Program.cs`) captures `var originalSource = opts.Source` — which
> would now be the readonly dir, not the user's original source. It then sets
> `opts.FingerprintSourcePath = originalSource`, corrupting the fingerprint to point at the
> intermediate dir instead of the real source.
>
> **Fix required in the existing goto block:** change
> ```csharp
> var originalSource = opts.Source;
> ```
> to
> ```csharp
> var originalSource = opts.UsingReadOnlyPreprocessedSource
>     ? opts.FingerprintSourcePath!   // preserve original set by readonly block
>     : opts.Source;
> ```
> and guard the `opts.FingerprintSourcePath = originalSource` assignment so it does not overwrite
> a value already set by the readonly block. Concretely, only set it when
> `!opts.UsingReadOnlyPreprocessedSource`. The same applies to `opts.FingerprintInputRoot` — the
> goto block should only override it when readonly did not already set it, OR it should combine
> correctly (the goto `SourceRoot` is relative to the readonly intermediate, which is acceptable
> for enumeration as long as `FingerprintSourcePath` points at the true original).

#### 7e. GetProjectConversionOptionTokens — add fingerprint token
```csharp
private static IReadOnlyList<string> GetProjectConversionOptionTokens(ConvertProjectOptions opts)
{
    return new List<string>
    {
        // ... existing tokens ...
        $"eliminate-goto={opts.EliminateGoto}",
        $"make-readonly={opts.MakeReadOnly}",
        $"skip-types={opts.SkipTypes ?? "<none>"}",
        $"extra-value-types={opts.ExtraValueTypes ?? "<none>"}",
        $"extra-deps={GetNormalizedExtraDependencyToken(opts)}",
    };
}
```

#### 7f. GetPreservedDestinationDirectories — preserve both intermediate dirs
```csharp
private static IReadOnlySet<string>? GetPreservedDestinationDirectories(ConvertProjectOptions opts)
{
    var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (opts.UsingGotoPreprocessedSource)
        dirs.Add(ProjectGotoPreprocessor.IntermediateDirectoryName);
    if (opts.UsingReadOnlyPreprocessedSource)
        dirs.Add(ProjectReadOnlyStructPreprocessor.IntermediateDirectoryName);
    return dirs.Count > 0 ? dirs : null;
}
```

#### 7g. HandleMakeReadOnly method
```csharp
private static async Task<int> HandleMakeReadOnly(MakeReadOnlyOptions opts)
{
    var maker = new ReadOnlyStructMaker();
    var options = new ReadOnlyStructMakerOptions
    {
        Strict = opts.Strict,
        Verbose = opts.Verbose,
        SkipTypes = opts.SkipTypes,
        ExtraValueTypes = opts.ExtraValueTypes,
    };

    // Single-file mode
    if (opts.Input != null)
    {
        if (!File.Exists(opts.Input))
        {
            Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
            return 1;
        }
        var sourceCode = await File.ReadAllTextAsync(opts.Input);
        var result = await maker.MakeReadOnlyAsync(sourceCode, options);

        foreach (var d in result.Diagnostics)
            Console.Error.WriteLine($"[{d.Severity}] {d.StructName}: {d.Reason}");

        if (opts.Output != null)
        {
            await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
            if (opts.Verbose) Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
        }
        else
        {
            Console.Write(result.OutputCode);
        }
        // Strict mode: Warning AND Error are fatal. Non-strict: only Error is fatal.
        // (The original ternary `== (strict ? Warning : Error)` was a bug — it made Error
        //  non-fatal in strict mode by only matching Warning.)
        var fatalSeverities = opts.Strict
            ? new[] { ReadOnlyStructSeverity.Warning, ReadOnlyStructSeverity.Error }
            : new[] { ReadOnlyStructSeverity.Error };
        return result.Diagnostics.Any(d => fatalSeverities.Contains(d.Severity)) ? 1 : 0;
    }

    // Directory / project mode
    if (opts.Source == null || opts.Destination == null)
    {
        Console.Error.WriteLine("Error: provide (-i/-o) or (-s/-d).");
        return 1;
    }
    if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
    {
        Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
        return 1;
    }
    Directory.CreateDirectory(opts.Destination);

    var preprocessResult = await ProjectReadOnlyStructPreprocessor.PreprocessAsync(
        new ProjectReadOnlyStructPreprocessRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = opts.Destination,
            Force = true,
            Verbose = opts.Verbose,
            Strict = opts.Strict,
            SkipTypes = opts.SkipTypes,
            ExtraValueTypes = opts.ExtraValueTypes,
        });

    foreach (var d in preprocessResult.Diagnostics)
        Console.Error.WriteLine($"[{d.Severity}] {d.StructName}: {d.Reason}");

    Console.WriteLine(
        $"make-readonly: {preprocessResult.Statistics.StructsConverted} converted, " +
        $"{preprocessResult.Statistics.StructsSkipped} skipped, " +
        $"{preprocessResult.Statistics.StructsFailed} failed");

    return preprocessResult.Success ? 0 : 1;
}
```

## Files To Create (Tests)

### 8. `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`
Unit tests (xUnit, mirror `GotoEliminatorTests` style) using a `BuildCompilation(string src)`
helper that creates a `CSharpCompilation` with TPA references. Cover:
- Plain struct with `DateTime` field → becomes `readonly struct`, field gets `readonly`, `int`
  field (primitive) does NOT trigger conversion of the struct but IS made readonly once the struct
  is converted; constructor gets `field = default;` for the unassigned `int`.
- Struct already `readonly` → no change.
- Struct with a non-readonly mutating method → skipped, `StructsFailed` diagnostic, no change.
- Field initializer preserved; `readonly struct` still compiles (parse + `compilation.GetDiagnostics()`
  has no errors).
- `: this(...)` delegation → defaults added only where needed, no double assignment (compile check).
- Auto-property `set` → `init`.
- `SkipTypes` excludes a type; `ExtraValueTypes` adds one.
- Single-file `MakeReadOnlyAsync` happy path returns changed `OutputCode`.
- `ref struct` → skipped (Info).
- `partial struct` → skipped (Info).
- `record struct` → skipped (Info).
- `ref this` escape → skipped (Warning).

### 9. `tests/CSharpToJava.Tests/ProjectReadOnlyStructPreprocessorTests.cs`
Integration test: create a temp directory with a small `.csproj` + a `.cs` file containing a
mutable struct holding `DateTime`, call `ProjectReadOnlyStructPreprocessor.PreprocessAsync`,
assert the produced file in `.cs2j-readonly-src` parses and that loading it via `SolutionLoader`
yields a compilation with no errors, and that the struct is `readonly`.
(Note: the existing `ProjectGotoPreprocessorTests` only tests `ResolveRestoreTarget` layout
resolution without `SolutionLoader`; this test goes further by exercising the full preprocessor
including MSBuild loading.)

## Verification Commands

```bash
# Build
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj

# Single-file CLI smoke test
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- make-readonly -i sample.cs --verbose

# Project directory
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- make-readonly -s ./src/SomeProject -d ./out

# convert-project with the phase on (default) and off
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s ./src -d ./out
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s ./src -d ./out --no-make-readonly

# Tests
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMaker"
dotnet test --filter "FullyQualifiedName~GotoEliminator"   # ensure no regression
dotnet test                                                # full suite
```

## Risks / Notes
- **Mutating-method skip** trades coverage for guaranteed compilation — acceptable per SafeRewrite.
- **`readonly struct` + `record struct`**: skipped in v1; revisit if target projects use them.
- **Field-initializer + `this(...)` interaction** is handled via the effective-assigned fixpoint;
  the compile-check tests are the safety net.
- Single-file mode has weaker type resolution than the project path; promote the project path for
  real conversions.
- **Strategy divergence from spec**: This plan uses active rewrite (adds readonly/set→init/defaults)
  rather than the spec's conservative filter. The spec should be updated to match. Dropped spec
  items: `CallGraphBuilder.cs`, `[DoNotMakeReadOnly]` attribute, `OptOutAttributeName`/
  `ReportSkipped` options (see Strategy Divergence note above).
- **`SkipTypes` glob matching**: No existing codebase precedent; implement simple matching
  (simple name, full name, namespace.* glob) in `ReadOnlyStructSkipSet`.
- **Two-pass fingerprint chain**: when both readonly and goto preprocessing run, the existing goto
  block must be patched to preserve `opts.FingerprintSourcePath` set by the readonly block (see
  7d). Without this fix, the fingerprint points at the intermediate readonly dir instead of the
  original source, breaking incremental cache invalidation.
- **Double MSBuild load**: the readonly preprocessor loads via `SolutionLoader` for semantic
  analysis, and `ConvertProject` loads again for the main conversion. This is accepted overhead
  in v1; a future optimization could reuse the preprocessor's compilation.
- **Constructor visibility preserved**: the rewriter does NOT force constructors to `public`
  (earlier draft said "ensure public" — this was incorrect and would break API contracts). Only
  `field = default;` statements are injected.
