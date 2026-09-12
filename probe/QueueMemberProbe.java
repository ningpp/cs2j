import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Tests.TestClassWithObjectIEnumerableConstructibleTypes;

/**
 * Faithful end-to-end reproduction: deserialize the exact class the suite fails on and report
 * what the queue property's converter resolved to.
 */
public class QueueMemberProbe {

    @SuppressWarnings({"unchecked", "rawtypes"})
    public static void main(String[] args) throws Exception {
        Object value = JsonSerializer.deserialize(
            new io.github.ningpp.compat.ReadOnlySpan<>(toBytes(TestClassWithObjectIEnumerableConstructibleTypes.s_data),
                0, toBytes(TestClassWithObjectIEnumerableConstructibleTypes.s_data).length),
            null, TestClassWithObjectIEnumerableConstructibleTypes.class);
        System.out.println("value = " + value);

        JsonSerializerOptions options = new JsonSerializerOptions();
        for (String name : new String[] { "CSharpQueue", "CSharpStack", "CSharpHashSet" }) {
            Class<?> type = Class.forName("io.github.ningpp.compat." + name);
            Object converter = options.getConverter(type);
            System.out.println(name + " converter = " + converter.getClass().getSimpleName()
                + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter));
        }
    }

    static Integer[] toBytes(byte[] bytes) {
        Integer[] boxed = new Integer[bytes.length];
        for (int i = 0; i < bytes.length; i++) {
            boxed[i] = (int) bytes[i];
        }
        return boxed;
    }
}
