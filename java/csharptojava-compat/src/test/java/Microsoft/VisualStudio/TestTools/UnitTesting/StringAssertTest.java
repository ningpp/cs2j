package Microsoft.VisualStudio.TestTools.UnitTesting;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertThrows;

class StringAssertTest {

    @Test
    void startsWith_MatchesPrefix() {
        StringAssert.startsWith("digraph \"noname\" {", "digraph");
    }

    @Test
    void endsWith_MatchesSuffix() {
        StringAssert.endsWith("abc}", "}");
    }

    @Test
    void contains_MatchesSubstring() {
        StringAssert.contains("label=\"myGraph\"", "myGraph");
    }

    @Test
    void startsWith_FailsOnMismatch() {
        assertThrows(AssertionError.class, () -> StringAssert.startsWith("abc", "z"));
    }

    @Test
    void contains_FailsOnMissingSubstring() {
        assertThrows(AssertionError.class, () -> StringAssert.contains("abc", "z"));
    }
}
