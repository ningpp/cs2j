package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

public class InterlockedTest {
    @Test
    public void increment_IntHolder_incrementsValue() {
        IntHolder holder = new IntHolder(5);
        int result = Interlocked.increment(holder);
        assertEquals(6, result);
        assertEquals(6, holder.value);
    }

    @Test
    public void decrement_IntHolder_decrementsValue() {
        IntHolder holder = new IntHolder(5);
        int result = Interlocked.decrement(holder);
        assertEquals(4, result);
        assertEquals(4, holder.value);
    }
}
