package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Java half of the C38 async-streams bridge contract. The enumeration loops
 * below are written in exactly the shape the CSharpToJava converter emits for
 * an expanded {@code await foreach} (C18 async-normalize pass + C38 §5
 * conversion): {@code getAsyncEnumerator(CancellationToken.getNone())}, then
 * {@code while (enumerator.moveNextAsync().join())} with
 * {@code enumerator.getCurrent()} in the body, and
 * {@code enumerator.disposeAsync().join();} in a plain try/finally.
 *
 * <p>The exception-masking test mirrors the measured .NET 10 truth pinned by
 * {@code AsyncStreamTruthTests.T4_InFlightDoubleFault_DisposeExceptionMasksBody_AndDisposeRan}
 * in SampleAsyncSolution: under ordinary try/finally lowering, a fault from
 * {@code DisposeAsync} masks the in-flight {@code MoveNextAsync} fault, and
 * disposal still ran exactly once. Every future is already completed, so
 * {@code join()} never blocks and the test executes synchronously.</p>
 */
class AsyncStreamsBridgeTest {

    @Test
    void joinDrivenLoopConsumesElementsInOrderAndDisposesOnce() {
        AsyncEnumerable<Integer> source = cancellationToken -> new RangeEnumerator(1, 3);

        List<Integer> seen = new ArrayList<>();
        int sum = 0;
        RangeEnumerator enumerator = (RangeEnumerator)
                source.getAsyncEnumerator(CancellationToken.getNone());
        try {
            while (enumerator.moveNextAsync().join()) {
                seen.add(enumerator.getCurrent());
                sum += enumerator.getCurrent();
            }
        } finally {
            enumerator.disposeAsync().join();
        }

        assertEquals(List.of(1, 2, 3), seen);
        assertEquals(6, sum);
        assertEquals(1, enumerator.disposeCount);
    }

    @Test
    void disposeFaultMasksInFlightMoveNextFaultLikeNetT4() {
        // Mirrors .NET truth test T4: the third MoveNextAsync hop fails with an
        // in-flight exception, then DisposeAsync fails in finally. The plain
        // try/finally surface exposes the dispose-side exception.
        AsyncEnumerable<Integer> source = cancellationToken -> new FailingEnumerator();

        FailingEnumerator enumerator = (FailingEnumerator)
                source.getAsyncEnumerator(CancellationToken.getNone());

        CompletionException caught;
        try {
            // Exact converted shape: the enumeration loop is the try block and
            // dispose runs in an ordinary finally (no catch around moveNext).
            try {
                while (enumerator.moveNextAsync().join()) {
                    enumerator.getCurrent();
                }
                throw new AssertionError("expected the in-flight MoveNextAsync failure");
            } finally {
                enumerator.disposeAsync().join();
            }
        } catch (CompletionException e) {
            caught = e;
        }

        // The finally must still run exactly once, and its failure replaces the
        // in-flight IllegalStateException (plain finally masking, net10 T4).
        assertEquals(1, enumerator.disposeCount);
        RuntimeException cause = assertInstanceOf(RuntimeException.class, caught.getCause());
        assertEquals("dispose", cause.getMessage());
        assertTrue(!(cause instanceof IllegalStateException),
                "the in-flight MoveNextAsync fault must be masked by the dispose fault");
    }

    /** Enumerates {@code [start, end]} inclusive via already-completed futures. */
    private static final class RangeEnumerator implements AsyncEnumerator<Integer> {

        private final int end;
        private int current;
        private int disposeCount;

        RangeEnumerator(int start, int end) {
            this.current = start - 1;
            this.end = end;
        }

        @Override
        public CompletableFuture<Boolean> moveNextAsync() {
            return CompletableFuture.completedFuture(++current <= end);
        }

        @Override
        public Integer getCurrent() {
            return current;
        }

        @Override
        public CompletableFuture<Void> disposeAsync() {
            disposeCount++;
            return CompletableFuture.completedFuture(null);
        }
    }

    /**
     * Yields one element, then faults the third MoveNextAsync hop
     * (in-flight) and always faults DisposeAsync.
     */
    private static final class FailingEnumerator implements AsyncEnumerator<Integer> {

        private int moveNextCalls;
        private int disposeCount;

        @Override
        public CompletableFuture<Boolean> moveNextAsync() {
            moveNextCalls++;
            if (moveNextCalls < 3) {
                return CompletableFuture.completedFuture(Boolean.TRUE);
            }
            return CompletableFuture.failedFuture(new IllegalStateException("in-flight"));
        }

        @Override
        public Integer getCurrent() {
            return moveNextCalls;
        }

        @Override
        public CompletableFuture<Void> disposeAsync() {
            disposeCount++;
            return CompletableFuture.failedFuture(new RuntimeException("dispose"));
        }
    }
}
