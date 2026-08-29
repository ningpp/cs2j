import dotnet.text.json.JsonSerializer;
import io.github.ningpp.compat.Encoding;

public class ByteArrayWrite {
    public static void main(String[] args) throws Exception {
        byte[] data = new byte[] { 1, 2, 3, 4, 5, 6 };
        try {
            String json = JsonSerializer.serialize_0tp(data, (Class) byte[].class);
            System.out.println("byte[] serialize: " + json);
        } catch (Throwable t) {
            System.out.println("THROW: " + t);
        }
        // converter 解析
        var opts = new dotnet.text.json.JsonSerializerOptions();
        try {
            var c = opts.getConverter(byte[].class);
            System.out.println("converter for byte[]: " + c.getClass().getName());
        } catch (Throwable t) {
            System.out.println("converter THROW: " + t);
        }
    }
}
