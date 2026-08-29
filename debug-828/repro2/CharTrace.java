import dotnet.text.json.*;
import io.github.ningpp.compat.Encoding;
import java.lang.reflect.Field;

public class CharTrace {
    public static void main(String[] args) throws Exception {
        Field tok = Utf8JsonReader.class.getDeclaredField("_tokenType");
        Field consumed = Utf8JsonReader.class.getDeclaredField("_consumed");
        tok.setAccessible(true); consumed.setAccessible(true);

        String json = "{\"MyChar\":\"a\",\"MyByte\":7}";
        var opts = new JsonReaderOptions();
        var state = new JsonReaderState(opts);
        var reader = new Utf8JsonReader(new io.github.ningpp.compat.ReadOnlySpan<Integer>(Encoding.getUTF8().getBytes(json)), true, state.clone());
        while (true) {
            boolean ok = reader.read();
            System.out.println("read -> " + ok + " tok=" + tok.get(reader) + " consumed=" + consumed.get(reader));
            if (!ok) break;
            if (reader.getTokenType() == JsonTokenType.String) {
                String s = reader.getString();
                System.out.println("  getString=[" + s + "] consumed=" + consumed.get(reader));
            }
            if (reader.getTokenType() == JsonTokenType.Number) {
                int v = reader.getInt32();
                System.out.println("  getInt32=" + v + " consumed=" + consumed.get(reader));
            }
        }
    }
}
