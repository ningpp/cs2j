import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Tests.TestClassWithObjectIEnumerableConstructibleTypes;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;

/** Is the queue member's converter built with the right element type now? */
public class QueueElemProbe {

    public static void main(String[] args) throws Exception {
        JsonSerializerOptions options = new JsonSerializerOptions();
        describe(options, io.github.ningpp.compat.CSharpQueue.class, "CSharpQueue before");
        try {
            Object value = JsonSerializer.deserialize(
                new io.github.ningpp.compat.ReadOnlySpan<>(
                    toBoxed(TestClassWithObjectIEnumerableConstructibleTypes.s_data), 0,
                    TestClassWithObjectIEnumerableConstructibleTypes.s_data.length),
                null, TestClassWithObjectIEnumerableConstructibleTypes.class);
            System.out.println("deserialize -> " + value);
        } catch (Throwable t) {
            System.out.println("deserialize FAILED: " + t.getClass().getSimpleName() + ": "
                + String.valueOf(t.getMessage()).split("\n")[0]);
        }
        describe(options, io.github.ningpp.compat.CSharpQueue.class, "CSharpQueue after");
    }

    static void describe(JsonSerializerOptions options, Class<?> type, String label) throws Exception {
        Object converter = options.getConverter(type);
        System.out.println(label + " -> " + converter.getClass().getSimpleName()
            + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter));
    }

    static Integer[] toBoxed(byte[] bytes) {
        Integer[] boxed = new Integer[bytes.length];
        for (int i = 0; i < bytes.length; i++) {
            boxed[i] = (int) bytes[i];
        }
        return boxed;
    }
}
