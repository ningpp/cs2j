import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;

public class SimpleU64c {
    public static void main(String[] args) throws Exception {
        String[] tests = {
            "{\"MyUInt64Enum\":18446744073709551615}",
            "{\"MyUInt64\":6}",
            "{\"MyByte\":7}",
            "{\"MySByte\":8}",
            "{\"MyEnum\":2}",
            "{\"MyInt64Enum\":-9223372036854775808}",
            "{\"MyUInt64Enum\":18446744073709551615,\"MyInt32\":2}",
        };
        for (String j : tests) {
            try {
                SimpleTestClass o = (SimpleTestClass) JsonSerializer.deserialize(SimpleTestClass.class, j);
                System.out.println("OK  " + j + " u64enum=" + o.getMyUInt64Enum());
            } catch (Throwable t) {
                System.out.println("ERR " + j + " => " + t.getClass().getSimpleName() + ": " + String.valueOf(t.getMessage()).substring(0, Math.min(60, String.valueOf(t.getMessage()).length())));
            }
        }
    }
}
