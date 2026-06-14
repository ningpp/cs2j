package io.github.ningpp.compat;

/**
 * Test that Encoding can be subclassed (mirrors .NET's non-sealed System.Text.Encoding).
 * Error #1 fix: Ucs4Encoding extends Encoding in XmlEncoding.cs
 */
public class EncodingSubclassTest {

    // Simulates Ucs4Encoding pattern from XmlEncoding.cs
    public static class TestEncoding extends Encoding {
        private final String name;

        public TestEncoding(String name) {
            super(); // Uses protected no-arg constructor
            this.name = name;
        }

        @Override
        public String getEncodingName() {
            return name;
        }

        @Override
        public int getCodePage() {
            return 9999;
        }
    }

    public static class TestDecoder extends Decoder {
        @Override
        public int getCharCount(byte[] bytes, int index, int count) {
            return count;
        }

        @Override
        public int getChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) {
            for (int i = 0; i < byteCount; i++) {
                chars[charIndex + i] = (char)(bytes[byteIndex + i] & 0xFF);
            }
            return byteCount;
        }

        @Override
        public void convert(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, int charCount,
                            boolean flush, IntHolder bytesUsed, IntHolder charsUsed, BoolHolder completed) {
            int count = Math.min(byteCount, charCount);
            getChars(bytes, byteIndex, count, chars, charIndex);
            bytesUsed.value = count;
            charsUsed.value = count;
            completed.value = count == byteCount;
        }
    }

    public static class EncodingWithCustomDecoder extends Encoding {
        private final Decoder decoder = new TestDecoder();

        @Override
        public Decoder getDecoder() {
            return decoder;
        }
    }

    // Simulates Ucs4Encoding4321 pattern (subclass of subclass)
    public static class TestSubEncoding extends TestEncoding {
        public TestSubEncoding() {
            super("test-sub");
        }

        @Override
        public String getWebName() {
            return "test-sub-web";
        }
    }

    public static void main(String[] args) {
        int passed = 0;
        int failed = 0;

        // Test 1: Basic subclass can be instantiated
        TestEncoding enc = new TestEncoding("test-encoding");
        if (enc != null && enc.getEncodingName().equals("test-encoding")) {
            System.out.println("PASS: Test 1 - Basic subclass instantiation");
            passed++;
        } else {
            System.out.println("FAIL: Test 1 - Basic subclass instantiation");
            failed++;
        }

        // Test 2: Subclass inherits parent methods
        if (enc.getCodePage() == 9999) {
            System.out.println("PASS: Test 2 - Subclass overrides getCodePage");
            passed++;
        } else {
            System.out.println("FAIL: Test 2 - Subclass overrides getCodePage, got " + enc.getCodePage());
            failed++;
        }

        // Test 3: getBytes works on subclass
        byte[] bytes = enc.getBytes("Hello");
        if (bytes != null && bytes.length > 0) {
            System.out.println("PASS: Test 3 - Subclass inherits getBytes");
            passed++;
        } else {
            System.out.println("FAIL: Test 3 - Subclass inherits getBytes");
            failed++;
        }

        // Test 4: Subclass of subclass works
        TestSubEncoding subEnc = new TestSubEncoding();
        if ("test-sub".equals(subEnc.getEncodingName()) && "test-sub-web".equals(subEnc.getWebName())) {
            System.out.println("PASS: Test 4 - Subclass of subclass");
            passed++;
        } else {
            System.out.println("FAIL: Test 4 - Subclass of subclass");
            failed++;
        }

        // Test 5: Instanceof check
        if (subEnc instanceof TestEncoding && subEnc instanceof Encoding) {
            System.out.println("PASS: Test 5 - instanceof chain works");
            passed++;
        } else {
            System.out.println("FAIL: Test 5 - instanceof chain");
            failed++;
        }

        // Test 6: Encoding subclasses can override getDecoder with the standalone Decoder type
        EncodingWithCustomDecoder customDecoderEncoding = new EncodingWithCustomDecoder();
        if (customDecoderEncoding.getDecoder() instanceof TestDecoder) {
            System.out.println("PASS: Test 6 - subclass getDecoder override returns standalone Decoder");
            passed++;
        } else {
            System.out.println("FAIL: Test 6 - subclass getDecoder override");
            failed++;
        }

        System.out.println("\n=== Results: " + passed + " passed, " + failed + " failed ===");
        if (failed > 0) {
            System.exit(1);
        }
    }
}
