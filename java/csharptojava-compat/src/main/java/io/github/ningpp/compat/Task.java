package io.github.ningpp.compat;

import java.util.function.Consumer;

/**
 * System.Threading.Tasks.Task compatibility class (synchronous, Swing-friendly).
 */
public class Task {

    private static final TaskFactory FACTORY = new TaskFactory();

    private final Runnable action;

    public Task() {
        this(null);
    }

    public Task(Runnable action) {
        this(action, null);
    }

    public Task(Runnable action, CancellationToken cancellationToken) {
        this.action = action;
    }

    public static TaskFactory getFactory() {
        return FACTORY;
    }

    public void start() {
        if (action != null) {
            action.run();
        }
    }

    public <T> Task thenApply(Consumer<T> continuation, int continuationOptions) {
        if (continuation != null) {
            continuation.accept((T) this);
        }
        return this;
    }

    public boolean isCompleted() {
        return true;
    }

    public boolean getIsCompleted() {
        return true;
    }

    public Exception getException() {
        return null;
    }

    public void waitTask() {
    }

    public void dispose() {
    }
}
