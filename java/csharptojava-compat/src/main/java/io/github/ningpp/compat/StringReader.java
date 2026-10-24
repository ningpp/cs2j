package io.github.ningpp.compat;

import java.io.Reader;

/**
 * Compatibility replacement for System.IO.StringReader.
 * <p>
 * Mirrors the C# hierarchy {@code StringReader : TextReader} so instances bind
 * to {@link TextReader} parameters (real chain: XmlTextReader / TextReader-typed
 * returns receiving {@code new StringReader(...)} — the JDK StringReader is not
 * a compat TextReader). All reads delegate to a {@link java.io.StringReader}
 * over the source string.
 */
public class StringReader extends TextReader {

    public StringReader(String s) {
        super(new java.io.StringReader(s == null ? "" : s));
    }
}
