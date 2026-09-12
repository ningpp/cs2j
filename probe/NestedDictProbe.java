import com.fasterxml.jackson.core.type.TypeReference;
import io.github.ningpp.compat.CSharpDictionary;
import io.github.ningpp.compat.JsonSerializerCompat;
import io.github.ningpp.compat.TypeHelper;

/** Mirrors ReferenceHandlerTests.dictionaryOfDictionaries: Dictionary<string, Dictionary<string, string>>. */
public class NestedDictProbe {

    public static void main(String[] args) {
        // 1) plain document
        run("plain", "{\"Dictionary1\":{\"value1\":1,\"value2\":2}}", null);
        // 2) same shape as the failing test, with preserve options
        Object options = null;
        try {
            Class<?> optionsType = Class.forName("dotnet.text.json.JsonSerializerOptions");
            java.lang.reflect.Field f = Class.forName("dotnet.text.json.Serialization.Tests.ReferenceHandlerTests")
                .getDeclaredField("s_deserializerOptionsPreserve");
            f.setAccessible(true);
            options = f.get(null);
        } catch (Throwable t) {
            System.out.println("no preserve options: " + t);
        }
        run("preserve", "{\"$id\":\"1\",\"Dictionary1\":{\"$id\":\"2\",\"value1\":1,\"value2\":2},\"Dictionary2\":{\"$ref\":\"2\"}}", options);
    }

    static void run(String label, String json, Object options) {
        try {
            Object result = options == null
                ? JsonSerializerCompat.deserialize(
                    new TypeReference<CSharpDictionary<String, CSharpDictionary<String, Integer>>>() {}, json)
                : JsonSerializerCompat.deserialize(
                    new TypeReference<CSharpDictionary<String, CSharpDictionary<String, Integer>>>() {}, json, options);
            System.out.println(label + " -> " + result);
        } catch (Throwable t) {
            System.out.println(label + " -> FAILED: " + t);
        }
    }
}
