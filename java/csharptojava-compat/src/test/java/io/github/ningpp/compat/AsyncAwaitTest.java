package io.github.ningpp.compat;

import java.io.IOException;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;

class AsyncAwaitTest {

    @Test
    void joinReturnsCompletedValue() {
        CompletableFuture<Integer> future = CompletableFuture.completedFuture(42);
        assertEquals(Integer.valueOf(42), AsyncAwait.join(future));
    }

    @Test
    void joinUnwrapsCompletionExceptionToOriginalCause() {
        IOException failure = new IOException("boom");
        CompletableFuture<Integer> future = new CompletableFuture<>();
        future.completeExceptionally(failure);

        IOException caught = assertThrows(IOException.class, () -> AsyncAwait.join(future));
        assertSame(failure, caught);
    }

    @Test
    void joinSurfacesCancellationAsCancellationException() {
        CompletableFuture<Integer> future = new CompletableFuture<>();
        future.cancel(false);
        assertThrows(CancellationException.class, () -> AsyncAwait.join(future));
    }

    @Test
    void joinRethrowsRuntimeFailureOfAsyncTasksDelayChain() {
        CancellationTokenSource source = new CancellationTokenSource();
        source.cancel();
        CompletableFuture<Void> delayed = AsyncTasks.delay(1000L, source.getToken());
        assertThrows(CancellationException.class, () -> AsyncAwait.join(delayed));
    }
}
