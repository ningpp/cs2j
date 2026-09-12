import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;

/** Standalone vs inside-a-collection: is MySByteArray read differently? */
public class SByteMemberProbe {

    public static void main(String[] args) {
        try {
            Object direct = JsonSerializer.deserialize(SimpleTestClass.s_json, null, SimpleTestClass.class);
            System.out.println("direct OK: " + direct);
        } catch (Throwable t) {
            System.out.println("direct FAILED: " + t);
        }
        try {
            Object inList = JsonSerializer.deserialize("[" + SimpleTestClass.s_json + "]", null,
                io.github.ningpp.compat.CSharpList.class);
            System.out.println("inList OK: " + inList);
        } catch (Throwable t) {
            System.out.println("inList FAILED: " + t);
        }
    }
}
