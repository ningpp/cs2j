package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;

class ParallelStreamTest {

    @Test
    void forEach_twoArgOverload_visitsEveryElement() {
        AtomicInteger sum = new AtomicInteger();
        ParallelStream.forEach(List.of(1, 2, 3), sum::addAndGet);
        assertEquals(6, sum.get());
    }

    @Test
    void invoke_runsEveryActionInOrder() {
        List<String> executed = new ArrayList<>();
        ParallelStream.invoke(() -> executed.add("first"),
                              () -> executed.add("second"),
                              () -> executed.add("third"));
        assertEquals(List.of("first", "second", "third"), executed);
    }

    @Test
    void invoke_nullOrEmptyArray_doesNothing() {
        ParallelStream.invoke((Runnable[]) null);
        ParallelStream.invoke();
    }
}
