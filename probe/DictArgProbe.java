import dotnet.text.json.*;
import io.github.ningpp.compat.*;

public class DictArgProbe {
    static void show(String label, Class<?> t) {
        System.out.println(label + " " + t.getSimpleName()
            + " -> getGenericArguments=" + java.util.Arrays.toString(TypeHelper.getGenericArguments(t)));
    }

    public static void main(String[] args) throws Exception {
        Class<?>[] targets = {
            Class.forName("io.github.ningpp.compat.CSharpGenericIDictionary"),
            Class.forName("io.github.ningpp.compat.CSharpDictionary"),
            Class.forName("java.util.concurrent.ConcurrentHashMap"),
        };
        String json = "{\"Key\":1}";
        for (Class<?> t : targets) {
            System.out.println("=== target " + t.getName());
            // mimic the test: bare Class token, fresh options each time
            dotnet.text.json.JsonSerializerOptions options = new dotnet.text.json.JsonSerializerOptions();
            try {
                Object v = dotnet.text.json.JsonSerializer.deserialize(json, options, t);
                System.out.println("  deserialize OK -> " + (v == null ? "null" : v.getClass().getName()));
            } catch (Throwable ex) {
                System.out.println("  deserialize FAIL " + ex);
            }
            show("  after", t);
            try {
                System.out.println("  serialize -> " + dotnet.text.json.JsonSerializer.serialize(Object.class, "{\"Key\":1}", options));
            } catch (Throwable ex) {
                System.out.println("  serialize FAIL " + ex);
            }
        }
    }
}
