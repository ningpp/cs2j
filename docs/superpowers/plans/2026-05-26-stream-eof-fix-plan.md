# Stream.Read EOF vs InputStream.read EOF Fix — Implementation Plan

**Goal:** Fix the EOF return value discrepancy between C# `Stream.Read(byte[], int, int)` (returns 0 at EOF) and Java `InputStream.read(byte[], int, int)` (returns -1 at EOF).

**Hard constraints**: Do not modify C# source. Do not modify generated Java. Fix must be in the compat library (not converter).

---

## Background

| Operation | C# Stream.Read | Java InputStream.read |
|---|---|---|
| `Read(byte[], int, int)` | Returns **0** at EOF | Returns **-1** at EOF |
| `ReadByte()` / `read()` (no-arg) | Returns **-1** at EOF | Returns **-1** at EOF |

The failure chain (from root cause analysis Bug 3):
1. `BlockReaderFactory.raw()` Lambda → `stream.read(b, 0, number)` → returns -1 at EOF
2. `BuildBuffer.read()` checks `if (count == 0)` → -1 ≠ 0 → enters else branch
3. `StringBuilder.append(chars, 0, -1)` → `IndexOutOfBoundsException`

---

## Architecture Decision

**Fix in the compat library**, not the converter.

Alternatives rejected:
- Converter-level rewriter: Cannot distinguish byte-array `.read()` from single-byte `.read()` by method name alone; couples converter to compat internals
- BuildBuffer-specific fix (`count <= 0`): Pattern-specific, doesn't fix other code with the same bug

**Chosen approach**: Fix `StreamWrapper.read(byte[], int, int)` to return 0 on EOF (C# semantics). This fixes ALL converted code universally with a 1-line change.

---

## Task 1: Add Failing Tests

**File:** `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/StreamCompatibilityTest.java`

- [ ] Empty InputStream → `read(buf, 0, n)` returns 0 (not -1)
- [ ] Empty MemoryStream → `read(buf, 0, n)` returns 0
- [ ] MemoryStream partial read → returns available count, then 0 at EOF
- [ ] BlockReaderFactory pattern: empty stream → read returns 0 so `count == 0` works
- [ ] MemoryStream `inputAdapter` → still returns -1 on EOF (Java contract)
- [ ] StreamReader round-trip through empty MemoryStream → EOF detected correctly

---

## Task 2: Fix StreamWrapper.read(byte[], int, int)

**File:** `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StreamWrapper.java`

Change:
```java
public int read(byte[] buffer, int offset, int count) {
    try { return inputStream().read(buffer, offset, count); }
    catch (IOException e) { throw new java.io.UncheckedIOException(e); }
}
```

To:
```java
public int read(byte[] buffer, int offset, int count) {
    try {
        int result = inputStream().read(buffer, offset, count);
        return result == -1 ? 0 : result;
    } catch (IOException e) { throw new java.io.UncheckedIOException(e); }
}
```

Single-byte `read()` (no-arg) does NOT need modification — both C# and Java return -1 on EOF for single-byte reads.

---

## Task 3: Fix MemoryStream.read(byte[], int, int) and inputAdapter

**File:** `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/MemoryStream.java`

### Step 3a: Change EOF return from -1 to 0

```java
if (position >= length) return 0;  // was: return -1;
```

### Step 3b: Decouple inputAdapter from MemoryStream.read()

MemoryStream serves two roles:
1. As `StreamWrapper` for converted C# code → `read(buf, off, len)` returns 0 on EOF
2. As `InputStream` provider for Java code (via `inputStream()`) → must return -1 on EOF

The `inputAdapter` currently delegates to `this.read()` — after Step 3a, this would break Java consumers (`InputStreamReader` inside `StreamReader`). Fix by giving `inputAdapter` its own implementation:

```java
private final InputStream inputAdapter = new InputStream() {
    @Override
    public int read() { return MemoryStream.this.read(); }
    @Override
    public int read(byte[] b, int off, int len) {
        if (position >= length) return -1;  // Java contract
        int n = Math.min(len, length - position);
        System.arraycopy(buffer, position, b, off, n);
        position += n;
        return n;
    }
};
```

Single-byte `read()` delegation stays the same (both contracts match).

---

## Task 4: Verify

- [ ] `cd java/csharptojava-compat && mvn test` → all tests pass
- [ ] `dotnet test` from repo root → no converter test regressions
- [ ] Re-convert MSAGL → `SugiyamaLayoutTests#randomDotFileTests` passes

---

## Risk Assessment

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Breaking Java code expecting -1 from StreamWrapper | Very Low | StreamWrapper is specifically for C# compat; Java code should use `.inputStream()` |
| MemoryStream decoupling edge cases | Low | Dedicated test verifies both contracts independently |
| Other StreamWrapper subclasses | Low | Only MemoryStream extends StreamWrapper; review completed |
