package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ExceptionCompatTest {

    @Test
    void getStackTraceReturnsFormattedStringWithAtLeastOneFrame() {
        RuntimeException ex = new RuntimeException("test");
        String trace = ExceptionCompat.getStackTrace(ex);

        assertNotNull(trace);
        assertTrue(trace.contains("   at "), "stack trace should contain .NET-style '   at ' prefix");
        assertTrue(trace.contains(ExceptionCompatTest.class.getName()), "stack trace should contain current class");
    }
}
