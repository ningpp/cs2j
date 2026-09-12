import com.fasterxml.jackson.core.type.TypeReference;
import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Tests.StringStackWrapper;
import dotnet.text.json.Serialization.Tests.StringIListWrapper;
import io.github.ningpp.compat.CSharpStack;
import io.github.ningpp.compat.JsonSerializerCompat;

/**
 * Does converting the raw interface first decide the element converter for every later
 * implementation of it? JsonSerializerOptions._converters is keyed by the raw Class.
 *
 * <pre>
 *   java CachePollutionProbe clean       # wrapper only
 *   java CachePollutionProbe polluted    # raw interface first, then the wrapper
 * </pre>
 */
public class CachePollutionProbe {

    public static void main(String[] args) {
        String mode = args.length > 0 ? args[0] : "clean";
        if (mode.equals("polluted")) {
            Object raw = JsonSerializerCompat.deserialize(
                new TypeReference<CSharpStack<Integer>>() {}, "[1,2]");
            System.out.println("raw stack -> " + raw);
        }
        readStack();
        readList();
    }

    static void readStack() {
        try {
            StringStackWrapper result = (StringStackWrapper) JsonSerializer.deserialize(
                "[\"1\",\"2\"]", null, StringStackWrapper.class);
            StringBuilder sb = new StringBuilder();
            for (String s : result) {
                sb.append(s).append('/').append(s == null ? "null" : s.getClass().getSimpleName()).append(' ');
            }
            System.out.println("stack elements = " + sb);
        } catch (Throwable t) {
            System.out.println("stack FAILED: " + t);
        }
    }

    static void readList() {
        try {
            StringIListWrapper result = (StringIListWrapper) JsonSerializer.deserialize(
                "[\"1\",\"2\"]", null, StringIListWrapper.class);
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
