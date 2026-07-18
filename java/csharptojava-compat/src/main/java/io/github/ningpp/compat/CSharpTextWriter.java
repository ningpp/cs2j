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

    public CSharpTextWriter() {
        super(new StringWriter());
    }

    public CSharpTextWriter(Writer writer) {
        super(writer);
    }

    public CSharpTextWriter(Writer writer, boolean autoFlush) {
        super(writer, autoFlush);
    }

    public CSharpTextWriter(OutputStream out) {
        super(out);
    }

    public CSharpTextWriter(OutputStream out, boolean autoFlush) {
        super(out, autoFlush);
    }

    public CSharpTextWriter(OutputStream out, boolean autoFlush, Charset charset) {
        super(out, autoFlush, charset);
    }

    public CSharpTextWriter(String fileName) throws FileNotFoundException {
        super(fileName);
    }

    public CSharpTextWriter(String fileName, String csn) throws FileNotFoundException, UnsupportedEncodingException {
        super(fileName, csn);
    }

    public CSharpTextWriter(String fileName, Charset charset) throws IOException {
        super(fileName, charset);
    }

    public CSharpTextWriter(File file) throws FileNotFoundException {
        super(file);
    }

    public CSharpTextWriter(File file, String csn) throws FileNotFoundException, UnsupportedEncodingException {
        super(file, csn);
    }

    public CSharpTextWriter(File file, Charset charset) throws IOException {
        super(file, charset);
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
}
