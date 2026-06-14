package io.github.ningpp.compat;

import java.util.Arrays;

public class EncodingTest {
    static String fmt(byte[] b) {
        if (b == null) return "null";
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < b.length; i++) {
            if (i > 0) sb.append(",");
            sb.append(b[i] & 0xFF);
        }
        return sb.append("]").toString();
    }

    static String fmt(char[] c) {
        if (c == null) return "null";
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < c.length; i++) {
            if (i > 0) sb.append(",");
            sb.append((int) c[i]);
        }
        return sb.append("]").toString();
    }

    public static void main(String[] args) {
        var utf8 = Encoding.getUTF8();
        var ascii = Encoding.getASCII();
        var unicode = Encoding.getUnicode();
        var utf32 = Encoding.getUTF32();

        String testStr = "Hello\u03A0";
        char[] testChars = testStr.toCharArray();
        byte[] utf8Bytes = utf8.getBytes(testStr);
        byte[] asciiBytes = ascii.getBytes(testStr);

        System.out.println("=== Static Properties ===");
        System.out.println("UTF8.CodePage=" + utf8.getCodePage());
        System.out.println("ASCII.CodePage=" + ascii.getCodePage());
        System.out.println("Unicode.CodePage=" + unicode.getCodePage());
        System.out.println("UTF32.CodePage=" + utf32.getCodePage());
        System.out.println("Latin1.CodePage=" + Encoding.getLatin1().getCodePage());
        System.out.println("BigEndianUnicode.CodePage=" + Encoding.getBigEndianUnicode().getCodePage());
        System.out.println("UTF7.CodePage=" + Encoding.getUTF7().getCodePage());

        System.out.println("\n=== Instance Properties ===");
        System.out.println("UTF8.BodyName=" + utf8.getBodyName());
        System.out.println("UTF8.EncodingName=" + utf8.getEncodingName());
        System.out.println("UTF8.HeaderName=" + utf8.getHeaderName());
        System.out.println("UTF8.WebName=" + utf8.getWebName());
        System.out.println("UTF8.WindowsCodePage=" + utf8.getWindowsCodePage());
        System.out.println("UTF8.IsBrowserDisplay=" + utf8.isBrowserDisplay());
        System.out.println("UTF8.IsBrowserSave=" + utf8.isBrowserSave());
        System.out.println("UTF8.IsMailNewsDisplay=" + utf8.isMailNewsDisplay());
        System.out.println("UTF8.IsMailNewsSave=" + utf8.isMailNewsSave());
        System.out.println("UTF8.IsSingleByte=" + utf8.isSingleByte());
        System.out.println("UTF8.IsReadOnly=" + utf8.isReadOnly());
        System.out.println("ASCII.IsSingleByte=" + ascii.isSingleByte());
        System.out.println("ASCII.IsBrowserDisplay=" + ascii.isBrowserDisplay());
        System.out.println("ASCII.IsMailNewsDisplay=" + ascii.isMailNewsDisplay());
        System.out.println("UTF8.Preamble=" + fmt(utf8.getPreamble()));
        System.out.println("Unicode.Preamble=" + fmt(unicode.getPreamble()));
        System.out.println("ASCII.Preamble=" + fmt(ascii.getPreamble()));
        System.out.println("UTF32.Preamble=" + fmt(utf32.getPreamble()));

        System.out.println("\n=== GetByteCount ===");
        System.out.println("utf8.GetByteCount(char[])=" + utf8.getByteCount(testChars));
        System.out.println("utf8.GetByteCount(string)=" + utf8.getByteCount(testStr));
        System.out.println("utf8.GetByteCount(chars,0,5)=" + utf8.getByteCount(testChars, 0, 5));
        System.out.println("utf8.GetByteCount(str,0,5)=" + utf8.getByteCount(testStr, 0, 5));
        System.out.println("ascii.GetByteCount(char[])=" + ascii.getByteCount(testChars));
        System.out.println("ascii.GetByteCount(string)=" + ascii.getByteCount(testStr));

        System.out.println("\n=== GetBytes ===");
        System.out.println("utf8.GetBytes(char[])=" + fmt(utf8.getBytes(testChars)));
        System.out.println("utf8.GetBytes(string)=" + fmt(utf8.getBytes(testStr)));
        System.out.println("utf8.GetBytes(chars,0,5)=" + fmt(utf8.getBytes(testChars, 0, 5)));
        System.out.println("utf8.GetBytes(str,0,5)=" + fmt(utf8.getBytes(testStr, 0, 5)));
        byte[] buf1 = new byte[20];
        int n1 = utf8.getBytes(testChars, 0, 5, buf1, 0);
        System.out.println("utf8.GetBytes(chars,0,5,buf,0)=" + n1 + " buf=" + fmt(Arrays.copyOf(buf1, n1)));
        byte[] buf2 = new byte[20];
        int n2 = utf8.getBytes(testStr, 0, 5, buf2, 0);
        System.out.println("utf8.GetBytes(str,0,5,buf,0)=" + n2 + " buf=" + fmt(Arrays.copyOf(buf2, n2)));
        System.out.println("ascii.GetBytes(char[])=" + fmt(ascii.getBytes(testChars)));

        System.out.println("\n=== GetCharCount ===");
        System.out.println("utf8.GetCharCount(bytes)=" + utf8.getCharCount(utf8Bytes));
        System.out.println("utf8.GetCharCount(bytes,0,5)=" + utf8.getCharCount(utf8Bytes, 0, 5));

        System.out.println("\n=== GetChars ===");
        System.out.println("utf8.GetChars(bytes)=" + fmt(utf8.getChars(utf8Bytes)));
        System.out.println("utf8.GetChars(bytes,0,5)=" + fmt(utf8.getChars(utf8Bytes, 0, 5)));
        char[] cbuf1 = new char[20];
        int cn1 = utf8.getChars(utf8Bytes, 0, utf8Bytes.length, cbuf1, 0);
        System.out.println("utf8.GetChars(bytes,0,len,cbuf,0)=" + cn1 + " chars=" + fmt(Arrays.copyOf(cbuf1, cn1)));

        System.out.println("\n=== GetString ===");
        System.out.println("utf8.GetString(bytes)=" + utf8.getString(utf8Bytes));
        System.out.println("utf8.GetString(bytes,0,5)=" + utf8.getString(utf8Bytes, 0, 5));
        System.out.println("ascii.GetString(asciiBytes)=" + ascii.getString(asciiBytes));

        System.out.println("\n=== GetMaxByteCount / GetMaxCharCount ===");
        System.out.println("utf8.GetMaxByteCount(10)=" + utf8.getMaxByteCount(10));
        System.out.println("utf8.GetMaxCharCount(10)=" + utf8.getMaxCharCount(10));
        System.out.println("ascii.GetMaxByteCount(10)=" + ascii.getMaxByteCount(10));
        System.out.println("ascii.GetMaxCharCount(10)=" + ascii.getMaxCharCount(10));
        System.out.println("unicode.GetMaxByteCount(10)=" + unicode.getMaxByteCount(10));
        System.out.println("unicode.GetMaxCharCount(10)=" + unicode.getMaxCharCount(10));
        System.out.println("utf32.GetMaxByteCount(10)=" + utf32.getMaxByteCount(10));
        System.out.println("utf32.GetMaxCharCount(10)=" + utf32.getMaxCharCount(10));

        System.out.println("\n=== Convert ===");
        byte[] conv = Encoding.convert(unicode, utf8, unicode.getBytes(testStr));
        System.out.println("Convert(Unicode,UTF8,bytes)=" + fmt(conv));
        byte[] conv2 = Encoding.convert(unicode, utf8, unicode.getBytes(testStr), 0, 10);
        System.out.println("Convert(Unicode,UTF8,bytes,0,10)=" + fmt(conv2));

        System.out.println("\n=== Clone ===");
        Encoding cloned = (Encoding) utf8.clone();
        System.out.println("cloned.CodePage=" + cloned.getCodePage() + " cloned.IsReadOnly=" + cloned.isReadOnly());

        System.out.println("\n=== IsAlwaysNormalized ===");
        System.out.println("ascii.IsAlwaysNormalized()=" + ascii.isAlwaysNormalized());
        System.out.println("utf8.IsAlwaysNormalized()=" + utf8.isAlwaysNormalized());

        System.out.println("\n=== Equals/GetHashCode ===");
        System.out.println("utf8.Equals(utf8)=" + utf8.equals(utf8));
        System.out.println("utf8.Equals(ascii)=" + utf8.equals(ascii));
        System.out.println("utf8.GetHashCode()=" + utf8.hashCode());

        System.out.println("\n=== GetEncodings ===");
        for (Encoding.EncodingInfo ei : Encoding.getEncodings()) {
            System.out.println("  " + ei.getCodePage() + " " + ei.getName() + " " + ei.getDisplayName());
        }

        System.out.println("\n=== Decoder ===");
        Decoder dec = utf8.getDecoder();
        char[] dcbuf = new char[20];
        int dcn = dec.getChars(utf8Bytes, 0, utf8Bytes.length, dcbuf, 0);
        System.out.println("decoder.GetChars(bytes,0,len,cbuf,0)=" + dcn + " chars=" + fmt(Arrays.copyOf(dcbuf, dcn)));

        System.out.println("\n=== Encoder ===");
        Encoder encObj = utf8.getEncoder();
        byte[] ebuf = new byte[20];
        IntHolder bytesUsed = new IntHolder(), charsUsed = new IntHolder();
        BoolHolder completed = new BoolHolder();
        encObj.convert(testChars, 0, 5, ebuf, 0, 20, true, charsUsed, bytesUsed, completed);
        System.out.println("encoder.Convert(chars,0,5,buf,0,20)=" + bytesUsed.value + " buf=" + fmt(Arrays.copyOf(ebuf, bytesUsed.value)));

        System.out.println("\n=== Decoder ===");
        Decoder decObj = utf8.getDecoder();
        char[] dcbuf2 = new char[20];
        IntHolder bytesUsed2 = new IntHolder(), charsUsed2 = new IntHolder();
        BoolHolder completed2 = new BoolHolder();
        decObj.convert(utf8Bytes, 0, utf8Bytes.length, dcbuf2, 0, 20, true, bytesUsed2, charsUsed2, completed2);
        System.out.println("decoder.Convert(bytes,0,len,cbuf,0,20)=" + charsUsed2.value + " chars=" + fmt(Arrays.copyOf(dcbuf2, charsUsed2.value)));

        System.out.println("\n=== Precise ASCII test ===");
        String simple = "AB";
        byte[] simpleBytes = ascii.getBytes(simple);
        System.out.println("ascii.GetBytes('AB')=" + fmt(simpleBytes));
        System.out.println("ascii.GetByteCount('AB')=" + ascii.getByteCount(simple));
        System.out.println("ascii.GetCharCount([65,66])=" + ascii.getCharCount(simpleBytes));
        System.out.println("ascii.GetString([65,66])=" + ascii.getString(simpleBytes));

        System.out.println("\n=== Precise Unicode test ===");
        byte[] uniBytes = unicode.getBytes("A");
        System.out.println("unicode.GetBytes('A')=" + fmt(uniBytes));
        System.out.println("unicode.GetByteCount('A')=" + unicode.getByteCount("A"));
    }
}
