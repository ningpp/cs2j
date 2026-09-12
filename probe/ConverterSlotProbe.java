import com.fasterxml.jackson.core.type.TypeReference;
import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.JsonConverter;
import io.github.ningpp.compat.CSharpICollection;
import io.github.ningpp.compat.JsonSerializerCompat;

/**
 * JsonSerializerOptions._converters is keyed by the erased Class, so a converter built for one
 * element type answers for every other one. Prove it in one JVM.
 *
 * <pre>
 *   java ConverterSlotProbe fresh
 *   java ConverterSlotProbe warm-string   # convert CSharpICollection<String> first
 * </pre>
 */
public class ConverterSlotProbe {

    public static void main(String[] args) {
        String mode = args.length > 0 ? args[0] : "fresh";
        JsonSerializerOptions options = new JsonSerializerOptions();
        if (mode.equals("warm-string")) {
            Object warm = JsonSerializerCompat.deserialize(
                new TypeReference<CSharpICollection<String>>() {}, "[\"a\",\"b\"]");
            System.out.println("warmed with String -> " + warm);
        }
        describe(options, "CSharpICollection");

        // Now read the object-element payload through the same erased class.
        try {
            Object value = JsonSerializer.deserialize("{\"MyData\":[\"Hello\",\"World\"]}", null,
                dotnet.text.json.Serialization.Tests.TestClassWithGenericICollection.class);
            System.out.println("object payload -> OK");
        } catch (Throwable t) {
            System.out.println("object payload -> " + t.getClass().getSimpleName() + ": "
                + String.valueOf(t.getMessage()).split("\n")[0]);
        }
    }

    static void describe(JsonSerializerOptions options, String label) {
        try {
            JsonConverter converter = options.getConverter(CSharpICollection.class);
            System.out.println(label + " converter = " + converter.getClass().getSimpleName()
                + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter));
        } catch (Throwable t) {
            System.out.println(label + " describe failed: " + t);
        }
    }
}
