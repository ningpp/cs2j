package io.github.ningpp.compat;

/**
 * Test that MemoryExtensions.asSpan(byte[]) returns ReadOnlySpan&lt;Integer&gt;
 * matching the converter's byte→Integer type mapping.
 */
public class MemoryExtensionsSpanTest {

    public static void main(String[] args) {
        int passed = 0;
        int failed = 0;

        // Test 1: asSpan(byte[]) returns ReadOnlySpan<Integer>
        byte[] data = { 1, 2, 3, 4, 5 };
        ReadOnlySpan<Integer> span = MemoryExtensions.asSpan(data);
        if (span != null && span.length() == 5 && span.get(0) == 1) {
            System.out.println("PASS: Test 1 - asSpan(byte[]) returns ReadOnlySpan<Integer>");
            passed++;
        } else {
            System.out.println("FAIL: Test 1 - asSpan(byte[])");
            failed++;
        }

        // Test 2: asSpan(byte[], int, int) returns ReadOnlySpan<Integer>
        ReadOnlySpan<Integer> span2 = MemoryExtensions.asSpan(data, 1, 3);
        if (span2 != null && span2.length() == 3 && span2.get(0) == 2 && span2.get(2) == 4) {
            System.out.println("PASS: Test 2 - asSpan(byte[], 1, 3) returns ReadOnlySpan<Integer>");
            passed++;
        } else {
            System.out.println("FAIL: Test 2 - asSpan(byte[], 1, 3)");
            failed++;
        }

        // Test 3: Pass ReadOnlySpan<Integer> to method expecting ReadOnlySpan<Integer>
        int hash = computeHash(span, 0L);
        if (hash != 0) {
            System.out.println("PASS: Test 3 - method accepting ReadOnlySpan<Integer> works");
            passed++;
        } else {
            System.out.println("FAIL: Test 3 - computeHash returned 0");
            failed++;
        }

        // Test 4: new ReadOnlySpan<>(asSpan result) - verify diamond inference
        ReadOnlySpan<Integer> span3 = new ReadOnlySpan<>(MemoryExtensions.asSpan(data, 0, 3));
        if (span3 != null && span3.length() == 3) {
            System.out.println("PASS: Test 4 - diamond inference with copy constructor");
            passed++;
        } else {
            System.out.println("FAIL: Test 4 - diamond inference");
            failed++;
        }

        System.out.println("\n=== Results: " + passed + " passed, " + failed + " failed ===");
        if (failed > 0) {
            System.exit(1);
        }
    }

    private static int computeHash(ReadOnlySpan<Integer> data, long seed) {
        int hash = (int)(seed);
        for (int i = 0; i < data.length(); i++) {
            hash = ((hash << 5) + hash) ^ data.get(i);
        }
        return hash;
    }
}
