import dotnet.text.json.*;
import io.github.ningpp.compat.Encoding;

public class DepthRepro {
    public static void main(String[] args) throws Exception {
        String schema = "{\"Age\":30,\"FirstName\":\"John\"}";  // 模拟 FullJsonSchema1
        String json = "{\n \"property0\": " + schema + "\n}";
        var opts = new JsonReaderOptions();
        var state = new JsonReaderState(opts);
        var reader = new Utf8JsonReader(new io.github.ningpp.compat.ReadOnlySpan<Integer>(Encoding.getUTF8().getBytes(json)), true, state.clone());
        int i = 0;
        try {
            while (reader.read()) {
                System.out.println("tok" + (++i) + "=" + reader.getTokenType() + " consumed=" + reader.getBytesConsumed());
                if (i > 20) break;
            }
        } catch (Throwable t) {
            System.out.println("THROW at tok" + i + ": " + String.valueOf(t.getMessage()).substring(0, Math.min(80, String.valueOf(t.getMessage()).length())));
        }
    }
}
