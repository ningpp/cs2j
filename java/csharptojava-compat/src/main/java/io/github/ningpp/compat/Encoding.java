package io.github.ningpp.compat;

import java.lang.foreign.MemorySegment;
import java.lang.foreign.ValueLayout;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.text.Normalizer;
import java.util.ArrayList;
import java.util.List;

/**
 * Facade for System.Text.Encoding over java.nio.charset.Charset.
 * Implements all public instance/static methods from .NET's System.Text.Encoding
 * that are expressible in Java (no pointer/Span overloads).
 */
public final class Encoding {

    private final Charset charset;
    private final int codePage;
    private final boolean isReadOnly;

    private Encoding(Charset charset, int codePage) {
        this(charset, codePage, true);
    }

    private Encoding(Charset charset, int codePage, boolean isReadOnly) {
        this.charset = charset;
        this.codePage = codePage;
        this.isReadOnly = isReadOnly;
    }

    // ---- Static Properties ----

    public static Encoding getDefault() {
        Charset charset = Charset.defaultCharset();
        return new Encoding(charset, codePageFor(charset));
    }

    public static Encoding getASCII() {
        return new Encoding(StandardCharsets.US_ASCII, 20127);
    }

    public static Encoding getLatin1() {
        return new Encoding(StandardCharsets.ISO_8859_1, 28591);
    }

    public static Encoding getUnicode() {
        return new Encoding(Charset.forName("UTF-16LE"), 1200);
    }

    public static Encoding getBigEndianUnicode() {
        return new Encoding(Charset.forName("UTF-16BE"), 1201);
    }

    public static Encoding getUTF7() {
        // Java does not natively support UTF-7; fall back to UTF-8 with the correct code page
        try {
            return new Encoding(Charset.forName("UTF-7"), 65000);
        } catch (java.nio.charset.UnsupportedCharsetException e) {
            return new Encoding(StandardCharsets.UTF_8, 65000);
        }
    }

    public static Encoding getUTF8() {
        return new Encoding(StandardCharsets.UTF_8, 65001);
    }

    public static Encoding getUTF32() {
        return new Encoding(Charset.forName("UTF-32LE"), 12000);
    }

    // ---- Instance Properties ----

    public Charset toCharset() {
        return charset;
    }

    public String getBodyName() {
        return charset.name().toLowerCase(java.util.Locale.ROOT);
    }

    public int getCodePage() {
        return codePage;
    }

    public String getEncodingName() {
        // Match .NET display names
        if (codePage == 65001) return "Unicode (UTF-8)";
        if (codePage == 1200) return "Unicode";
        if (codePage == 1201) return "Unicode (Big-Endian)";
        if (codePage == 20127) return "US-ASCII";
        if (codePage == 28591) return "Western European (ISO)";
        if (codePage == 65000) return "Unicode (UTF-7)";
        if (codePage == 12000) return "Unicode (UTF-32)";
        if (codePage == 12001) return "Unicode (UTF-32 Big-Endian)";
        return charset.displayName();
    }

    public String getHeaderName() {
        return getWebName();
    }

    public String getWebName() {
        String name = charset.name();
        // .NET uses lowercase IANA names
        if ("UTF-8".equalsIgnoreCase(name)) return "utf-8";
        if ("UTF-16LE".equalsIgnoreCase(name)) return "utf-16";
        if ("UTF-16BE".equalsIgnoreCase(name)) return "utf-16BE";
        if ("US-ASCII".equalsIgnoreCase(name)) return "us-ascii";
        if ("ISO-8859-1".equalsIgnoreCase(name)) return "iso-8859-1";
        return name.toLowerCase(java.util.Locale.ROOT);
    }

    public int getWindowsCodePage() {
        // Match .NET: UTF-8 and Unicode encodings return 1200 on Windows
        if (codePage == 65001) return 1200;
        if (codePage == 1200) return 1200;
        if (codePage == 1201) return 1200;
        if (codePage == 20127) return 1252;
        if (codePage == 28591) return 1252;
        if (codePage == 12000) return 1200;
        if (codePage == 12001) return 1200;
        if (codePage == 65000) return 1200;
        return codePage;
    }

    public boolean isBrowserDisplay() {
        return codePage == 65001 || codePage == 1200 || codePage == 1201;
    }

    public boolean isBrowserSave() {
        return codePage == 65001 || codePage == 1200 || codePage == 1201;
    }

    public boolean isMailNewsDisplay() {
        return codePage == 65001 || codePage == 20127 || codePage == 28591;
    }

