import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.JsonConverter;
import dotnet.text.json.Serialization.Tests.TestClassWithGenericICollection;

/** Reading a CSharpICollection<?> (element Object) must accept strings. */
public class ObjectCollectionProbe {

    public static void main(String[] args) throws Exception {
        JsonSerializerOptions options = new JsonSerializerOptions();
        JsonConverter converter = options.getConverter(io.github.ningpp.compat.CSharpICollection.class);
        System.out.println("CSharpICollection converter = " + converter.getClass().getSimpleName()
            + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter));

        try {
            Object value = JsonSerializer.deserialize("{\"MyData\":[\"Hello\",\"World\"]}", null,
                TestClassWithGenericICollection.class);
            System.out.println("OK -> " + value);
        } catch (Throwable t) {
            System.out.println("FAILED: " + t.getClass().getSimpleName() + ": "
                + String.valueOf(t.getMessage()).split("\n")[0]);
            for (StackTraceElement e : t.getStackTrace()) {
                if (e.getClassName().contains("Converters")) {
                    System.out.println("    at " + e);
                }
            }
        }
    }
}
