package io.github.ningpp.compat;

/**
 * System.Threading.CancellationTokenSource compatibility class.
 */
public class CancellationTokenSource {

    private final CancellationToken token = new CancellationToken();

    public void cancel() {
    }

    public CancellationToken getToken() {
        return token;
    }

    public boolean isCancellationRequested() {
        return false;
    }

    public boolean getIsCancellationRequested() {
        return false;
    }
}
