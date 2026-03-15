# CSharpToJavaConverter

A C# to Java source code converter using Roslyn for parsing and semantic analysis.

## Features

- Convert C# code to Java with support for modern Java versions (8, 11, 17, 21, 25)
- Type mapping configuration (JSON-driven)
- LINQ-to-procedural code rewriting
- Async/await translation (Task → CompletableFuture)
- Java Records support for C# records
- Project-level conversion with partial type merging

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
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s ./src -d ./output
```

### Analyze type mapping coverage

```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- analyze -s ./src -r report.json
```

## CLI Options

| Option | Description |
|--------|-------------|
| `-i, --input` | Input C# file path |
| `-o, --output` | Output Java file path (default: stdout) |
| `-m, --mapping` | Type mapping configuration file (default: ./config/TypeMappings.json) |
| `-j, --java-version` | Target Java version (Java8, Java11, Java17, Java21) |
| `--no-records` | Don't use Java records for C# records |
| `--use-optional` | Use Optional for nullable types |
| `--no-javadoc` | Don't generate JavaDoc comments |
| `--no-linq-rewrite` | Don't pre-process LINQ to procedural code |
| `-v, --verbose` | Show diagnostic messages |

## Architecture

```
src/
├── CSharpToJava.Core/      # Core conversion engine (Roslyn-based)
├── CSharpToJava.CLI/       # Command-line interface
├── CSharpToJava.TypeMapping/   # Type mapping configuration
├── CSharpToJava.LINQ/      # LINQ-to-procedural conversion
└── CSharpToJava.Async/     # Async/await support

tests/
└── CSharpToJava.Tests/     # XUnit tests
```

## License

MIT License
