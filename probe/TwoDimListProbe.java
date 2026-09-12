import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;

/** Isolate the failing member: SimpleTestClass.MyInt16TwoDimensionList (List<List<short>>). */
public class TwoDimListProbe {

    public static void main(String[] args) {
        run("full SimpleTestClass", SimpleTestClass.s_json);
        run("only two-dim list", "{\"MyInt16TwoDimensionList\":[[10,11],[20,21]]}");
        run("only string list", "{\"MyStringList\":[\"Hello\"]}");
        run("only int32 array", "{\"MyInt32Array\":[2]}");
    }

    static void run(String label, String json) {
        try {
            Object value = JsonSerializer.deserialize(json, null, SimpleTestClass.class);
            System.out.println(label + " -> OK");
        } catch (Throwable t) {
            String msg = String.valueOf(t.getMessage()).split("\n")[0];
            System.out.println(label + " -> " + t.getClass().getSimpleName() + ": " + msg);
        }
    }
}
