# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

### Building
```bash
dotnet build                    # Build entire solution
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj    # Build specific project
```

### Running Tests
```bash
dotnet test                     # Run all tests
dotnet test --filter "FullyQualifiedName~AliasTest"            # Run specific test
```

### Running the CLI
```bash
# Convert a single file
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert -i Input.cs -o Output.java

# Convert an entire project
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s ./src -d ./output

# Analyze a project for type mapping coverage
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- analyze -s ./src -r report.json
```

### CLI Options
- `-i, --input`: Input C# file path
- `-o, --output`: Output Java file path (default: stdout)
- `-m, --mapping`: Path to type mapping configuration file (default: ./config/TypeMappings.json)
- `-j, --java-version`: Target Java version (Java25)
- `--no-records`: Don't use Java records for C# records
- `--use-optional`: Use Optional for nullable types
- `--no-javadoc`: Don't generate JavaDoc comments
- `--no-linq-rewrite`: Don't pre-process LINQ to procedural code
- `-v, --verbose`: Show diagnostic messages

## Architecture Overview

This is a **source code converter** that translates C# code to Java using Roslyn (Microsoft.CodeAnalysis) for parsing and semantic analysis.

### Project Structure

```
CSharpToJavaConverter/
├── src/
│   ├── CSharpToJava.Core/          # Core conversion engine (Roslyn-based)
│   ├── CSharpToJava.CLI/           # Command-line interface
│   ├── CSharpToJava.TypeMapping/   # Type mapping configuration (JSON-driven)
├── tests/
│   └── CSharpToJava.Tests/         # XUnit tests
└── config/
    └── TypeMappings.json           # Default type mappings
```

### Architecture (v2 — in progress)

The new conversion engine follows a five-phase pipeline:
1. **Frontend**: C# parsing + LINQ rewrite + partial type merging
2. **HIR Generation**: C# Syntax Tree -> C#-flavored Java IR (`src/CSharpToJava.Core/HIR/`)
3. **Lowering**: 12 semantic lowering passes eliminate C#-specific semantics (`src/CSharpToJava.Core/Lowering/`)
4. **Validation**: Diagnostics-only IR checks
5. **CodeGen**: Pure Java IR -> formatted Java source (`src/CSharpToJava.Core/Java2/CodeGen/`)

New code locations:
- `src/CSharpToJava.Core/Java2/` — New IR model (IrNode, IrExpression, IrStatement, IrDeclaration) + CodeGen
- `src/CSharpToJava.Core/HIR/` — HIR Generator
- `src/CSharpToJava.Core/Lowering/` — Semantic lowering passes
- `src/CSharpToJava.Core/Pipeline/NewConversionPipeline.cs` — New pipeline entry point

### Conversion Pipeline

The conversion follows a **three-phase pipeline**:

1. **Parsing Phase**: C# syntax tree analysis with Roslyn
2. **Transformation Phase**: Visitor pattern with specialized transformers
3. **Code Generation Phase**: Java syntax tree to source code

Key entry point: `src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs:46`

### Type Mapping System

**Configuration-driven** type mappings defined in `config/TypeMappings.json`:
- Basic type mappings (int -> int, string -> String)
- Generic type mappings (List<T> -> List<T>)
- Method name mappings (Add -> put, Add -> add)
- Namespace-to-package mappings

Managed by: `src/CSharpToJava.TypeMapping/TypeMappingRegistry.cs:92`

### Core Components

#### Visitor Pattern
`CSharpToJavaVisitor` (`src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs:15`) implements `CSharpSyntaxVisitor<JavaSyntaxNode?>` to traverse C# syntax trees and delegate to specialized transformers.

#### Transformer Factory
Creates specialized transformers for different C# constructs (`src/CSharpToJava.Core/Transformers/TransformerFactory.cs`):
- Type transformers (Class/Interface/Enum/Record/Struct)
- Member transformers (Method/Property/Field/Constructor/Indexer)
- Statement/Expression transformers

#### Conversion Context
`ConversionContext` (`src/CSharpToJava.Core/Context/ConversionContext.cs:65`) maintains conversion state:
- Current namespace/type/method stacks
- Type symbol cache
- Using alias registry (file-scoped)
- Import collection
- Semantic model access

### Key Design Patterns

1. **Pipeline Pattern**: Clean separation of conversion phases
2. **Visitor Pattern**: C# syntax tree traversal
3. **Factory Pattern**: Centralized transformer creation
4. **Registry Pattern**: Type mapping management
5. **Adapter Pattern**: Roslyn semantic model to converter context
6. **Rewriter Pattern**: `JavaSyntaxRewriter` for IR-level post-processing

### Java IR (Intermediate Representation)

Located in `src/CSharpToJava.Core/Java/` (9 files, ~2,000 lines):
- `JavaSyntaxNode` — base class for all IR nodes
- `JavaCompilationUnit` — file-level: package, imports, type declarations
- `JavaTypeDeclaration` — class/interface/enum/record declarations
- `JavaMemberDeclaration` — field/method/constructor/parameter/annotations/modifiers
- `JavaStatement` — 14 statement types (block, if, for, while, try-catch, switch, etc.)
- `JavaExpression` — 16 expression types (method call, member access, literal, lambda, etc.)
- `JavaMethodBody` — structured method body container (alternative to raw string Body)
- `JavaSyntaxRewriter` — deep traversal framework for IR-level modifications
- `JavaRawStatement` / `JavaRawExpression` — backward-compatible fallback to raw strings

### Important Features

#### Using Aliases
Supports C# using aliases like `using P2 = Core.Geometry.Point;`. Aliases are:
- File-scoped (cleared per file)
- Registered with semantic symbol resolution
- Validated for Java keyword conflicts and duplicate definitions
- Handled in: `src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs:118`

#### LINQ Rewriting
Preprocessing phase that converts LINQ queries to procedural Java code before main conversion. Located in: `src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs`

#### Partial Type Merging
Can merge partial class declarations across files using `ProjectConversionPipeline`. See: `src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs`

#### Async/Await Translation
Maps C# `Task<T>` to Java `CompletableFuture<T>` and async/await to CompletableFuture chains.

### Configuration

The default type mapping configuration is in `config/TypeMappings.json`. To customize:
1. Copy the file
2. Modify type/method/namespace mappings
3. Pass with `-m` flag: `convert -m custom-mappings.json ...`

### Dependencies

- **Microsoft.CodeAnalysis.CSharp 4.12.0**: Roslyn for C# parsing
- **CommandLineParser**: CLI argument parsing
- **System.Text.Json**: JSON configuration
- **XUnit**: Testing framework

### Testing

Tests use XUnit with Coverlet for coverage. Currently minimal - add tests for new transformers and type mappings.

### Java Version Support

Supports Java 25. All modern Java features are available including records, records require Java 14+.
