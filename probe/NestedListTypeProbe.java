import com.fasterxml.jackson.core.type.TypeReference;
import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.JsonSerializerCompat;

/** Nested generic LIST (not interface, not array element): List<List<int>>. */
public class NestedListTypeProbe {

    @SuppressWarnings({"unchecked", "rawtypes"})
    public static void main(String[] args) throws Exception {
        JsonSerializerOptions options = new JsonSerializerOptions();
        describe(options, "CSharpList");
        try {
            Object value = JsonSerializerCompat.deserialize(
                new TypeReference<CSharpList<CSharpList<Integer>>>() {}, "[[1,2],[3,4]]");
            System.out.println("TypeReference -> " + value);
        } catch (Throwable t) {
            System.out.println("TypeReference FAILED: " + t.getClass().getSimpleName() + ": "
                + String.valueOf(t.getMessage()).split("\n")[0]);
        }
        describe(options, "CSharpList after");
    }

    static void describe(JsonSerializerOptions options, String label) throws Exception {
        Object converter = options.getConverter(CSharpList.class);
        System.out.println(label + " -> " + converter.getClass().getSimpleName()
            + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter));
    }
}
