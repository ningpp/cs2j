package io.github.ningpp.compat;

import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.OutputStream;
import java.io.OutputStreamWriter;
import java.io.UncheckedIOException;
import java.io.Writer;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;

/** Replacement for System.IO.StreamWriter. */
public class StreamWriter extends CSharpTextWriter {
    private final StreamWrapper baseStream;
    private final Charset charset;

    public StreamWriter(StreamWrapper stream) {
        this(stream, StandardCharsets.UTF_8);
    }

    public StreamWriter(StreamWrapper stream, Charset charset) {
        super(new OutputStreamWriter(stream.outputStream(), charset));
        this.baseStream = stream;
        this.charset = charset;
        // encoding stored for getEncoding()
    }

    public StreamWriter(StreamWrapper stream, Encoding encoding) {
        this(stream, encoding.toCharset());
    }

    public StreamWriter(OutputStream stream) {
        this(StreamWrapper.of(stream));
    }

    public StreamWriter(OutputStream stream, Charset charset) {
        this(StreamWrapper.of(stream), charset);
    }

    public StreamWriter(Writer writer) {
        super(writer);
        this.baseStream = null;
        this.charset = StandardCharsets.UTF_8;
        // encoding stored for getEncoding()
    }

    public StreamWriter(String path) {
        this(open(path, false));
    }

    public StreamWriter(String path, boolean append) {
        this(open(path, append));
    }

    public StreamWriter(String path, boolean append, Charset charset) {
        this(open(path, append), charset);
    }

    /** Mirrors C# StreamWriter.Encoding property */
    public Encoding getEncoding() {
        return Encoding.getEncoding(charset);
    }

    public StreamWrapper getBaseStream() {
        flush();
        if (baseStream == null)
            throw new IllegalStateException("This StreamWriter was not created from a stream");
        return baseStream;
    }

    private static StreamWrapper open(String path, boolean append) {
        try {
            return StreamWrapper.of(new FileOutputStream(path, append));
        } catch (FileNotFoundException e) {
            throw new UncheckedIOException(e);
        }
    }
}
