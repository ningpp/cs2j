package io.github.ningpp.compat;

/**
 * System.Threading.CancellationToken compatibility class.
 */
public class CancellationToken {

    /** The shared token that can never be cancelled (C# CancellationToken.None). */
    private static final CancellationToken NONE = new CancellationToken();

    /**
     * C# CancellationToken.None — a token that can never be cancelled.
     *
     * @return The shared non-cancellable token.
     */
    public static CancellationToken getNone() {
        return NONE;
    }

    public boolean isCancellationRequested() {
        return false;
    }

    public boolean getIsCancellationRequested() {
        return isCancellationRequested();
    }
}
