import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;

public class SimpleU64 {
    public static void main(String[] args) throws Exception {
        try {
            SimpleTestClass o = (SimpleTestClass) JsonSerializer.deserialize(SimpleTestClass.class, SimpleTestClass.s_json);
            System.out.println("OK MyUInt64Enum=" + o.getMyUInt64Enum());
        } catch (Throwable t) {
            System.out.println("THROW: " + String.valueOf(t.getMessage()).substring(0, Math.min(100, String.valueOf(t.getMessage()).length())));
        }
        // 只反序列化 MyUInt64Enum
        try {
            Object v = JsonSerializer.deserialize(dotnet.text.json.Serialization.Tests.SampleEnumUInt64.class, "18446744073709551615");
            System.out.println("solo OK: " + v);
        } catch (Throwable t) {
            System.out.println("solo THROW: " + String.valueOf(t.getMessage()).substring(0, Math.min(100, String.valueOf(t.getMessage()).length())));
        }
    }
}