    public boolean isMailNewsSave() {
        return codePage == 65001 || codePage == 20127 || codePage == 28591;
    }

    public boolean isSingleByte() {
        return codePage == 20127 || codePage == 28591;
    }

    public boolean isReadOnly() {
        return isReadOnly;
    }

    public byte[] getPreamble() {
        if (codePage == 65001) {
            return new byte[]{(byte) 0xEF, (byte) 0xBB, (byte) 0xBF};
        }
        if (codePage == 1200) {
            return new byte[]{(byte) 0xFF, (byte) 0xFE};
        }
        if (codePage == 1201) {
            return new byte[]{(byte) 0xFE, (byte) 0xFF};
        }
        if (codePage == 12000) {
            return new byte[]{(byte) 0xFF, (byte) 0xFE, 0, 0};
        }
        return new byte[0];
    }

    // ---- Static Methods ----

    public static Encoding getEncoding(String name) {
        Charset charset = Charset.forName(name);
        return new Encoding(charset, codePageFor(charset));
    }

    public static Encoding getEncoding(int codePage) {
        return switch (codePage) {
            case 1200 -> new Encoding(Charset.forName("UTF-16LE"), codePage);
            case 1201 -> new Encoding(Charset.forName("UTF-16BE"), codePage);
            case 12000 -> new Encoding(Charset.forName("UTF-32LE"), codePage);
            case 12001 -> new Encoding(Charset.forName("UTF-32BE"), codePage);
            case 20127 -> new Encoding(StandardCharsets.US_ASCII, codePage);
            case 28591 -> new Encoding(StandardCharsets.ISO_8859_1, codePage);
            case 65000 -> getUTF7();
            case 65001 -> new Encoding(StandardCharsets.UTF_8, codePage);
            default -> getEncoding("windows-" + codePage);
        };
    }

    /** Mirrors C# Encoding.GetEncoding(int, EncoderReplacementFallback, DecoderReplacementFallback) */
    public static Encoding getEncoding(int codePage, EncoderReplacementFallback encoderFallback, DecoderReplacementFallback decoderFallback) {
        // The fallback parameters are ignored in this compat implementation
        return getEncoding(codePage);
    }

    public static Encoding getEncoding() {
        return getDefault();
    }

    public static byte[] convert(Encoding srcEncoding, Encoding dstEncoding, byte[] bytes) {
        return convert(srcEncoding, dstEncoding, bytes, 0, bytes.length);
    }

    public static byte[] convert(Encoding srcEncoding, Encoding dstEncoding, byte[] bytes, int index, int count) {
        String s = srcEncoding.getString(bytes, index, count);
        return dstEncoding.getBytes(s);
    }

    public static EncodingInfo[] getEncodings() {
        return new EncodingInfo[]{
            new EncodingInfo(1200, "utf-16", "Unicode"),
            new EncodingInfo(1201, "utf-16BE", "Unicode (Big-Endian)"),
            new EncodingInfo(12000, "utf-32", "Unicode (UTF-32)"),
            new EncodingInfo(12001, "utf-32BE", "Unicode (UTF-32 Big-Endian)"),
            new EncodingInfo(20127, "us-ascii", "US-ASCII"),
            new EncodingInfo(28591, "iso-8859-1", "Western European (ISO)"),
            new EncodingInfo(65001, "utf-8", "Unicode (UTF-8)"),
        };
    }

    public static void registerProvider(Object provider) {
        // No-op: Java Charset mechanism is different; kept for API compatibility
    }

    // ---- Instance Methods: GetByteCount ----

    public int getByteCount(char[] chars) {
        return getByteCount(chars, 0, chars.length);
    }

    public int getByteCount(String s) {
        return s.getBytes(charset).length;
    }

    public int getByteCount(String s, int index, int count) {
        return getByteCount(s.toCharArray(), index, count);
    }

    public int getByteCount(char[] chars, int index, int count) {
        CharBuffer cb = CharBuffer.wrap(chars, index, count);
        ByteBuffer bb = charset.encode(cb);
        return bb.remaining();
    }

    // ---- Instance Methods: GetBytes ----

    public byte[] getBytes(char[] chars) {
        return getBytes(chars, 0, chars.length);
    }

    public byte[] getBytes(String s) {
        return s.getBytes(charset);
    }

    public byte[] getBytes(String s, int index, int count) {
        return getBytes(s.toCharArray(), index, count);
    }

    public byte[] getBytes(char[] chars, int index, int count) {
        CharBuffer cb = CharBuffer.wrap(chars, index, count);
        ByteBuffer bb = charset.encode(cb);
        byte[] result = new byte[bb.remaining()];
        bb.get(result);
        return result;
    }

