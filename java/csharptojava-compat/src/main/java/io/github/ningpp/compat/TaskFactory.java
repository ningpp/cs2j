package io.github.ningpp.compat;

/**
 * System.Threading.Tasks.TaskFactory compatibility class (synchronous execution).
 */
public class TaskFactory {

    public Task startNew(Runnable action) {
        if (action != null) {
            action.run();
        }
        return new Task();
    }

    public Task startNew(Runnable action, CancellationToken cancellationToken) {
        return startNew(action);
    }

    public Task startNew(Runnable action, CancellationToken cancellationToken,
                         int creationOptions, TaskScheduler scheduler) {
        return startNew(action);
    }
}
