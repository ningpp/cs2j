# Design: Line Continuation Trim Fix for CR-Only Endings

Date: 2026-05-27

## Problem

The `bug1.dot` test file uses CR-only (`\r`, 0x0D) line endings. At a DOT line
continuation point (backslash at end of physical line), the hex bytes are:

```
hex: ... 35 34 5c 0d 37 ...
str: ...  5  4  \  \r  7 ...
```

The DOT lexer's STRING-state rule for line continuation is:

```
<STRING>([^\n"]*)(\\\r?\n)    { stringId += yytext; TrimString(); }
```

This regex requires LF (`\n`) after the optional CR. Since the file has CR without
LF, the DFA routes to state 35 instead of state 34:

| DFA State | Trigger | Action |
|-----------|---------|--------|
| 34 | `\<LF>` or `\<CR><LF>` | `stringId += yytext; trimString();` |
| 35 | `\<CR>` only | `stringId += yytext;` — no trim |

The `TrimString()` method only handles two cases:

```csharp
if (stringId.EndsWith("\\\r\n"))       // \<CR><LF> — strip 3 chars
    ...
else if (stringId.EndsWith("\\\n"))    // \<LF> — strip 2 chars
    ...
// MISSING: "\\\r" case — \<CR> only
```

Result: `54\<CR>7` enters the accumulated string. The `Split` method splits on
CR (it is in the delimiter set `[ ,\n\r;\t]`), but backslash is NOT a delimiter.
So `54\` becomes a standalone token that fails `Double.parseDouble("54\")`.

## Constraints

1. Cannot modify the original C# project (E:\agl-master\GraphLayout\)
2. Cannot modify the generated Java project (E:\z5\)
3. Fix must be in the cs2j converter
4. Must re-convert the project to verify
5. Must include unit tests

## Solution: LineContinuationTrimRewriter

A `JavaSyntaxRewriter` that runs post-HIR-generation, pre-CodeGen. It detects
the incomplete `trimString` pattern in GPLEX-generated Scanner classes and
injects the missing `\<CR>` handling.

### Two Required Changes

**Change 1 — Add `trimString()` call to state 35 action in `scan()`:**

```java
// BEFORE:
case 30, 31, 33, 35:
    stringId += getYytext();
    break;

// AFTER:
case 30, 31, 33:
    stringId += getYytext();
    break;
case 35:
    stringId += getYytext();
    trimString();
    break;
```

**Change 2 — Add `\<CR>` branch to `trimString()`:**

```java
// BEFORE:
void trimString() {
    if (stringId.endsWith("\\\r\n")) {
        stringId = stringId.substring(0, stringId.length() - 3);
    } else {
        if (stringId.endsWith("\\\n")) {
            stringId = stringId.substring(0, stringId.length() - 2);
        }
    }
}

// AFTER:
void trimString() {
    if (stringId.endsWith("\\\r\n")) {
        stringId = stringId.substring(0, stringId.length() - 3);
    } else if (stringId.endsWith("\\\n")) {
        stringId = stringId.substring(0, stringId.length() - 2);
    } else if (stringId.endsWith("\\\r")) {
        stringId = stringId.substring(0, stringId.length() - 2);
    }
}
```

### Detection Conditions

The rewriter triggers when BOTH conditions are met in the same class:

1. A `trimString` method exists whose body contains `endsWith("\\\r\n")` and
   `endsWith("\\\n")` but NOT `endsWith("\\\r")`

2. The `scan()` method contains a switch case grouping that includes state 35
   alongside 30/31/33 with only `stringId += getYytext()` (no `trimString()`)

### Files

| File | Purpose |
|------|---------|
| `src/CSharpToJava.Core/Java/Rewriters/LineContinuationTrimRewriter.cs` | New rewriter |
| `tests/CSharpToJava.Tests/Rewriters/LineContinuationTrimRewriterTests.cs` | Unit tests |

### Pipeline Integration

The rewriter runs in the lowering phase, after HIR generation and before CodeGen,
alongside existing rewriters like `MathMethodRewriter`.

## Verification

1. Unit tests: construct Java IR matching the before state, apply rewriter,
   assert after state
2. Re-convert `E:\agl-master\GraphLayout\` with the fix
3. Run `bug1.dot` through the regenerated Java parser — confirm no
   `NumberFormatException`
