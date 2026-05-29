# CSharpRandom Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `io.github.ningpp.compat.CSharpRandom` that wraps `java.util.Random` with full .NET `System.Random` semantic fidelity, and update type mappings so C# Random code converts correctly.

**Architecture:** Composition-based wrapper over `java.util.Random`. Each public method enforces C# range semantics. TypeMappings.json updated to point `System.Random` → `CSharpRandom` with corrected method mappings.

**Tech Stack:** Java 25, JUnit 5, Maven

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpRandom.java` | CSharpRandom class |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpRandomTest.java` | Tests |
| Modify | `config/TypeMappings.json` | Type + method mappings |

---

### Task 1: Create CSharpRandom.java

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpRandom.java`

- [ ] **Step 1: Write the CSharpRandom implementation**

```java
package io.github.ningpp.compat;

public class CSharpRandom {

    private final java.util.Random random;

    public CSharpRandom() {
        this.random = new java.util.Random();
    }

    public CSharpRandom(int seed) {
        this.random = new java.util.Random(seed);
    }

    public int next() {
        return random.nextInt(Integer.MAX_VALUE);
    }

    public int next(int maxValue) {
        if (maxValue < 0) {
            throw new IllegalArgumentException(
                "maxValue must be positive, was: " + maxValue);
        }
        return random.nextInt(maxValue);
    }

    public int next(int minValue, int maxValue) {
        if (minValue > maxValue) {
            throw new IllegalArgumentException(
                "minValue must be less than or equal to maxValue. minValue: "
                + minValue + ", maxValue: " + maxValue);
        }
        long range = (long) maxValue - minValue;
        if (range <= Integer.MAX_VALUE) {
            return ((int) (sample() * range) + minValue);
        } else {
            return (int) ((long) (getSampleForLargeRange() * range) + minValue);
        }
    }

    protected double sample() {
        return random.nextDouble();
    }

    private double getSampleForLargeRange() {
        int result = next();
        boolean negative = (next() % 2 == 0);
        if (negative) {
            result = -result;
        }
        double d = result;
        d += (Integer.MAX_VALUE - 1);
        d /= 2.0 * (long) Integer.MAX_VALUE - 1;
        return d;
    }

    public double nextDouble() {
        return random.nextDouble();
    }

    public float nextSingle() {
        return (float) random.nextDouble();
    }

    public long nextInt64() {
        return (long) (sample() * Long.MAX_VALUE);
    }

    public void nextBytes(byte[] buffer) {
        if (buffer == null) {
            throw new NullPointerException("buffer");
        }
        for (int i = 0; i < buffer.length; i++) {
            buffer[i] = (byte) next();
        }
    }
}
```

- [ ] **Step 2: Verify compilation**

Run: `cd d:\code\cs2j\java\csharptojava-compat; mvn compile -q`
Expected: BUILD SUCCESS

- [ ] **Step 3: Commit**

```bash
git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpRandom.java
git commit -m "feat: add CSharpRandom class wrapping java.util.Random with C# semantics"
```

---

### Task 2: Create CSharpRandomTest.java

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpRandomTest.java`

