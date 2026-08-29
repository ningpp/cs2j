import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;
import io.github.ningpp.compat.Encoding;

public class SimpleU64d {
    public static void main(String[] args) throws Exception {
        String full = SimpleTestClass.s_json;
        int[] cuts = {200, 300, 400, 441, 480, 520, 560, 600};
        for (int cut : cuts) {
            if (cut > full.length()) continue;
            String j = full.substring(0, cut);
            if (!j.endsWith("}")) j += "}";
            try {
                SimpleTestClass o = (SimpleTestClass) JsonSerializer.deserialize(SimpleTestClass.class, j);
                System.out.println("OK  cut=" + cut);
            } catch (Throwable t) {
                System.out.println("ERR cut=" + cut + " => " + t.getClass().getSimpleName() + ": " + String.valueOf(t.getMessage()).substring(0, Math.min(55, String.valueOf(t.getMessage()).length())));
            }
        }
        // 完整
        try {
            SimpleTestClass o = (SimpleTestClass) JsonSerializer.deserialize(SimpleTestClass.class, full);
            System.out.println("OK full");
        } catch (Throwable t) {
            System.out.println("ERR full => " + t.getClass().getSimpleName() + ": " + String.valueOf(t.getMessage()).substring(0, Math.min(60, String.valueOf(t.getMessage()).length())));
        }
    }
}
