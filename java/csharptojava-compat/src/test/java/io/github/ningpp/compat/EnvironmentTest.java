package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class EnvironmentTest {

    @Test
    void newLineMatchesSystemLineSeparator() {
        assertEquals(System.lineSeparator(), Environment.getNewLine());
    }

    @Test
    void currentManagedThreadIdIsPositive() {
        assertTrue(Environment.getCurrentManagedThreadId() > 0);
    }

    @Test
    void processorCountIsPositive() {
        assertTrue(Environment.getProcessorCount() > 0);
    }

    @Test
    void currentDirectoryIsNotNull() {
        assertNotNull(Environment.getCurrentDirectory());
    }
}
