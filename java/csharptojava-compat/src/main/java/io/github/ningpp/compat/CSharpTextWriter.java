package io.github.ningpp.compat;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.OutputStream;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.io.UnsupportedEncodingException;
import java.io.Writer;
import java.nio.charset.Charset;

/**
 * Compatibility replacement for System.IO.TextWriter.
 * Extends PrintWriter and provides a parameterless constructor
 * plus C# TextWriter.NewLine semantics.
 */
public class CSharpTextWriter extends PrintWriter {
    private String newLine = "\r\n";
    private final Writer innerWriter;

    public CSharpTextWriter() {
        super(new StringWriter());
        this.innerWriter = null; // can't recover the default StringWriter
    }

    public CSharpTextWriter(Writer writer) {
        super(writer);
        this.innerWriter = writer;
    }

    public CSharpTextWriter(Writer writer, boolean autoFlush) {
        super(writer, autoFlush);
        this.innerWriter = writer;
    }

    public CSharpTextWriter(OutputStream out) {
        super(out);
        this.innerWriter = null;
    }

    public CSharpTextWriter(OutputStream out, boolean autoFlush) {
        super(out, autoFlush);
        this.innerWriter = null;
    }

    public CSharpTextWriter(OutputStream out, boolean autoFlush, Charset charset) {
        super(out, autoFlush, charset);
        this.innerWriter = null;
    }

    public CSharpTextWriter(String fileName) throws FileNotFoundException {
        super(fileName);
        this.innerWriter = null;
    }

    public CSharpTextWriter(String fileName, String csn) throws FileNotFoundException, UnsupportedEncodingException {
        super(fileName, csn);
        this.innerWriter = null;
    }

    public CSharpTextWriter(String fileName, Charset charset) throws IOException {
        super(fileName, charset);
        this.innerWriter = null;
    }

    public CSharpTextWriter(File file) throws FileNotFoundException {
        super(file);
        this.innerWriter = null;
    }

    public CSharpTextWriter(File file, String csn) throws FileNotFoundException, UnsupportedEncodingException {
        super(file, csn);
        this.innerWriter = null;
    }

    public CSharpTextWriter(File file, Charset charset) throws IOException {
        super(file, charset);
        this.innerWriter = null;
    }

    public String getNewLine() {
        return newLine;
    }

    public void setNewLine(String newLine) {
        this.newLine = newLine;
    }

    @Override
    public void println() {
        print(newLine);
    }

    /**
     * C# TextWriter.ToString() returns the buffered content when the underlying
     * writer is a StringWriter.  Mirror that behaviour so test helpers that call
     * {@code stringWriter.ToString()} get the JSON text rather than the default
     * Java object representation.
     */
    @Override
    public String toString() {
        if (innerWriter != null) {
            return innerWriter.toString();
        }
        return super.toString();
    }
}
