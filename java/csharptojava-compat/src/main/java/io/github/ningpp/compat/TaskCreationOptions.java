package io.github.ningpp.compat;

/**
 * System.Threading.Tasks.TaskCreationOptions compatibility flags (int constants).
 */
public final class TaskCreationOptions {

    public static final int None = 0;
    public static final int PreferFairness = 1;
    public static final int LongRunning = 2;
    public static final int AttachedToParent = 4;
    public static final int DenyChildAttach = 8;
    public static final int HideScheduler = 16;
    public static final int RunContinuationsAsynchronously = 64;

    private TaskCreationOptions() {
    }
}
