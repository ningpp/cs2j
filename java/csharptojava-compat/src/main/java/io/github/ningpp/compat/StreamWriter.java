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
    private final boolean leaveOpen;

    public StreamWriter(StreamWrapper stream) {
        this(stream, StandardCharsets.UTF_8);
    }

    public StreamWriter(StreamWrapper stream, Charset charset) {
        this(stream, charset, false);
    }

    private StreamWriter(StreamWrapper stream, Charset charset, boolean leaveOpen) {
        super(newWriter(stream.outputStream(), charset));
        this.baseStream = stream;
        this.charset = charset;
        this.leaveOpen = leaveOpen;
    }

    /**
     * A UTF-8 (or other charset) writer that replaces malformed input with U+FFFD, like .NET's
     * {@code Encoding.UTF8} does.
     *
     * <p>Java's {@code OutputStreamWriter} replaces an unpaired surrogate with {@code '?'} (0x3F),
     * so the expected string of {@code Utf8JsonWriterTests#writeCommentsInvalidTextAllowed} - built
     * by writing the comment through this writer - carried {@code ?} where .NET (and the writer
     * under test, which writes U+FFFD) has U+FFFD. The tail of the comment was preserved either way;
     * only the replacement character differed.</p>
     */
    private static Writer newWriter(OutputStream stream, Charset charset) {
        java.nio.charset.CharsetEncoder encoder = charset.newEncoder()
            .onMalformedInput(java.nio.charset.CodingErrorAction.REPLACE)
            .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPLACE);
        if (encoder.canEncode('\uFFFD')) {
            encoder.replaceWith(new byte[] { (byte) 0xEF, (byte) 0xBF, (byte) 0xBD });
        }
        return new OutputStreamWriter(stream, encoder);
    }

    public StreamWriter(StreamWrapper stream, Encoding encoding) {
        this(stream, encoding.toCharset());
    }

    public StreamWriter(StreamWrapper stream, Encoding encoding, int bufferSize) {
        this(stream, encoding, bufferSize, false);
    }

    public StreamWriter(StreamWrapper stream, Encoding encoding, int bufferSize, boolean leaveOpen) {
        this(stream, encoding.toCharset(), leaveOpen);
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
        this.leaveOpen = false;
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

    public StreamWriter(String path, boolean append, Encoding encoding) {
        this(open(path, append), encoding);
    }

    public StreamWriter(String path, boolean append, Encoding encoding, int bufferSize) {
        this(open(path, append), encoding, bufferSize, false);
    }

    /** Mirrors C# StreamWriter.Encoding property */
    public Encoding getEncoding() {
        return Encoding.getEncoding(charset);
    }

    /** Mirrors C# StreamWriter.BaseStream property */
    public StreamWrapper getBaseStream() {
        flush();
        if (baseStream == null)
            throw new IllegalStateException("This StreamWriter was not created from a stream");
        return baseStream;
    }

    @Override
    public void close() {
        flush();
        if (!leaveOpen) {
            super.close();
        }
    }

    private static StreamWrapper open(String path, boolean append) {
        try {
            return StreamWrapper.of(new FileOutputStream(path, append));
        } catch (FileNotFoundException e) {
            throw new UncheckedIOException(e);
        }
    }
}
