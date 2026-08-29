import dotnet.text.json.*;
import io.github.ningpp.compat.Encoding;

public class DepthRepro2 {
    static String SCHEMA = "{\"age\":30,\"first\":\"John\",\"last\":\"Smith\",\"phoneNumbers\":[\"425-000-1212\"],\"address\":{\"street\":\"1Way\",\"city\":\"Redmond\",\"zip\":98052},\"IDs\":[425,-70],\"arrayWithObjects\":[\"text\",14,[],null,false,{},{\"time\":24},[\"1\",\"2\"]],\"boolean\":false,\"null\":null,\"objectName\":{\"group\":{\"array\":[false],\"field\":\"simple\"}},\"emptyObject\":{}}";
    public static void main(String[] args) throws Exception {
        int depthPadding = 3;
        StringBuilder pre = new StringBuilder();
        StringBuilder suf = new StringBuilder();
        for (int i = 0; i < depthPadding; i++) {
            pre.append("{\n \"property").append(i).append("\": ");
            suf.append("\n}");
        }
        String json = pre.toString() + SCHEMA + suf.toString();
        System.out.println("JSON length=" + json.length() + " prefix=[" + pre + "]");
        var opts = new JsonReaderOptions();
        opts.setMaxDepth(256);
        var state = new JsonReaderState(opts);
        var reader = new Utf8JsonReader(new io.github.ningpp.compat.ReadOnlySpan<Integer>(Encoding.getUTF8().getBytes(json)), true, state.clone());
        int i = 0;
        try {
            while (reader.read()) {
                i++;
                if (i <= 12) System.out.println("tok" + i + "=" + reader.getTokenType() + " consumed=" + reader.getBytesConsumed());
                if (i > 60) break;
            }
        } catch (Throwable t) {
            System.out.println("THROW at tok" + i + ": " + String.valueOf(t.getMessage()).substring(0, Math.min(90, String.valueOf(t.getMessage()).length())));
        }
    }
}
