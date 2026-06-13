package io.github.ningpp.compat;

import java.io.PrintWriter;

/**
 * Helper for C# TextWriter properties that don't exist on Java's PrintWriter.
 */
public final class TextWriterHelper {
    private TextWriterHelper() {
    }

    /**
     * Mirrors C# TextWriter.Encoding.
     * Returns the encoding of a StreamWriter, or UTF-8 as default for
     * plain PrintWriter instances.
     */
    public static Encoding getEncoding(PrintWriter w) {
        if (w instanceof StreamWriter sw) {
            return sw.getEncoding();
        }
        return Encoding.getDefault();
    }
}
