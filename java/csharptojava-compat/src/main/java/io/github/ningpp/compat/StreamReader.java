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

    /** C# new StreamReader(Stream, Encoding, bool detectEncodingFromByteOrderMarks, int bufferSize, bool leaveOpen). */
    public StreamReader(StreamWrapper stream, Encoding encoding, boolean detectEncodingFromByteOrderMarks,
                        int bufferSize, boolean leaveOpen) {
        this(stream.inputStream(), encoding);
    }

    /** C# overload with a MemoryStream argument (MemoryStream is a StreamWrapper). */
    public StreamReader(MemoryStream stream, Encoding encoding, boolean detectEncodingFromByteOrderMarks,
                        int bufferSize, boolean leaveOpen) {
        this((StreamWrapper) stream, encoding, detectEncodingFromByteOrderMarks, bufferSize, leaveOpen);
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
            return new FileInputStream(resolveTestResourcePath(path));
        } catch (FileNotFoundException e) {
            throw new UncheckedIOException(e);
        }
    }

    /**
     * Test fixtures keep their payload under src/test/resources/Resources, while
     * the converted MSTest deployment directory is src/test/resources. Resolve
     * the physical file by inserting the missing Resources segment.
     */
    private static String resolveTestResourcePath(String path) {
        java.io.File file = new java.io.File(path);
        if (file.exists()) {
            return path;
        }
        String marker = java.io.File.separator + "src" + java.io.File.separator
            + "test" + java.io.File.separator + "resources" + java.io.File.separator;
        int index = path.indexOf(marker);
        if (index >= 0) {
            String candidate = path.substring(0, index + marker.length())
                + "Resources" + java.io.File.separator
                + path.substring(index + marker.length());
            if (new java.io.File(candidate).exists()) {
                return candidate;
            }
        }
        String backslashMarker = "\\src\\test\\resources\\";
        int backslashIndex = path.indexOf(backslashMarker);
        if (backslashIndex >= 0) {
            String candidate = path.substring(0, backslashIndex + backslashMarker.length())
                + "Resources\\"
                + path.substring(backslashIndex + backslashMarker.length());
            if (new java.io.File(candidate).exists()) {
                return candidate;
            }
        }
        return path;
    }
}
