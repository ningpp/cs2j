package io.github.ningpp.compat;

import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.Reader;
import java.io.UncheckedIOException;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;

/** Replacement for System.IO.StreamReader. */
public class StreamReader extends TextReader {
    public StreamReader(StreamWrapper stream) {
        this(stream.inputStream(), StandardCharsets.UTF_8);
    }

    public StreamReader(StreamWrapper stream, Charset charset) {
        this(stream.inputStream(), charset);
    }

    public StreamReader(InputStream stream) {
        this(stream, StandardCharsets.UTF_8);
    }

    public StreamReader(InputStream stream, Charset charset) {
        super(new InputStreamReader(stream, charset));
    }

    public StreamReader(Reader reader) {
        super(reader);
    }

    public StreamReader(String path) {
        this(open(path), StandardCharsets.UTF_8);
    }

    public StreamReader(String path, Charset charset) {
        this(open(path), charset);
    }

    private static InputStream open(String path) {
        try {
            return new FileInputStream(path);
        } catch (FileNotFoundException e) {
            throw new UncheckedIOException(e);
        }
    }
}
