import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.util.List;
import java.util.Map;

/**
 * Demo: TypeReference 匿名类在运行时保留完整泛型签名。
 * new TypeReference<List<String>>() {} 的 getType() 返回 ParameterizedType，
 * 泛型实参 String 在运行时完全可恢复——这正是 C# 泛型在转换到 Java 后
 * 丢失的信息。
 */
public class JacksonDemo {
    public static void main(String[] args) throws Exception {
        ObjectMapper mapper = new ObjectMapper();

        // C#: List<String> list = JsonSerializer.Deserialize<List<String>>(json);
        // Java 目标: 用 TypeReference 传递完整泛型类型
        TypeReference<List<String>> ref1 = new TypeReference<List<String>>() {};
        java.lang.reflect.Type t1 = ref1.getType();
        System.out.println("List<String> type = " + t1);
        System.out.println("  isParameterized = " + (t1 instanceof java.lang.reflect.ParameterizedType));
        if (t1 instanceof java.lang.reflect.ParameterizedType pt) {
            System.out.println("  raw = " + pt.getRawType());
            System.out.println("  args = " + java.util.Arrays.toString(pt.getActualTypeArguments()));
        }
        List<String> list = mapper.readValue("[\"a\",\"b\"]", ref1);
        System.out.println("list = " + list + " elemClass=" + list.get(0).getClass().getSimpleName());

        // 嵌套泛型: C# Deserialize<Map<String, List<Integer>>>(json)
        TypeReference<Map<String, List<Integer>>> ref2 = new TypeReference<Map<String, List<Integer>>>() {};
        java.lang.reflect.Type t2 = ref2.getType();
        System.out.println("Map<String,List<Integer>> = " + t2);
        Map<String, List<Integer>> map = mapper.readValue("{\"k\":[1,2]}", ref2);
        System.out.println("map = " + map + " valClass=" + map.get("k").get(0).getClass().getSimpleName());

        // 自定义泛型: C# Deserialize<ImmutableSet<ImmutableSet<Integer>>>(json)
        TypeReference<DemoSet<DemoSet<Integer>>> ref3 = new TypeReference<DemoSet<DemoSet<Integer>>>() {};
        System.out.println("DemoSet<DemoSet<Integer>> = " + ref3.getType());
    }

    public static class DemoSet<T> extends java.util.LinkedHashSet<T> {}
}
