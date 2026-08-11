package io.github.ningpp.compat;

/**
 * System.Threading.Tasks.TaskScheduler compatibility class.
 */
public class TaskScheduler {

    private static final TaskScheduler DEFAULT = new TaskScheduler();

    private TaskScheduler() {
    }

    public static TaskScheduler getDefault() {
        return DEFAULT;
    }
}
