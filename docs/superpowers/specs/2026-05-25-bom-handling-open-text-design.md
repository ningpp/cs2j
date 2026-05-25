# Fix UTF-8 BOM Handling in FileHelper.openText

## Problem

`FileHelper.openText` creates a Java `InputStreamReader` that does not skip the UTF-8 BOM (`U+FEFF`). C# `StreamReader` detects and skips the BOM automatically. This causes BOM-prefixed text files to be misread — the first `peek()`/`read()` returns `﻿` instead of the first content character.

### Concrete failure

`GeometryGraphReader.createFromFile` checks `firstCharacter(fileName) != '<'` to detect XML files. The `baseball.msagl.geom` test file starts with a UTF-8 BOM followed by `<?xml`. In Java, `firstCharacter` reads `﻿` (the BOM), finds it's not `'<'`, and returns `null`. `MsaglTestBase.loadGraph` then passes `null` to `setupPorts`, which throws `NullPointerException("graph")`.

### Why C# works

C# `StreamReader` constructor detects the UTF-8 BOM in the first 3 bytes, sets the encoding accordingly, and advances the stream position past the BOM. `Peek()` returns `'<'`.

### Why Java fails

Java `InputStreamReader` with `StandardCharsets.UTF_8` reads the BOM bytes as the Unicode character `﻿` (zero-width no-break space). It does NOT advance past it. `peek()` returns `﻿`.

## Solution

Modify `FileHelper.openText` to detect and skip a leading UTF-8 BOM before creating the `InputStreamReader`.

### Implementation

In `FileHelper.openText`, wrap the `FileInputStream` with BOM detection logic:

1. Read up to 3 bytes from the stream
2. If bytes match `0xEF, 0xBB, 0xBF` (UTF-8 BOM), discard them and continue
3. If not, push any read bytes back via `PushbackInputStream`
4. Create `InputStreamReader` from the (possibly advanced) stream

### Why this approach

- **Root cause fix**: addresses the Java/C# semantic difference directly
- **Single point of change**: `FileHelper.openText` is the only entry point for opening text files in the compat layer
- **Transparent to callers**: `firstCharacter`, `readAllText`, and all other `TextReader` consumers benefit automatically
- **Matches C# semantics exactly**: C# `StreamReader` detects BOM and advances; this makes Java do the same

## Scope

### Java compat (fix)

- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/FileHelper.java` — modify `openText` to skip BOM

### Not in scope

- `Path.Combine` varargs — already fixed in commit `92a873c`, regenerating Java output picks up the fix
- Other BOM encodings (UTF-16 LE/BE) — C# `StreamReader` handles these too, but they're uncommon in this codebase; can be added later if needed
- BOM handling in `FileHelper.readAllText` — this method uses `Files.readAllBytes` then constructs a `String` from bytes, which also doesn't skip BOM. If callers hit this, it's a separate fix with the same root cause

## Files to change

### Java compat library

| File | Change |
|------|--------|
| `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/FileHelper.java` | Modify `openText` to detect and skip UTF-8 BOM before creating `InputStreamReader` |

## Verification

1. Build the compat library: `cd java/csharptojava-compat && mvn package`
2. Rebuild the converter: `dotnet build`
3. Regenerate the Java test project from the C# source
4. Run `baseballLayeredTest` — it should pass (load the graph, not throw NPE)
