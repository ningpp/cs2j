package io.github.ningpp.compat;

import java.io.Writer;

/**
 * Compatibility replacement for System.IO.StringWriter.
 * <p>
 * Mirrors the C# hierarchy {@code StringWriter : TextWriter} by extending
 * {@link CSharpTextWriter} (real chain: {@code new XmlTextWriter(TextWriter)}
 * receiving a StringWriter — a bare {@code Writer} subtype cannot bind to the
 * CSharpTextWriter parameter). All writes accumulate into the caller-supplied
 * {@link StringBuilder} so {@code sb.toString()} returns the accumulated
 * output, unlike {@link java.io.StringWriter} which uses an internal buffer.
 */
public class StringWriter extends CSharpTextWriter {

    private final StringBuilder sb;

    public StringWriter() {
        this(new StringBuilder());
    }

    public StringWriter(StringBuilder sb) {
        super(writerFor(sb));
        this.sb = sb;
    }

    /**
     * Matches C# System.IO.StringWriter(IFormatProvider): the format provider
     * is irrelevant to the accumulated text, only the constructor shape must
     * exist so converted call sites bind.
     */
    public StringWriter(CultureInfo cultureInfo) {
        this();
    }

    /**
     * Matches C# System.IO.StringWriter(StringBuilder, IFormatProvider): the
     * format provider is irrelevant to the accumulated text, only the
     * constructor shape must exist so converted call sites bind.
     */
    public StringWriter(StringBuilder sb, CultureInfo cultureInfo) {
        this(sb);
    }

    @Override
    public String toString() {
        return sb.toString();
    }

    public StringBuilder getStringBuilder() {
        return sb;
    }

    /// <summary>A Writer view that appends into the caller's StringBuilder — PrintWriter's
    /// print/println/write/append all delegate here, so every write lands in sb.</summary>
    private static Writer writerFor(StringBuilder sb) {
        return new Writer() {
            @Override
            public void write(char[] cbuf, int off, int len) {
                sb.append(cbuf, off, len);
            }

            @Override
            public void write(int c) {
                sb.append((char) c);
            }

            @Override
            public void write(String str) {
                sb.append(str);
            }

            @Override
            public Writer append(CharSequence csq) {
                sb.append(csq == null ? "null" : csq);
                return this;
            }

            @Override
            public Writer append(CharSequence csq, int start, int end) {
                sb.append(csq == null ? "null" : csq, start, end);
                return this;
            }

            @Override
            public Writer append(char c) {
                sb.append(c);
                return this;
            }

            @Override
            public void flush() {
                // no-op: StringBuilder needs no flushing
            }

            @Override
            public void close() {
                // no-op: C# StringWriter.Close is a no-op for the buffer
            }
        };
    }
}
