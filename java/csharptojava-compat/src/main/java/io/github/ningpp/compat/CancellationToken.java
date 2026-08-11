package io.github.ningpp.compat;

/**
 * System.Threading.CancellationToken compatibility class.
 */
public class CancellationToken {

    public boolean isCancellationRequested() {
        return false;
    }

    public boolean getIsCancellationRequested() {
        return isCancellationRequested();
    }
}
