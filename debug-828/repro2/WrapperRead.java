import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.ArrayListWrapper;

public class WrapperRead {
    public static void main(String[] args) throws Exception {
        try {
            Object v = JsonSerializer.deserialize(ArrayListWrapper.class, "[1,2,3]");
            System.out.println("OK: " + v + " type=" + v.getClass().getName());
        } catch (Throwable t) {
            System.out.println("THROW: " + t);
            t.printStackTrace(System.out);
        }
    }
}
