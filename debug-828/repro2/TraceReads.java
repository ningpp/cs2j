import dotnet.text.json.*;
import io.github.ningpp.compat.Encoding;

public class TraceReads {
    public static void main(String[] args) throws Exception {
        String json = "  /* comment */  /* comment */  " + "  true  /* comment */ 5 ";
        for (JsonCommentHandling ch : new JsonCommentHandling[]{JsonCommentHandling.Skip, JsonCommentHandling.Allow}) {
            for (boolean finalBlock : new boolean[]{false, true}) {
                var opts = new JsonReaderOptions();
                opts.setCommentHandling(ch);
                var state = new JsonReaderState(opts);
                var reader = new Utf8JsonReader(new io.github.ningpp.compat.ReadOnlySpan<Integer>(Encoding.getUTF8().getBytes(json)), finalBlock, state.clone());
                StringBuilder sb = new StringBuilder();
                sb.append("ch=").append(ch).append(" final=").append(finalBlock).append(" : ");
                try {
                    for (int i = 0; i < 6; i++) {
                        boolean ok = reader.read();
                        sb.append("[read->").append(ok).append(" tok=").append(reader.getTokenType()).append("] ");
                        if (!ok) break;
                    }
                } catch (Throwable t) {
                    sb.append("THROW: ").append(String.valueOf(t.getMessage()).substring(0, Math.min(60, String.valueOf(t.getMessage()).length())));
                }
                System.out.println(sb);
            }
        }
    }
}
