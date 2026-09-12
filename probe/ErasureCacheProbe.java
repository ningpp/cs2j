import com.fasterxml.jackson.core.type.TypeReference;
import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.StringIListWrapper;
import io.github.ningpp.compat.JsonSerializerCompat;
import io.github.ningpp.compat.TypeHelper;

/**
 * Two entry paths for the same concrete list class:
 *  - class literal  → JsonSerializer.deserialize(json, options, Class)
 *  - TypeReference  → JsonSerializerCompat.deserialize(TypeReference, json) publishes the
 *                     concrete type, so the erased interface can report its argument.
 */
public class ErasureCacheProbe {

    public static void main(String[] args) {
        String mode = args.length > 0 ? args[0] : "class-literal";
        switch (mode) {
            case "class-literal" -> {
                System.out.println("class-literal:");
                StringIListWrapper result = (StringIListWrapper) JsonSerializer.deserialize("[\"1\",\"2\"]", null, StringIListWrapper.class);
                for (String s : result) {
                    System.out.println("  element=" + s + " (" + (s == null ? "null" : s.getClass().getSimpleName()) + ")");
                }
            }
            case "type-reference" -> {
                System.out.println("type-reference:");
                StringIListWrapper result = (StringIListWrapper) JsonSerializerCompat.deserialize(
                    new TypeReference<StringIListWrapper>() {}, "[\"1\",\"2\"]");
                for (String s : result) {
                    System.out.println("  element=" + s + " (" + (s == null ? "null" : s.getClass().getSimpleName()) + ")");
                }
            }
            case "lookup" -> {
                TypeHelper.registerConcreteType(StringIListWrapper.class);
                System.out.println("getGenericArguments(CSharpGenericIList) = "
                    + java.util.Arrays.toString(TypeHelper.getGenericArguments(
                        io.github.ningpp.compat.CSharpGenericIList.class)));
            }
            default -> System.out.println("unknown mode");
        }
    }
}
