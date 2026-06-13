package io.github.ningpp.compat;

import java.io.PrintWriter;
import java.io.Writer;

/**
 * Compat class for System.IO.TextWriter.
 * Extends PrintWriter (since .NET TextWriter ≈ Java PrintWriter)
 * and adds getEncoding() support.
 */
public abstract class TextWriter extends PrintWriter {
    protected Encoding encoding;

    protected TextWriter() {
        super(Writer.nullWriter());
    }

    protected TextWriter(Writer out) {
        super(out);
    }

    /** Mirrors C# TextWriter.Encoding property */
    public Encoding getEncoding() {
        return encoding != null ? encoding : Encoding.getDefault();
    }

    /** Set the encoding (called by subclasses like StreamWriter) */
    protected void setEncoding(Encoding enc) {
        this.encoding = enc;
    }
}
