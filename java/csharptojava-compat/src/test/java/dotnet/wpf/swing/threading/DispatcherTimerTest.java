package dotnet.wpf.swing.threading;

import org.junit.jupiter.api.Test;

import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.*;

class DispatcherTimerTest {

    @Test
    void startStop_andTick() throws Exception {
        DispatcherTimer timer = new DispatcherTimer();
        AtomicInteger ticks = new AtomicInteger();
        timer.addTickListener((s, e) -> ticks.incrementAndGet());
        timer.setIntervalMilliseconds(20);
        timer.start();
        assertTrue(timer.getIsEnabled());
        Thread.sleep(90);
        timer.stop();
        assertFalse(timer.getIsEnabled());
        assertTrue(ticks.get() >= 1);
    }

    @Test
    void intervalFromTimeSpan() {
        DispatcherTimer timer = new DispatcherTimer();
        timer.setInterval(io.github.ningpp.compat.CSharpTimeSpan.fromMilliseconds(250));
        assertEquals(250, timer.getIntervalMilliseconds());
    }

    @Test
    void stop_noMoreTicks() throws Exception {
        DispatcherTimer timer = new DispatcherTimer();
        AtomicInteger ticks = new AtomicInteger();
        timer.addTickListener((s, e) -> ticks.incrementAndGet());
        timer.setIntervalMilliseconds(10);
        timer.start();
        Thread.sleep(60);
        timer.stop();
        int afterStop = ticks.get();
        Thread.sleep(60);
        assertEquals(afterStop, ticks.get());
    }
}
