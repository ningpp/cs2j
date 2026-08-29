import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;

public class SimpleU64b {
    public static void main(String[] args) throws Exception {
        try {
            SimpleTestClass o = (SimpleTestClass) JsonSerializer.deserialize(SimpleTestClass.class, SimpleTestClass.s_json);
            System.out.println("OK");
        } catch (Throwable t) {
            t.printStackTrace(System.out);
        }
    }
}
