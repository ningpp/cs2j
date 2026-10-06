package io.github.ningpp.compat;

import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTimeoutPreemptively;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static java.time.Duration.ofSeconds;

class AsyncTasksTest {

    @Test
    void delayCompletesAfterRequestedMillis() {
        assertTimeoutPreemptively(ofSeconds(5), () -> {
            long start = System.nanoTime();
            assertNull(AsyncTasks.delay(30L).join());
            long elapsedMillis = (System.nanoTime() - start) / 1_000_000;
            assertTrue(elapsedMillis >= 20, "delay returned too early: " + elapsedMillis + "ms");
        });
    }

    @Test
    void delayWithTimespanOverloadUsesTotalMilliseconds() throws Exception {
        assertTimeoutPreemptively(ofSeconds(5), () ->
            assertNull(AsyncTasks.delay(CSharpTimeSpan.fromMilliseconds(20)).join()));
    }

    @Test
    void delayRejectsValuesBelowMinusOne() {
        assertThrows(ArgumentOutOfRangeException.class, () -> AsyncTasks.delay(-2L));
    }

    @Test
    void delayWithNoneTokenIsNotCancelled() throws Exception {
        CompletableFuture<Void> future = AsyncTasks.delay(20L, CancellationToken.getNone());
        assertNull(future.join());
        assertFalse(future.isCompletedExceptionally());
    }

    @Test
    void delayWithCancelledTokenCompletesExceptionally() {
        CancellationTokenSource source = new CancellationTokenSource();
        source.cancel();
        CompletableFuture<Void> future = AsyncTasks.delay(60_000L, source.getToken());
        assertThrows(CancellationException.class, future::join);
        assertTrue(future.isCompletedExceptionally());
    }

    @Test
    void delayCancellationDuringWaitAbortsFutureAndDetachesCallback() throws Exception {
        CancellationTokenSource source = new CancellationTokenSource();
        CompletableFuture<Void> future = AsyncTasks.delay(60_000L, source.getToken());
        assertFalse(future.isDone());

        source.cancel();
        assertThrows(CancellationException.class, future::join);

        // After settlement the registration must be detached: a second cancel must not
        // touch the (already completed) future — no exception leaking here either.
        source.cancel();
        assertTrue(future.isCompletedExceptionally());
    }

    @Test
    void infiniteDelayStaysPendingUntilCancelled() throws Exception {
        CancellationTokenSource source = new CancellationTokenSource();
        CompletableFuture<Void> future = AsyncTasks.delay(-1L, source.getToken());
        Thread.sleep(50);
        assertFalse(future.isDone());

        source.cancel();
        assertThrows(CancellationException.class, future::join);
    }
}
