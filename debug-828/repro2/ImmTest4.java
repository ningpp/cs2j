import io.github.ningpp.compat.JsonSerializerCompat;
import io.github.ningpp.compat.ImmutableList;
import dotnet.text.json.JsonSerializerOptions;

public class ImmTest4 {
    public static void main(String[] args) throws Exception {
        // 无注册时创建 ImmutableList converter 并缓存
        var opts = new JsonSerializerOptions();
        var c1 = opts.getConverter(ImmutableList.class);
        System.out.println("pre-polluted: " + c1.getClass().getName());
        // 用同一 options 走 TypeRef（缓存命中）
        Object v = JsonSerializerCompat.deserialize(new com.fasterxml.jackson.core.type.TypeReference<ImmutableList<Integer>>() {}, "[1,2]");
        System.out.println("OK: first=" + ((ImmutableList)v).get(0).getClass().getName());
        // 无注册 deserialize（模拟其他测试）
        Object v2 = JsonSerializerCompat.deserialize(new com.fasterxml.jackson.core.type.TypeReference<ImmutableList<Integer>>() {}, "[3,4]");
        System.out.println("OK2: first=" + ((ImmutableList)v2).get(0).getClass().getName());
    }
}
