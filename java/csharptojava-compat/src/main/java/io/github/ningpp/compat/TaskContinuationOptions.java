package io.github.ningpp.compat;

/**
 * System.Threading.Tasks.TaskContinuationOptions compatibility flags (int constants).
 */
public final class TaskContinuationOptions {

    public static final int None = 0;
    public static final int PreferFairness = 1;
    public static final int LongRunning = 2;
    public static final int AttachedToParent = 4;
    public static final int DenyChildAttach = 8;
    public static final int HideScheduler = 16;
    public static final int LazyCancellation = 32;
    public static final int RunContinuationsAsynchronously = 64;
    public static final int NotOnRanToCompletion = 65536;
    public static final int NotOnFaulted = 131072;
    public static final int OnlyOnCanceled = 196608;
    public static final int NotOnCanceled = 262144;
    public static final int OnlyOnFaulted = 327680;
    public static final int OnlyOnRanToCompletion = 393216;

    private TaskContinuationOptions() {
    }
}
