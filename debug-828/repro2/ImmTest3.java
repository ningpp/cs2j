import io.github.ningpp.compat.JsonSerializerCompat;
import io.github.ningpp.compat.ImmutableList;
import dotnet.text.json.JsonSerializerOptions;

public class ImmTest3 {
    public static void main(String[] args) throws Exception {
        // 先污染缓存：无注册时创建 IImmutableList converter
        var opts = new JsonSerializerOptions();
        var c1 = opts.getConverter(io.github.ningpp.compat.IImmutableList.class);
        System.out.println("pre-polluted converter: " + c1.getClass().getName());
        // 再走 TypeRef
        try {
            Object v = JsonSerializerCompat.deserialize(new com.fasterxml.jackson.core.type.TypeReference<ImmutableList<Integer>>() {}, "[1,2]");
            System.out.println("OK: " + v + " first=" + ((ImmutableList)v).get(0).getClass().getName());
        } catch (Throwable t) {
            System.out.println("THROW: " + String.valueOf(t.getMessage()).substring(0, 90));
        }
    }
}
