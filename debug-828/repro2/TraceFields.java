import dotnet.text.json.*;
import io.github.ningpp.compat.Encoding;
import java.lang.reflect.Field;

public class TraceFields {
    public static void main(String[] args) throws Exception {
        Field tok = Utf8JsonReader.class.getDeclaredField("_tokenType");
        Field prev = Utf8JsonReader.class.getDeclaredField("_previousTokenType");
        Field consumed = Utf8JsonReader.class.getDeclaredField("_consumed");
        tok.setAccessible(true); prev.setAccessible(true); consumed.setAccessible(true);

        String json = "  /* comment */  /* comment */  " + "  true  /* comment */ 5 ";
        var opts = new JsonReaderOptions();
        opts.setCommentHandling(JsonCommentHandling.Allow);
        var state = new JsonReaderState(opts);
        var reader = new Utf8JsonReader(new io.github.ningpp.compat.ReadOnlySpan<Integer>(Encoding.getUTF8().getBytes(json)), true, state.clone());
        for (int i = 1; i <= 4; i++) {
            try {
                boolean ok = reader.read();
                System.out.println("read" + i + " -> " + ok + " tok=" + tok.get(reader) + " prev=" + prev.get(reader) + " consumed=" + consumed.get(reader));
                if (!ok) break;
            } catch (Throwable t) {
                System.out.println("read" + i + " -> THROW tok=" + tok.get(reader) + " prev=" + prev.get(reader) + " consumed=" + consumed.get(reader) + " : " + String.valueOf(t.getMessage()).substring(0, Math.min(70, String.valueOf(t.getMessage()).length())));
                break;
            }
        }
    }
}
