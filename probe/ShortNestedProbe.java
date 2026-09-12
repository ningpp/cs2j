import com.fasterxml.jackson.core.type.TypeReference;
import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.JsonSerializerCompat;

/** List<List<short>> (C# short -> Java Short) — does the nested element converter work? */
public class ShortNestedProbe {

    public static void main(String[] args) {
        run("List<List<Short>>", new TypeReference<CSharpList<CSharpList<Short>>>() {}, "[[10,11],[20,21]]");
        run("List<List<Integer>>", new TypeReference<CSharpList<CSharpList<Integer>>>() {}, "[[10,11],[20,21]]");
        run("List<Short>", new TypeReference<CSharpList<Short>>() {}, "[10,11]");
    }

    static void run(String label, TypeReference<?> ref, String json) {
        try {
            Object value = JsonSerializerCompat.deserialize(ref, json);
            System.out.println(label + " -> " + value);
        } catch (Throwable t) {
            System.out.println(label + " -> " + t.getClass().getSimpleName() + ": "
                + String.valueOf(t.getMessage()).split("\n")[0]);
        }
    }
}
