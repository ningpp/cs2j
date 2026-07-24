package io.github.ningpp.compat;

/**
 * Compatibility stub for System.Text.CodePages.CodePagesEncodingProvider.
 *
 * <p>In .NET this provider supplies encodings for legacy code pages. Java's
 * Charset mechanism handles these encodings natively, so this class is a
 * singleton no-op placeholder. The typical usage in converted code is:
 *
 * <pre>{@code Encoding.registerProvider(CodePagesEncodingProvider.getInstance());}</pre>
 *
 * which delegates to {@link Encoding#registerProvider(Object)} and is also a no-op.
 */
public class CodePagesEncodingProvider {

    private static final CodePagesEncodingProvider INSTANCE = new CodePagesEncodingProvider();

    private CodePagesEncodingProvider() {
    }

    public static CodePagesEncodingProvider getInstance() {
        return INSTANCE;
    }
}
