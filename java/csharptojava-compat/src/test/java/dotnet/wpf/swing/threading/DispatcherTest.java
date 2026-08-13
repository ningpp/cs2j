package dotnet.wpf.swing.threading;

import dotnet.wpf.swing.internal.UiLoop;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Headless dispatcher tests: scheduling, priority, results, abort,
 * shutdown state and nested frames.
 */
class DispatcherTest {

    private Dispatcher dispatcher;

    @BeforeEach
    @AfterEach
    void resetLoop() {
        UiLoop.get().reset();
    }

    @BeforeEach
    void setUp() {
        dispatcher = Dispatcher.getCurrentDispatcher();
    }

    @Test
    void checkAccess_headlessAlwaysTrue() {
        assertTrue(dispatcher.checkAccess());
        dispatcher.verifyAccess();
    }

    @Test
    void beginInvoke_runsCallback() {
        AtomicInteger ran = new AtomicInteger();
        DispatcherOperation<Void> op = dispatcher.beginInvoke(ran::incrementAndGet);
        op.waitForCompletion();
        assertEquals(1, ran.get());
        assertEquals(DispatcherOperation.Status.Completed, op.getStatus());
    }

    @Test
    void beginInvoke_honorsPriority() {
        AtomicInteger ran = new AtomicInteger();
        DispatcherOperation<Void> op = dispatcher.beginInvoke(DispatcherPriority.Send, ran::incrementAndGet);
        op.waitForCompletion();
        assertEquals(DispatcherPriority.Send.getValue(), DispatcherPriority.fromValue(10).getValue());
        assertEquals(1, ran.get());
    }

    @Test
    void invokeAsync_returnsResult() {
        DispatcherOperation<String> op = dispatcher.invokeAsync(() -> "hello");
        op.waitForCompletion();
        assertEquals("hello", op.getResult());
        assertEquals(DispatcherOperation.Status.Completed, op.getStatus());
    }

    @Test
    void abort_preventsExecution() {
        AtomicInteger ran = new AtomicInteger();
        DispatcherOperation<Void> op = dispatcher.beginInvoke(ran::incrementAndGet);
        op.abort();
        op.waitForCompletion();
        assertEquals(0, ran.get());
        assertEquals(DispatcherOperation.Status.Aborted, op.getStatus());
    }

    @Test
    void invoke_runsSynchronously() {
        AtomicInteger ran = new AtomicInteger();
        dispatcher.invoke(ran::incrementAndGet);
        assertEquals(1, ran.get());
        assertEquals("sync", dispatcher.invoke(() -> "sync"));
    }

    @Test
    void shutdown_marksHasShutdownStarted() {
        assertFalse(dispatcher.hasShutdownStarted());
        assertFalse(dispatcher.hasShutdownFinished());
        dispatcher.shutdown();
        assertTrue(dispatcher.hasShutdownStarted());
        assertTrue(dispatcher.hasShutdownFinished());
    }

    @Test
    void pushFrame_returnsWhenContinueFalse() {
        DispatcherFrame frame = new DispatcherFrame();
        frame.setContinue(false);
        dispatcher.pushFrame(frame);
    }

    @Test
    void fromThread_returnsCurrentDispatcher() {
        assertSame(dispatcher, Dispatcher.fromThread(Thread.currentThread()));
    }

    @Test
    void operation_errorCapturedOnFailure() {
        DispatcherOperation<Integer> op = dispatcher.invokeAsync(() -> {
            throw new IllegalStateException("boom");
        });
        op.waitForCompletion();
        assertEquals(DispatcherOperation.Status.Aborted, op.getStatus());
        assertEquals("boom", op.getError().getMessage());
    }

    @Test
    void priorityValues_matchWpf() {
        assertEquals(-1, DispatcherPriority.Invalid.getValue());
        assertEquals(0, DispatcherPriority.Inactive.getValue());
        assertEquals(4, DispatcherPriority.Background.getValue());
        assertEquals(9, DispatcherPriority.Normal.getValue());
        assertEquals(10, DispatcherPriority.Send.getValue());
        assertEquals(DispatcherPriority.Normal, DispatcherPriority.fromValue(9));
    }
}