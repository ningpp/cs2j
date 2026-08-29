import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SimpleTestClass;

public class SimpleU64e {
    public static void main(String[] args) throws Exception {
        String full = SimpleTestClass.s_json;
        // 用 Jackson 或手写：按顶层逗号切分（s_json 无嵌套逗号歧义——有嵌套对象但顶层逗号在 depth 1）
        java.util.List<String> chunks = new java.util.ArrayList<>();
        int depth = 0; int segStart = 1;
        for (int i = 1; i < full.length() - 1; i++) {
            char c = full.charAt(i);
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
            if (c == ',' && depth == 0) {
                chunks.add(full.substring(segStart, i));
                segStart = i + 1;
            }
        }
        chunks.add(full.substring(segStart, full.length() - 1));
        System.out.println("chunks=" + chunks.size());
        String acc = "{";
        for (int i = 0; i < chunks.size(); i++) {
            String chunk = chunks.get(i);
            String name = chunk.substring(chunk.indexOf('"') + 1, chunk.indexOf('"', chunk.indexOf('"') + 1));
            if (acc.length() > 1) acc += ",";
            acc += chunk;
            try {
                SimpleTestClass o = (SimpleTestClass) JsonSerializer.deserialize(SimpleTestClass.class, acc + "}");
            } catch (Throwable t) {
                System.out.println("FAIL after [" + name + "] (#" + (i+1) + ") => " + t.getClass().getSimpleName() + ": " + String.valueOf(t.getMessage()).substring(0, Math.min(60, String.valueOf(t.getMessage()).length())));
                break;
            }
        }
        System.out.println("all passed");
    }
}
