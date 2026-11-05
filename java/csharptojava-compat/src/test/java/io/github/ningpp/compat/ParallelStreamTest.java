package io.github.ningpp.compat;

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
}