- [ ] **Step 1: Write the test class**

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpRandomTest {

    @Test
    void next_defaultConstructor_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 10000; i++) {
            int v = r.next();
            assertTrue(v >= 0 && v < Integer.MAX_VALUE,
                "next() returned " + v + " which is out of [0, MAX_VALUE)");
        }
    }

    @Test
    void next_seeded_repeatable() {
        CSharpRandom r1 = new CSharpRandom(42);
        CSharpRandom r2 = new CSharpRandom(42);
        for (int i = 0; i < 100; i++) {
            assertEquals(r1.next(), r2.next());
        }
    }

    @Test
    void next_maxValue1_alwaysZero() {
        CSharpRandom r = new CSharpRandom(123);
        for (int i = 0; i < 1000; i++) {
            assertEquals(0, r.next(1));
        }
    }

    @Test
    void next_maxValueNegative_throws() {
        CSharpRandom r = new CSharpRandom();
        assertThrows(IllegalArgumentException.class, () -> r.next(-1));
    }

    @Test
    void next_maxValueZero_throws() {
        CSharpRandom r = new CSharpRandom();
        assertThrows(IllegalArgumentException.class, () -> r.next(0));
    }

    @Test
    void next_maxValue_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        int max = 100;
        for (int i = 0; i < 10000; i++) {
            int v = r.next(max);
            assertTrue(v >= 0 && v < max,
                "next(maxValue) returned " + v + " out of [0, " + max + ")");
        }
    }

    @Test
    void next_minMax_equal_returnsMin() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 100; i++) {
            int v = r.next(5, 5);
            assertEquals(5, v);
        }
    }

    @Test
    void next_minMax_inverted_throws() {
        CSharpRandom r = new CSharpRandom();
        assertThrows(IllegalArgumentException.class, () -> r.next(10, 5));
    }

    @Test
    void next_minMax_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        int min = 10, max = 20;
        for (int i = 0; i < 10000; i++) {
            int v = r.next(min, max);
            assertTrue(v >= min && v < max,
                "next(min,max) returned " + v + " out of [10, 20)");
        }
    }

    @Test
    void next_minMax_largeRange() {
        CSharpRandom r = new CSharpRandom();
        int min = Integer.MIN_VALUE;
        int max = Integer.MAX_VALUE;
        for (int i = 0; i < 10000; i++) {
            int v = r.next(min, max);
            assertTrue(v >= min && v < max,
                "next(MIN,MAX) returned " + v + " out of range");
        }
    }

    @Test
    void nextDouble_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 10000; i++) {
            double v = r.nextDouble();
            assertTrue(v >= 0.0 && v < 1.0,
                "nextDouble() returned " + v + " out of [0.0, 1.0)");
        }
    }

    @Test
    void nextSingle_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 10000; i++) {
            float v = r.nextSingle();
            assertTrue(v >= 0.0f && v < 1.0f,
                "nextSingle() returned " + v + " out of [0.0f, 1.0f)");
        }
    }

    @Test
    void nextInt64_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 10000; i++) {
            long v = r.nextInt64();
            assertTrue(v >= 0L && v < Long.MAX_VALUE,
                "nextInt64() returned " + v + " out of [0, MAX_VALUE)");
        }
    }

    @Test
    void nextBytes_fillsBuffer() {
        CSharpRandom r = new CSharpRandom(42);
        byte[] buf = new byte[100];
        r.nextBytes(buf);
        boolean allZero = true;
        for (byte b : buf) {
            if (b != 0) allZero = false;
        }
        assertFalse(allZero, "nextBytes should produce non-trivial values");
    }

    @Test
    void nextBytes_nullBuffer_throws() {
        CSharpRandom r = new CSharpRandom();
        assertThrows(NullPointerException.class, () -> r.nextBytes(null));
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `cd d:\code\cs2j\java\csharptojava-compat; mvn test -pl . -Dtest=CSharpRandomTest -q`
Expected: Tests pass

- [ ] **Step 3: Commit**

```bash
git add java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpRandomTest.java
git commit -m "test: add CSharpRandomTest covering all methods and boundary conditions"
```

---

### Task 3: Update TypeMappings.json — type mapping

**Files:**
- Modify: `config/TypeMappings.json` (lines 1537-1543)

- [ ] **Step 1: Update the type mapping from java.util.Random to CSharpRandom**

Change lines 1537-1543 from:

```json
                        {
                            "csharp":  "System.Random",
                            "java":  "Random",
                            "imports":  [
                                            "java.util.Random"
                                        ]
                        },
```

To:

```json
                        {
                            "csharp":  "System.Random",
                            "java":  "CSharpRandom",
                            "imports":  [
                                            "io.github.ningpp.compat.CSharpRandom"
                                        ]
                        },
```

- [ ] **Step 2: Commit**

```bash
git add config/TypeMappings.json
git commit -m "fix: map System.Random to CSharpRandom instead of java.util.Random"
```

---

### Task 4: Update TypeMappings.json — method mappings

**Files:**
- Modify: `config/TypeMappings.json` (lines 3747-3771)

- [ ] **Step 1: Update method mappings for System.Random**

Change lines 3747-3771 from:

```json
                          {
                              "type":  "System.Random",
                              "method":  "Next",
                              "javaMethod":  "nextInt"
                          },
                          {
                              "type":  "System.Random",
                              "method":  "NextDouble",
                              "javaMethod":  "nextDouble"
                          },
                          {
                              "type":  "System.Random",
                              "method":  "NextSingle",
                              "javaMethod":  "nextFloat"
                          },
                          {
                              "type":  "System.Random",
                              "method":  "NextInt64",
                              "javaMethod":  "nextLong"
                          },
                          {
                              "type":  "System.Random",
                              "method":  "NextBytes",
                              "javaMethod":  "nextBytes"
                          },
```

To:

```json
                          {
                              "type":  "System.Random",
                              "method":  "Next",
                              "javaMethod":  "next"
                          },
                          {
                              "type":  "System.Random",
                              "method":  "NextDouble",
                              "javaMethod":  "nextDouble"
                          },
                          {
                              "type":  "System.Random",
                              "method":  "NextSingle",
                              "javaMethod":  "nextSingle"
                          },
                          {
                              "type":  "System.Random",
                              "method":  "NextInt64",
                              "javaMethod":  "nextInt64"
                          },
                          {
                              "type":  "System.Random",
                              "method":  "NextBytes",
                              "javaMethod":  "nextBytes"
                          },
```

- [ ] **Step 2: Commit**

```bash
git add config/TypeMappings.json
git commit -m "fix: update System.Random method mappings for CSharpRandom"
```

---

### Task 5: Full build and test verification

- [ ] **Step 1: Run full Maven build with tests**

Run: `cd d:\code\cs2j\java\csharptojava-compat; mvn verify -q`
Expected: BUILD SUCCESS

- [ ] **Step 2: Verify no regressions in existing tests**

Run: `cd d:\code\cs2j\java\csharptojava-compat; mvn test -q`
Expected: All tests pass (including existing tests)
