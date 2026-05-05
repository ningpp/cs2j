# Strip Unreachable `break;` After Terminal Statements in Switch Sections

**Date:** 2026-05-05
**Status:** Approved

## Problem

C# allows `break;` (or other statements) after terminal statements in switch sections — the compiler emits CS0162 (unreachable code) as a warning, but the code compiles. When converted to Java, this produces unreachable code which is a **compile-time error** per JLS §14.21.

Example:
```csharp
// Valid C# (CS0162 warning only)
case 4:
    return (int) MkId(yytext);
    break;

// Invalid Java (compile error)
case 4:
    return (int) MkId(yytext);
    break;
```

The old conversion pipeline (`TransformPlainSwitch`) blindly converts all statements in a switch section including both the terminal and the unreachable follow-up, producing invalid Java.

## Scope

Switch-section scope: strip unreachable statements after any terminal within switch sections. The same problem could theoretically occur in non-switch blocks, but C# switch sections are the only place where `return X; break;` is an idiomatic pattern (some developers write the `break;` out of habit even after a `return`).

## Design

### File

`src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs`

### Change

Replace the blind `.Select()` at line 127-128 with an iterative loop that stops when it hits a terminal statement.

**Before:**
```csharp
var statements = section.Statements.Select(s =>
    stmtTransformer.Transform(s, context).ToString("")).ToList();
```

**After:**
```csharp
var statements = new List<string>();
foreach (var s in section.Statements)
{
    statements.Add(stmtTransformer.Transform(s, context).ToString(""));
    if (IsSwitchSectionTerminal(s))
        break;
}
```

### New helper

```csharp
private static bool IsSwitchSectionTerminal(StatementSyntax stmt) =>
    stmt.Kind() switch
    {
        SyntaxKind.ReturnStatement or
        SyntaxKind.ThrowStatement or
        SyntaxKind.ContinueStatement or
        SyntaxKind.GotoStatement or
        SyntaxKind.GotoCaseStatement or
        SyntaxKind.GotoDefaultStatement => true,
        _ => false
    };
```

`BreakStatement` is intentionally excluded — `break;` is the normal section terminator and the statements before it are reachable.

### What's affected

| C# input | Java output |
|----------|-------------|
| `return X; break;` | `return X;` only |
| `throw ex; break;` | `throw ex;` only |
| `goto case 0; break;` | `goto case 0;` only |
| `doWork(); break;` | `doWork(); break;` (both kept) |
| `doWork(); return X; break;` | `doWork(); return X;` |

### Why the old pipeline only

The new HIR pipeline does not handle user-written switch statements yet — `HIRStatementGenerator` falls through to an error placeholder for `SwitchStatementSyntax`. The old pipeline is the only code path processing source-level switch statements.

If/when HIR gains switch support, equivalent terminal-detection logic should be included there.

## Risk

- **Minimal.** The change only affects what gets emitted when a terminal statement is present in a switch section. The existing behavior for normal sections (ending in `break;` only) is unchanged.
- No test regressions expected — existing tests are about pattern-matching switch behavior and basic plain switch output, not about dead-code-after-terminals.
