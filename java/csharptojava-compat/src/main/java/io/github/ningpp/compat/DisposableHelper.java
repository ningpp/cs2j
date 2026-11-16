package io.github.ningpp.compat;

/**
 * System.IDisposable → java.lang.AutoCloseable bridge.
 *
 * <p>C# declares {@code void Dispose()} with no checked exception, while
 * {@link AutoCloseable#close()} advertises {@code throws Exception}. Call sites
 * whose receiver static type is exactly {@code System.IDisposable} (e.g.
 * {@code IDisposable reader = stream as IDisposable; reader.Dispose();}) cannot
 * narrow that signature the way a concrete converted class does, so they are
 * emitted against this unchecked helper instead.
 */
public final class DisposableHelper {
    private DisposableHelper() {
    }

    public static void dispose(AutoCloseable resource) {
        if (resource == null) {
            return;
        }
        try {
            resource.close();
        } catch (RuntimeException e) {
            throw e;
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }
}
