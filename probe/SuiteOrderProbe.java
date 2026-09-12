import com.fasterxml.jackson.core.type.TypeReference;
import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Tests.StringStackWrapper;
import dotnet.text.json.Serialization.Tests.StringIListWrapper;
import io.github.ningpp.compat.CSharpStack;
import io.github.ningpp.compat.JsonSerializerCompat;
import io.github.ningpp.compat.TypeHelper;

/**
 * Reproduces the "passes alone, fails in the suite" pair: stackTOfStackT runs before
 * readSimpleGenericStack and converts CSharpStack&lt;CSharpStack&lt;Integer&gt;&gt; with a raw
 * Class token, which is the heap-pollution shape the suite uses.
 *
 * <pre>
 *   java SuiteOrderProbe alone
 *   java SuiteOrderProbe suite
 * </pre>
 */
public class SuiteOrderProbe {

    public static void main(String[] args) throws Exception {
        String mode = args.length > 0 ? args[0] : "alone";
        JsonSerializerOptions options = new JsonSerializerOptions();
        if (mode.equals("suite")) {
            // stackTOfStackT: raw Class token cast to the nested closed generic.
            Class<CSharpStack<CSharpStack<Integer>>> token =
                (Class<CSharpStack<CSharpStack<Integer>>>) (Class<?>) CSharpStack.class;
            Object nested = JsonSerializerCompat.deserialize(
                new TypeReference<CSharpStack<CSharpStack<Integer>>>() {},
                "[[1,2],[3,4]]", options);
            System.out.println("nested stack -> " + nested);
            System.out.println("re-serialized  -> " + serializeRaw(token, nested, options));
        }
        describe("CSharpStack converter", options.getConverter(CSharpStack.class));
        readStack(options);
        readList(options);
    }

    @SuppressWarnings({"unchecked", "rawtypes"})
    static String serializeRaw(Class token, Object value, JsonSerializerOptions options) {
        return JsonSerializer.serialize(token, value, options);
    }

    static void describe(String label, Object converter) throws Exception {
        System.out.println(label + " = " + converter.getClass().getSimpleName()
            + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter));
    }

    static void readStack(JsonSerializerOptions options) {
        try {
            StringStackWrapper result = (StringStackWrapper) JsonSerializer.deserialize(
                "[\"1\",\"2\"]", options, StringStackWrapper.class);
            StringBuilder sb = new StringBuilder();
            for (String s : result) {
                sb.append(s).append('/').append(s == null ? "null" : s.getClass().getSimpleName()).append(' ');
            }
            System.out.println("stack elements = " + sb);
        } catch (Throwable t) {
            System.out.println("stack FAILED: " + t);
        }
    }

    static void readList(JsonSerializerOptions options) {
        try {
            StringIListWrapper result = (StringIListWrapper) JsonSerializer.deserialize(
                "[\"1\",\"2\"]", options, StringIListWrapper.class);
            StringBuilder sb = new StringBuilder();
            for (String s : result) {
                sb.append(s).append('/').append(s == null ? "null" : s.getClass().getSimpleName()).append(' ');
            }
            System.out.println("list elements = " + sb);
        } catch (Throwable t) {
            System.out.println("list FAILED: " + t);
        }
    }
}
