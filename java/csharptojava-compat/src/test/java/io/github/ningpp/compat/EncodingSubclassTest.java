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

        System.out.println("\n=== Results: " + passed + " passed, " + failed + " failed ===");
        if (failed > 0) {
            System.exit(1);
        }
    }
}
