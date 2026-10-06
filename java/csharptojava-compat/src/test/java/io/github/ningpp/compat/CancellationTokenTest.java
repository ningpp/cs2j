package io.github.ningpp.compat;

import java.util.concurrent.atomic.AtomicInteger;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class CancellationTokenTest {

    @Test
    void noneTokenNeverCancelsAndRegistrationStaysInert() {
        CancellationToken none = CancellationToken.getNone();
        assertFalse(none.isCancellationRequested());

        AtomicInteger calls = new AtomicInteger();
        // None is a shared singleton produced by a source-less token; dispose must be safe.
        CancellationTokenRegistration registration = none.register(calls::incrementAndGet);
        registration.close();
        assertEquals(0, calls.get());
        assertFalse(none.isCancellationRequested());
    }

    @Test
    void registerBeforeCancelFiresOnceInOrder() {
        CancellationTokenSource source = new CancellationTokenSource();
        CancellationToken token = source.getToken();

        StringBuilder order = new StringBuilder();
        token.register(() -> order.append("a"));
        token.register(() -> order.append("b"));

        assertFalse(token.isCancellationRequested());
        source.cancel();
        assertTrue(token.isCancellationRequested());
        assertEquals("ab", order.toString());

        // Second cancel is a no-op: callbacks must not fire again.
        source.cancel();
        assertEquals("ab", order.toString());
    }

    @Test
    void registerAfterCancelRunsImmediately() {
        CancellationTokenSource source = new CancellationTokenSource();
        source.cancel();

        AtomicInteger calls = new AtomicInteger();
        source.getToken().register(calls::incrementAndGet);
        assertEquals(1, calls.get());
    }

    @Test
    void disposeBeforeCancelDetachesCallback() {
        CancellationTokenSource source = new CancellationTokenSource();
        CancellationToken token = source.getToken();

        AtomicInteger calls = new AtomicInteger();
        CancellationTokenRegistration registration = token.register(calls::incrementAndGet);
        registration.close();
        source.cancel();
        assertEquals(0, calls.get());
    }

    @Test
    void registerRejectsNullCallback() {
        assertThrows(NullPointerException.class,
            () -> new CancellationTokenSource().getToken().register(null));
    }

    @Test
    void cancelAfterFiresWithinDelayWindow() throws Exception {
        CancellationTokenSource source = new CancellationTokenSource();
        source.cancelAfter(30);

        long deadline = System.nanoTime() + 5_000_000_000L;
        while (!source.isCancellationRequested() && System.nanoTime() < deadline) {
            Thread.sleep(5);
        }
        assertTrue(source.isCancellationRequested(), "token should be cancelled by cancelAfter");
    }
}