    public int getBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) {
        CharBuffer cb = CharBuffer.wrap(chars, charIndex, charCount);
        ByteBuffer bb = charset.encode(cb);
        int len = bb.remaining();
        bb.get(bytes, byteIndex, len);
        return len;
    }

    public int getBytes(String s, int charIndex, int charCount, byte[] bytes, int byteIndex) {
        char[] chars = s.toCharArray();
        return getBytes(chars, charIndex, charCount, bytes, byteIndex);
    }

    /**
     * Pointer-based overload matching .NET's
     * {@code Encoding.GetBytes(char* src, int charCount, byte* dst, int byteCount)}.
     * Reads {@code charCount} chars from the source MemorySegment (char-aligned),
     * encodes them, and writes up to {@code byteCount} bytes to the destination
     * MemorySegment.
     *
     * @param src       source MemorySegment containing chars (2 bytes each)
     * @param charCount number of chars to read from src
     * @param dst       destination MemorySegment for encoded bytes
     * @param byteCount maximum number of bytes to write to dst
     * @return the number of bytes written
     */
    public int getBytes(MemorySegment src, int charCount, MemorySegment dst, int byteCount) {
        char[] chars = new char[charCount];
        for (int i = 0; i < charCount; i++) {
            chars[i] = src.get(ValueLayout.JAVA_CHAR, i * 2L);
        }
        CharBuffer cb = CharBuffer.wrap(chars);
        ByteBuffer bb = charset.encode(cb);
        int len = Math.min(bb.remaining(), byteCount);
        for (int i = 0; i < len; i++) {
            dst.set(ValueLayout.JAVA_BYTE, i, bb.get(i));
        }
        return len;
    }

    // ---- Instance Methods: GetCharCount ----

    public int getCharCount(byte[] bytes) {
        return getCharCount(bytes, 0, bytes.length);
    }

    public int getCharCount(byte[] bytes, int index, int count) {
        ByteBuffer bb = ByteBuffer.wrap(bytes, index, count);
        CharBuffer cb = charset.decode(bb);
        return cb.remaining();
    }

    // ---- Instance Methods: GetChars ----

    public char[] getChars(byte[] bytes) {
        return getChars(bytes, 0, bytes.length);
    }

    public char[] getChars(byte[] bytes, int index, int count) {
        ByteBuffer bb = ByteBuffer.wrap(bytes, index, count);
        CharBuffer cb = charset.decode(bb);
        char[] result = new char[cb.remaining()];
        cb.get(result);
        return result;
    }

    public int getChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) {
        ByteBuffer bb = ByteBuffer.wrap(bytes, byteIndex, byteCount);
        CharBuffer cb = charset.decode(bb);
        int len = cb.remaining();
        cb.get(chars, charIndex, len);
        return len;
    }

    // ---- Instance Methods: GetString ----

    public String getString(byte[] bytes) {
        return getString(bytes, 0, bytes.length);
    }

    public String getString(byte[] bytes, int index, int count) {
        return new String(bytes, index, count, charset);
    }

    // ---- Instance Methods: GetMaxByteCount / GetMaxCharCount ----

    public int getMaxByteCount(int charCount) {
        // .NET formula: (charCount + 1) * bytesPerChar (for default fallback)
        if (codePage == 1200 || codePage == 1201) return (charCount + 1) * 2;
        if (codePage == 12000 || codePage == 12001) return (charCount + 1) * 4;
        if (codePage == 65001) return (charCount + 1) * 3;
        if (codePage == 65000) return (charCount + 1) * 3;
        return charCount + 1; // single-byte encodings (ASCII, Latin1, etc.)
    }

    public int getMaxCharCount(int byteCount) {
        // .NET formulas from source code:
        // ASCII: byteCount (1:1, no surrogates)
        // UTF8: byteCount + 1
        // Unicode: (byteCount >> 1) + (byteCount & 1) + 1
        // UTF32: byteCount / 2 + 2
        if (codePage == 20127 || codePage == 28591) return byteCount;
        if (codePage == 65001) return byteCount + 1;
        if (codePage == 65000) return byteCount + 1;
        if (codePage == 1200 || codePage == 1201) return (byteCount >> 1) + (byteCount & 1) + 1;
        if (codePage == 12000 || codePage == 12001) return byteCount / 2 + 2;
        return byteCount; // default for other single-byte encodings
    }

    // ---- Instance Methods: Clone, GetDecoder, GetEncoder ----

    public Object clone() {
        return new Encoding(charset, codePage, false);
    }

    public Decoder getDecoder() {
        return new Decoder(this);
    }

    public Encoder getEncoder() {
        return new Encoder(this);
    }

    // ---- Instance Methods: IsAlwaysNormalized ----

    public boolean isAlwaysNormalized() {
        return isAlwaysNormalized(java.text.Normalizer.Form.NFC);
    }

    public boolean isAlwaysNormalized(Normalizer.Form form) {
        // .NET returns false for all standard encodings
        return false;
    }

    // ---- Instance Methods: Equals / GetHashCode ----

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof Encoding other)) return false;
        return this.codePage == other.codePage && this.charset.equals(other.charset);
    }

    @Override
    public int hashCode() {
        return charset.hashCode() * 31 + codePage;
    }

    // ---- Helper ----

    private static int codePageFor(Charset charset) {
        String name = charset.name().toUpperCase(java.util.Locale.ROOT);
        if ("UTF-8".equals(name)) return 65001;
        if ("UTF-16LE".equals(name)) return 1200;
        if ("UTF-16BE".equals(name)) return 1201;
        if ("UTF-32LE".equals(name)) return 12000;
        if ("UTF-32BE".equals(name)) return 12001;
        if ("US-ASCII".equals(name)) return 20127;
        if ("ISO-8859-1".equals(name)) return 28591;
        if ("UTF-7".equals(name)) return 65000;
        if (name.startsWith("WINDOWS-")) {
            try {
                return Integer.parseInt(name.substring("WINDOWS-".length()));
            } catch (NumberFormatException ignored) {
                return 0;
            }
        }
        return 0;
    }

    // ---- Nested Classes ----

    public static final class EncodingInfo {
        private final int codePage;
        private final String name;
        private final String displayName;

        EncodingInfo(int codePage, String name, String displayName) {
            this.codePage = codePage;
            this.name = name;
            this.displayName = displayName;
        }

        public int getCodePage() { return codePage; }
        public String getName() { return name; }
        public String getDisplayName() { return displayName; }

        @Override
        public boolean equals(Object obj) {
            if (this == obj) return true;
            if (!(obj instanceof EncodingInfo other)) return false;
            return this.codePage == other.codePage;
        }

        @Override
        public int hashCode() { return codePage; }
    }

    public static final class Decoder {
        private final Encoding encoding;

        Decoder(Encoding encoding) {
            this.encoding = encoding;
        }

        public int getCharCount(byte[] bytes, int index, int count) {
            return encoding.getCharCount(bytes, index, count);
        }

        public int getChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) {
            return encoding.getChars(bytes, byteIndex, byteCount, chars, charIndex);
        }

        public void convert(byte[] bytes, int byteIndex, int byteCount,
                            char[] chars, int charIndex, int charCount, boolean flush,
                            int[] bytesUsed, int[] charsUsed, boolean[] completed) {
            // Simplified: decode all available bytes
            CharBuffer cb = charset().decode(ByteBuffer.wrap(bytes, byteIndex, byteCount));
            int len = Math.min(cb.remaining(), charCount - charIndex);
            cb.get(chars, charIndex, len);
            if (bytesUsed != null && bytesUsed.length > 0) bytesUsed[0] = byteCount;
            if (charsUsed != null && charsUsed.length > 0) charsUsed[0] = len;
            if (completed != null && completed.length > 0) completed[0] = !cb.hasRemaining();
        }

        private Charset charset() { return encoding.toCharset(); }
    }

    public static final class Encoder {
        private final Encoding encoding;

        Encoder(Encoding encoding) {
            this.encoding = encoding;
        }

        public int getByteCount(char[] chars, int index, int count) {
            return encoding.getByteCount(chars, index, count);
        }

        public int getBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) {
            return encoding.getBytes(chars, charIndex, charCount, bytes, byteIndex);
        }

        public void convert(char[] chars, int charIndex, int charCount,
                            byte[] bytes, int byteIndex, int byteCount, boolean flush,
                            int[] charsUsed, int[] bytesUsed, boolean[] completed) {
            ByteBuffer bb = charset().encode(CharBuffer.wrap(chars, charIndex, charCount));
            int len = Math.min(bb.remaining(), byteCount - byteIndex);
            bb.get(bytes, byteIndex, len);
            if (charsUsed != null && charsUsed.length > 0) charsUsed[0] = charCount;
            if (bytesUsed != null && bytesUsed.length > 0) bytesUsed[0] = len;
            if (completed != null && completed.length > 0) completed[0] = !bb.hasRemaining();
        }

        private Charset charset() { return encoding.toCharset(); }
    }
}
