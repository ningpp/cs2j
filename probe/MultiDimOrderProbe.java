import dotnet.text.json.JsonSerializer;
import io.github.ningpp.compat.ReadOnlySpan;
import io.github.ningpp.compat.TypeHelper;
import java.nio.charset.StandardCharsets;

/**
 * Mirrors ArrayTests.readPrimitiveArrayFail in one JVM: a jagged int[][] conversion first, then the
 * multidimensional token. Both are int[].class at runtime.
 *
 * <pre>
 *   java MultiDimOrderProbe fresh
 *   java MultiDimOrderProbe jagged-first
 * </pre>
 */
public class MultiDimOrderProbe {

    public static void main(String[] args) {
        String mode = args.length > 0 ? args[0] : "fresh";
        if (mode.equals("jagged-first")) {
            Object jagged = JsonSerializer.deserialize("[[1,2],[3,4]]", null, int[].class);
            System.out.println("jagged -> " + jagged);
        }
        try {
            ReadOnlySpan<Integer> json = span("[[1,2],[3,4]]");
            Class<int[][]> token = (Class<int[][]>) (Class<?>) TypeHelper.getMultiDimArrayClass(int.class, 2);
            Object value = JsonSerializer.deserialize(json, null, token);
            System.out.println("MD value -> " + value + "  (no exception)");
        } catch (Throwable t) {
            System.out.println("MD threw " + t.getClass().getName());
        }
    }

    static ReadOnlySpan<Integer> span(String s) {
        byte[] bytes = s.getBytes(StandardCharsets.UTF_8);
        Integer[] boxed = new Integer[bytes.length];
        for (int i = 0; i < bytes.length; i++) {
            boxed[i] = (int) bytes[i];
        }
        return new ReadOnlySpan<>(boxed, 0, boxed.length);
    }
}
