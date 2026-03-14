# Task Plan: MSAGL C# to Java Conversion

## Goal
Convert the MSAGL (Microsoft Automatic Graph Layout) C# project to Java and fix ALL compilation errors until the generated Java code compiles successfully.

## Constraints
- **Source**: `C:\automatic-graph-layout-master\GraphLayout\MSAGL`
- **Destination**: `C:\agl\v20260314`
- **Java version**: Java 25
- **Maven generation**: Enabled
- **NEVER** modify MSAGL C# source code
- **NEVER** modify generated Java source code
- Fix errors by improving the CONVERTER (CSharpToJavaConverter), then regenerate

## Phases

### Phase 1: Setup [complete]
- [x] Verify source exists: C:\automatic-graph-layout-master\GraphLayout\MSAGL (492 .cs files)
- [x] Verify destination parent exists: C:\agl
- [x] Understand CLI options
- [x] Create planning files

### Phase 2: Build & First Conversion [in_progress]
- [ ] Build the CSharpToJavaConverter CLI
- [ ] Run convert-project on MSAGL source
- [ ] Verify Java files were generated

### Phase 3: Compile & Error Collection
- [ ] Install Java 25+ if needed (verify with `java -version`)
- [ ] Run `mvn compile` in C:\agl\v20260314
- [ ] Collect all compilation errors to findings.md

### Phase 4: Fix Converter Errors (iterate)
- [ ] Analyze error patterns
- [ ] Fix converter code for each error category
- [ ] Rebuild converter
- [ ] Regenerate Java from MSAGL
- [ ] Recompile and check remaining errors
- [ ] Repeat until 0 errors

## Conversion Command
```powershell
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s "C:\automatic-graph-layout-master\GraphLayout\MSAGL" `
  -d "C:\agl\v20260314" `
  -j Java25 `
  --generate-pom `
  --force `
  -v
```

## Errors Encountered
| Error | Attempt | Resolution |
|-------|---------|------------|
| (none yet) | - | - |

## Key Decisions
- Fix errors in the CONVERTER code, then regenerate - never touch generated Java
- Use `--force` to allow overwriting destination on each iteration
