package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;
import java.util.function.BiConsumer;

/**
 * System.ComponentModel.BackgroundWorker compatibility class.
 */
public class BackgroundWorker {

    private final List<BiConsumer<Object, Object>> doWorkListeners = new ArrayList<>();
    private final List<BiConsumer<Object, RunWorkerCompletedEventArgs>> runWorkerCompletedListeners = new ArrayList<>();

    public void addDoWorkListener(BiConsumer<Object, Object> handler) {
        doWorkListeners.add(handler);
    }

    public void removeDoWorkListener(BiConsumer<Object, Object> handler) {
        doWorkListeners.remove(handler);
    }

    public void addRunWorkerCompletedListener(BiConsumer<Object, RunWorkerCompletedEventArgs> handler) {
        runWorkerCompletedListeners.add(handler);
    }

    public void removeRunWorkerCompletedListener(BiConsumer<Object, RunWorkerCompletedEventArgs> handler) {
        runWorkerCompletedListeners.remove(handler);
    }

    public void runWorkerAsync() {
        runWorkerAsync(null);
    }

    public void runWorkerAsync(Object argument) {
        for (BiConsumer<Object, Object> handler : new ArrayList<>(doWorkListeners)) {
            handler.accept(this, argument);
        }
        for (BiConsumer<Object, RunWorkerCompletedEventArgs> handler : new ArrayList<>(runWorkerCompletedListeners)) {
            handler.accept(this, new RunWorkerCompletedEventArgs());
        }
    }

    public void cancelAsync() {
    }

    public boolean getCancellationPending() {
        return false;
    }

    public void setWorkerSupportsCancellation(boolean value) {
    }

    public void setWorkerReportsProgress(boolean value) {
    }
}
