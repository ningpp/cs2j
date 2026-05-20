package io.github.ningpp.compat;

import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.Reader;
import java.io.UncheckedIOException;
import java.nio.charset.Charset;

/** Replacement for System.IO.StreamReader. */
public class StreamReader extends TextReader {
    private final Encoding currentEncoding;

    public StreamReader(StreamWrapper stream) {
        this(stream.inputStream(), Encoding.getUTF8());
    }

    public StreamReader(StreamWrapper stream, Charset charset) {
        this(stream.inputStream(), Encoding.getEncoding(charset.name()));
    }

    public StreamReader(StreamWrapper stream, Encoding encoding) {
        this(stream.inputStream(), encoding);
    }

    public StreamReader(InputStream stream) {
        this(stream, Encoding.getUTF8());
    }

    public StreamReader(InputStream stream, Charset charset) {
        this(stream, Encoding.getEncoding(charset.name()));
    }

    public StreamReader(InputStream stream, Encoding encoding) {
        super(new InputStreamReader(stream, encoding.toCharset()));
        this.currentEncoding = encoding;
    }

    public StreamReader(Reader reader) {
        super(reader);
        this.currentEncoding = Encoding.getUTF8();
    }

    public StreamReader(String path) {
        this(open(path), Encoding.getUTF8());
    }

    public StreamReader(String path, Charset charset) {
        this(open(path), Encoding.getEncoding(charset.name()));
    }

    public StreamReader(String path, Encoding encoding) {
        this(open(path), encoding);
    }

    public Encoding getCurrentEncoding() {
        return currentEncoding;
    }

    private static InputStream open(String path) {
        try {
            return new FileInputStream(path);
        } catch (FileNotFoundException e) {
            throw new UncheckedIOException(e);
        }
    }
}
