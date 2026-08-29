import dotnet.text.json.*;
import io.github.ningpp.compat.Encoding;

public class DepthRepro3 {
    static String SCHEMA = "{\"age\":30,\"first\":\"John\",\"last\":\"Smith\",\"phoneNumbers\":[\"425-000-1212\",\"425-000-1213\"],\"address\":{\"street\":\"1MicrosoftWay\",\"city\":\"Redmond\",\"zip\":98052},\"IDs\":[425,-70,9223372036854775000],\"arrayWithObjects\":[\"text\",14,[],null,false,{},{\"time\":24},[\"1\",\"2\",\"3\"]],\"boolean\":false,\"null\":null,\"objectName\":{\"group\":{\"array\":[false],\"field\":\"simple\",\"anotherFieldNum\":5,\"anotherFieldBool\":true,\"lastField\":null}},\"emptyObject\":{}}";
    public static void main(String[] args) throws Exception {
        int depthPadding = 3;
        StringBuilder pre = new StringBuilder();
        StringBuilder suf = new StringBuilder();
        for (int i = 0; i < depthPadding; i++) {
            pre.append("{\n \"property").append(i).append("\": ");
            suf.append("\n}");
        }
        String json = pre.toString() + SCHEMA + suf.toString();
        var opts = new JsonReaderOptions();
        opts.setMaxDepth(256);
        var state = new JsonReaderState(opts);
        var reader = new Utf8JsonReader(new io.github.ningpp.compat.ReadOnlySpan<Integer>(Encoding.getUTF8().getBytes(json)), true, state.clone());
        int i = 0;
        try {
            while (reader.read()) {
                i++;
                if (i <= 30) System.out.println("tok" + i + "=" + reader.getTokenType() + " consumed=" + reader.getBytesConsumed());
                if (i > 80) break;
            }
            System.out.println("DONE at tok" + i);
        } catch (Throwable t) {
            System.out.println("THROW at tok" + i + ": " + String.valueOf(t.getMessage()).substring(0, Math.min(100, String.valueOf(t.getMessage()).length())));
            byte[] b = Encoding.getUTF8().getBytes(json);
            String m = String.valueOf(t.getMessage());
            int pos = -1;
            int idx = m.indexOf("BytePositionInLine:");
            if (idx >= 0) {
                String rest = m.substring(idx + "BytePositionInLine:".length()).trim();
                pos = Integer.parseInt(rest.replaceAll("[^0-9].*$", ""));
            }
            if (pos >= 0)
                System.out.println("around pos " + pos + ": [" + new String(b, Math.max(0,pos-15), Math.min(30, b.length-pos+15)) + "]");
        }
    }
}
