# CSharpToJavaConverter

A C# to Java source code converter using Roslyn for parsing and semantic analysis.

## Features

- **Single-file & project-level conversion** — convert a `.cs` file or an entire `.csproj`/`.sln` workspace
- **Maven multi-module output** — generates `pom.xml` with dependency graph, compat packs, and resource copying
- **Type mapping** — JSON-driven type mapping with Java standard library metadata (`config/java/`)
- **LINQ rewriting** — converts LINQ to procedural code or Java Stream API (configurable strategy)
- **Async/await** — translates `Task` / `Task<T>` to `CompletableFuture`
- **C# → Java language features** — records, structs, delegates, events, indexers, operators, pattern matching, `yield return`, `ref`/`out` parameters, `using` statements, default parameters
- **Goto elimination** — standalone `eliminate-goto` command transforms `goto`/`label` into switch-based state machines
- **Partial type merging** — merges `partial class`/`partial method` across files
- **Compatibility runtime** — `csharptojava-compat` Java library bridges C# API patterns (collections, reflection, exceptions, etc.)
- **Incremental builds** — input fingerprint caching skips re-conversion when sources are unchanged
- **Java 25 target** — supports modern Java language features

## Requirements

- .NET 10.0 SDK

## Building

```bash
dotnet build
```

## Running Tests

```bash
dotnet test
```

## Usage

### Convert a single file

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert -i Input.cs -o Output.java
```

### Convert an entire project

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s ./MyProject.csproj -d ./output
```

### Convert a solution

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s ./MySolution.sln -d ./output
```

### Eliminate goto statements

```bash
# Single file
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- eliminate-goto -i Input.cs -o Output.cs

# Directory
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- eliminate-goto -s ./src -d ./clean
```

### Analyze type mapping coverage

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- analyze -s ./src -r report.json
```

## CLI Commands & Options

### `convert` — Single-file conversion

| Option | Description |
|--------|-------------|
| `-i, --input` | Input C# file path |
| `-o, --output` | Output Java file path (default: stdout) |
| `-m, --mapping` | Type mapping configuration file |
| `-j, --java-version` | Target Java version (default: Java25) |
| `--no-records` | Don't use Java records for C# records |
| `--use-optional` | Use `Optional` for nullable types |
| `--no-javadoc` | Don't generate JavaDoc comments |
| `--no-linq-rewrite` | Don't pre-process LINQ to procedural code |
| `--prefer-stream-api` | Prefer Java Stream API for LINQ conversion |
| `--prefer-procedural` | Prefer procedural loops for LINQ conversion |
| `-v, --verbose` | Show diagnostic messages |

### `convert-project` — Project/solution conversion

| Option | Description |
|--------|-------------|
| `-s, --source` | Source directory, `.csproj`, or `.sln` path |
| `-d, --destination` | Destination directory path |
| `-m, --mapping` | Type mapping configuration file |
| `-j, --java-version` | Target Java version (default: Java25) |
| `-f, --force` | Overwrite existing files (default: true) |
| `--no-cache` | Disable fingerprint-based caching (default: true) |
| `--no-records` | Don't use Java records |
| `--use-optional` | Use `Optional` for nullable types |
| `--no-javadoc` | Don't generate JavaDoc |
| `--no-linq-rewrite` | Don't pre-process LINQ to procedural code |
| `--prefer-stream-api` | Prefer Java Stream API for LINQ conversion |
| `--prefer-procedural` | Prefer procedural loops for LINQ conversion |
| `--maven-group-id` | Maven groupId (default: io.github.ningpp) |
| `--maven-version` | Maven version (default: 0.0.1-SNAPSHOT) |
| `--include-tests` | Include test projects (default: true) |
| `--no-eliminate-goto` | Disable goto-elimination preprocessing |
| `--extra-deps` | Comma-separated Maven coordinates to add to every module |
| `--linq-report` | Output LINQ preprocessing report |
| `-v, --verbose` | Show detailed progress |

### `eliminate-goto` — Goto elimination

| Option | Description |
|--------|-------------|
| `-i, --input` | Input `.cs` file (file mode) |
| `-o, --output` | Output `.cs` file (default: stdout) |
| `-s, --source` | Source directory (directory mode) |
| `-d, --destination` | Destination directory (directory mode) |
| `--strict` | Treat unsupported-method diagnostics as fatal |
| `-v, --verbose` | Show detailed progress |

### `analyze` — Type mapping analysis

| Option | Description |
|--------|-------------|
| `-s, --source` | Source directory path |
| `-m, --mapping` | Type mapping configuration file |
| `-r, --report` | Output report file path (JSON) |

## Architecture

```
src/
├── CSharpToJava.CLI/             # CLI entry point (CommandLineParser)
├── CSharpToJava.Core/
│   ├── Context/                   # Conversion context, options, diagnostics
│   ├── Transformers/
│   │   ├── Expression/            # Expression transformers (invocation, binary, lambda, etc.)
│   │   ├── Statement/             # Statement transformers (loops, conditionals, goto, etc.)
│   │   ├── Member/                # Member transformers (method, property, constructor, etc.)
│   │   ├── Type/                  # Type transformers (class, struct, record, enum, delegate, interface)
│   │   └── Utilities/             # Default parameters, nested types, runtime class params
│   ├── Lowering/                  # IR lowering passes (delegate, event, indexer, operator, property, ref/out, struct, using, yield)
│   ├── LinqRewrite/               # LINQ-to-procedural/Stream rewrite engine
│   ├── GotoEliminator/            # Goto/label → state machine transformation
│   ├── HIR/                       # High-level IR generation
│   ├── Java/                      # Java AST & code generation
│   ├── Java2/                     # Second-generation IR & code generator
│   ├── PartialType/               # Partial type/method merging
│   ├── Pipeline/                  # Conversion pipeline, passes, planning, Maven POM generation
│   │   ├── Compatibility/         # Compat pack registry & planner
│   │   ├── Planning/              # Workspace plan, module layout, incremental caching
│   │   └── Passes/                # Single-file, project-level, validation passes
│   ├── Visitors/                  # Roslyn syntax visitor
│   └── Workspace/                 # MSBuild workspace loader
└── CSharpToJava.TypeMapping/     # Type mapping configuration

java/
└── csharptojava-compat/          # Java compatibility runtime library
    └── src/main/java/io/github/ningpp/compat/  # C# API bridges

config/
├── TypeMappings.json              # C# → Java type mapping rules
└── java/                          # Java standard library metadata (JSON)

tests/
└── CSharpToJava.Tests/           # XUnit test suite
```

## Design Docs

- [J2CL-informed Java-only architecture plan for large C# projects](docs/j2cl-architecture-upgrade.md)
- [LINQ 重写引擎架构设计 / LINQ Rewrite Engine Architecture Design](docs/linq-rewrite-engine-architecture.md)
- [Goto Eliminator 设计文档 / Goto Eliminator Design](docs/superpowers/specs/2026-06-27-goto-eliminator-design.md)

## Third-Party Credits

- **[roslyn-linq-rewrite](https://github.com/antiufo/roslyn-linq-rewrite)** by Michał Komorowski (MIT License) — The LINQ rewriting engine in `src/CSharpToJava.Core/LinqRewrite/` is based on this project, which converts C# LINQ expressions to procedural code. We adapted it to generate Java-compatible output.

## License

[MIT License](LICENSE)
