import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.JsonConverter;
import dotnet.text.json.Serialization.Tests.TestClassWithNull;
import io.github.ningpp.compat.ReadOnlySpan;

/** Directly drive the converter with a "null" payload to see which branch runs. */
public class NullBranchProbe {

    public static void main(String[] args) throws Exception {
        byte[] bytes = "null".getBytes(java.nio.charset.StandardCharsets.UTF_8);
        Integer[] boxed = new Integer[bytes.length];
        for (int i = 0; i < bytes.length; i++) {
            boxed[i] = (int) bytes[i];
        }
        ReadOnlySpan<Integer> span = new ReadOnlySpan<>(boxed, 0, boxed.length);
        dotnet.text.json.Utf8JsonReader reader = new dotnet.text.json.Utf8JsonReader(
            span, true, new dotnet.text.json.JsonReaderState());
        System.out.println("token before read = " + reader.getTokenType());
        System.out.println("reader class = " + reader.getClass().getName());

        JsonSerializerOptions options = new JsonSerializerOptions();
        JsonConverter converter = options.getConverter(TestClassWithNull.class);
        for (String m : new String[] { "getIsInternalConverter", "getHandleNullOnRead", "getCanBeNull", "getClassType" }) {
            System.out.println("   " + m + " = " + converter.getClass().getMethod(m).invoke(converter));
        }
        Object direct = converter.readCoreAsObject(
            new io.github.ningpp.compat.ObjectHolder<>(
                new dotnet.text.json.Utf8JsonReader(span, true, new dotnet.text.json.JsonReaderState())),
            options,
            new io.github.ningpp.compat.ObjectHolder<>(newStacks(TestClassWithNull.class, options)));
        System.out.println("readCoreAsObject -> " + direct);
    }

    static dotnet.text.json.ReadStack newStacks(Class<?> type, JsonSerializerOptions options) {
        dotnet.text.json.ReadStack stack = new dotnet.text.json.ReadStack();
        stack.initialize(type, options, false);
        return stack;
    }
}
