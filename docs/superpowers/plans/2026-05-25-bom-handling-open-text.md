# Fix UTF-8 BOM Handling in FileHelper.openText

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix `FileHelper.openText` to skip a leading UTF-8 BOM before creating `InputStreamReader`, matching C# StreamReader behavior.

**Architecture:** Single-file change to the Java compat library. Wrap the `FileInputStream` with BOM detection using `PushbackInputStream` — read first 3 bytes, if they match UTF-8 BOM discard them, otherwise push back. Then create `InputStreamReader` from the (possibly advanced) stream.

**Tech Stack:** Java 25, JUnit 5, Maven

---

### Task 1: Write the failing test

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/FileHelperTest.java`

- [ ] **Step 1: Create FileHelperTest.java with BOM test**

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;

class FileHelperTest {

    @Test
    void openText_ShouldSkipUtf8Bom() throws Exception {
        Path tmp = Files.createTempFile("bomtest", ".txt");
        try (OutputStream os = Files.newOutputStream(tmp)) {
            os.write(0xEF);
            os.write(0xBB);
            os.write(0xBF);
            os.write("<?xml version=\"1.0\"?>".getBytes(StandardCharsets.UTF_8));
        }

        try (TextReader reader = FileHelper.openText(tmp.toString())) {
            int first = reader.peek();
            assertEquals('<', (char) first,
                "First character after BOM should be '<', got: " + (char) first + " (0x" + Integer.toHexString(first) + ")");
        } finally {
            Files.deleteIfExists(tmp);
        }
    }

    @Test
    void openText_ShouldWorkWithoutBom() throws Exception {
        Path tmp = Files.createTempFile("nobomtest", ".txt");
        try (OutputStream os = Files.newOutputStream(tmp)) {
            os.write("hello world".getBytes(StandardCharsets.UTF_8));
        }

        try (TextReader reader = FileHelper.openText(tmp.toString())) {
            int first = reader.peek();
            assertEquals('h', (char) first);
        } finally {
            Files.deleteIfExists(tmp);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd java/csharptojava-compat && mvn test -Dtest=FileHelperTest -pl .`

Expected: `openText_ShouldSkipUtf8Bom` FAILS — `assertEquals` reports `'<'` expected but got `﻿` (U+FEFF, 0xFEFF).

`openText_ShouldWorkWithoutBom` PASSES (no BOM in file).

- [ ] **Step 3: Commit the failing test**

```bash
git add java/csharptojava-compat/src/test/java/io/github/ningpp/compat/FileHelperTest.java
git commit -m "test: add failing test for UTF-8 BOM handling in FileHelper.openText

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 2: Implement the BOM-skipping fix

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/FileHelper.java:21-24`

- [ ] **Step 1: Modify openText to skip BOM**

Replace the `openText` method body (lines 21-24) with:

```java
    public static TextReader openText(String path) {
        try {
            InputStream fs = new FileInputStream(path);
            PushbackInputStream pbs = new PushbackInputStream(fs, 3);
            byte[] bom = new byte[3];
            int read = pbs.read(bom);
            if (read == 3 && bom[0] == (byte)0xEF && bom[1] == (byte)0xBB && bom[2] == (byte)0xBF) {
                // BOM detected and skipped — stream is positioned after it
            } else {
                // No BOM — push back whatever bytes we read
                if (read > 0) pbs.unread(bom, 0, read);
            }
            return new TextReader(new InputStreamReader(pbs, StandardCharsets.UTF_8));
        } catch (FileNotFoundException e) { throw new UncheckedIOException(e); }
        catch (IOException e) { throw new UncheckedIOException(e); }
    }
```

Add the `java.io.IOException` import to the existing imports (it's already imported via `java.io.*` so no import change needed).

- [ ] **Step 2: Run tests to verify fix**

Run: `cd java/csharptojava-compat && mvn test -Dtest=FileHelperTest -pl .`

Expected: Both tests PASS.

- [ ] **Step 3: Run the full test suite to check for regressions**

Run: `cd java/csharptojava-compat && mvn test`

Expected: All tests PASS.

- [ ] **Step 4: Commit the fix**

```bash
git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/FileHelper.java
git commit -m "fix: skip UTF-8 BOM in FileHelper.openText to match C# StreamReader behavior

C# StreamReader detects and skips the UTF-8 BOM automatically. Java's
InputStreamReader does not, causing the first peek()/read() to return
U+FEFF instead of the actual first content character. This broke
GeometryGraphReader.createFromFile which checks firstCharacter(fileName)
!= '<' to detect XML files with BOM prefixes.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```
