import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;

public class ByteChar {
    public static void main(String[] args) throws Exception {
        String[] tests = {
            "{\"MyByte\":7,\"MyChar\":\"a\"}",
            "{\"MyInt32\":2,\"MyChar\":\"a\"}",
            "{\"MySByte\":8,\"MyChar\":\"a\"}",
            "{\"MyUInt16\":4,\"MyChar\":\"a\"}",
            "{\"MyInt16\":1,\"MyChar\":\"a\"}",
            "{\"MyInt64\":3,\"MyChar\":\"a\"}",
            "{\"MyChar\":\"a\",\"MyByte\":7}",
        };
        for (String j : tests) {
            try {
                SimpleTestClass o = (SimpleTestClass) JsonSerializer.deserialize(SimpleTestClass.class, j);
                System.out.println("OK  " + j + " char=[" + o.getMyChar() + "]");
            } catch (Throwable t) {
                System.out.println("ERR " + j + " => " + t.getClass().getSimpleName() + ": " + String.valueOf(t.getMessage()).substring(0, Math.min(70, String.valueOf(t.getMessage()).length())));
            }
        }
    }
}
