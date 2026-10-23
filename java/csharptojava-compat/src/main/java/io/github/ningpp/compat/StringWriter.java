package io.github.ningpp.compat;

import java.io.IOException;
import java.io.Writer;

/**
 * A {@link Writer} that writes to a provided {@link StringBuilder},
 * mirroring the behaviour of C#'s {@code System.IO.StringWriter(StringBuilder)}.
 * <p>
 * Unlike {@link java.io.StringWriter}, which uses an internal buffer,
 * this class delegates all writes to the caller-supplied {@link StringBuilder}
 * so that {@code sb.toString()} returns the accumulated output.
 */
public class StringWriter extends Writer {

    private final StringBuilder sb;

    public StringWriter() {
        this.sb = new StringBuilder();
        lock = this.sb;
    }

    public StringWriter(StringBuilder sb) {
        if (sb == null) {
            throw new NullPointerException("sb");
        }
        this.sb = sb;
        lock = this.sb;
    }

    /**
     * Matches C# System.IO.StringWriter(IFormatProvider): the format provider
     * is irrelevant to the accumulated text, only the constructor shape must
     * exist so converted call sites bind.
     */
    public StringWriter(CultureInfo cultureInfo) {
        this();
    }

    @Override
    public void write(char[] cbuf, int off, int len) throws IOException {
        sb.append(cbuf, off, len);
    }

    @Override
    public void write(int c) throws IOException {
        sb.append((char) c);
    }

    @Override
    public void write(String str) throws IOException {
        sb.append(str);
    }

    @Override
    public void write(String str, int off, int len) throws IOException {
        sb.append(str, off, off + len);
    }

    @Override
    public Writer append(CharSequence csq) throws IOException {
        if (csq == null) {
            sb.append("null");
        } else {
            sb.append(csq);
        }
        return this;
    }

    @Override
    public Writer append(CharSequence csq, int start, int end) throws IOException {
        if (csq == null) {
            sb.append("null", start, end);
        } else {
            sb.append(csq, start, end);
        }
        return this;
    }

    @Override
    public Writer append(char c) throws IOException {
        sb.append(c);
        return this;
    }

    @Override
    public void flush() {
        // no-op
    }

    @Override
    public void close() {
        // no-op
    }

    @Override
    public String toString() {
        return sb.toString();
    }

    public StringBuilder getStringBuilder() {
        return sb;
    }
}
