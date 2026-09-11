import dotnet.text.json.JsonSerializer;
import io.github.ningpp.compat.ArrayHelper;
import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.TypeHelper;

import java.lang.reflect.ParameterizedType;
import java.lang.reflect.Type;

/**
 * One case per JVM run, so the serializer caches of an earlier case cannot mask the effect
 * of publishing type arguments at the call site.
 *
 * <pre>
 *   java SessionTypeArgProbe write-plain | write-reg
 *   java SessionTypeArgProbe read-plain  | read-reg
 *   java SessionTypeArgProbe nested-write-reg
 * </pre>
 */
public final class SessionTypeArgProbe {

    static Type closed(Class<?> raw, Type... args) {
        return new ParameterizedType() {
            public Type[] getActualTypeArguments() { return args.clone(); }
            public Type getRawType() { return raw; }
            public Type getOwnerType() { return null; }
            public String toString() { return raw.getSimpleName() + java.util.Arrays.toString(args); }
        };
    }

    static CSharpList<Integer> ints(Integer... values) {
        return new CSharpList<>(ArrayHelper.toList(values));
    }

    public static void main(String[] args) {
        String mode = args.length > 0 ? args[0] : "write-plain";
        try {
            switch (mode) {
                case "write-plain" -> System.out.println(
                    "write-plain  : " + JsonSerializer.serialize(CSharpList.class, ints(1, 2), null));
                case "write-reg" -> {
                    TypeHelper.registerParameterizedType(closed(CSharpList.class, Integer.class));
                    System.out.println("write-reg    : " + JsonSerializer.serialize(CSharpList.class, ints(1, 2), null));
                }
                case "nested-write-reg" -> {
                    TypeHelper.registerParameterizedType(
                        closed(CSharpList.class, closed(CSharpList.class, Integer.class)));
                    System.out.println("nested-write : " + JsonSerializer.serialize(CSharpList.class,
                        new CSharpList<>(ArrayHelper.toList(ints(1, 2), ints(3, 4))), null));
                }
                case "read-plain" -> System.out.println(
                    "read-plain   : " + describe(JsonSerializer.deserialize("[1,2]", null, CSharpList.class)));
                case "read-reg" -> {
                    TypeHelper.registerParameterizedType(closed(CSharpList.class, Integer.class));
                    System.out.println("read-reg     : " + describe(JsonSerializer.deserialize("[1,2]", null, CSharpList.class)));
                }
                case "read-reg-after-lookup" -> {
                    // Warm the options/class cache first, then register: mirrors a call site that
                    // publishes arguments only right before the deserialize call.
                    JsonSerializer.deserialize("[]", null, CSharpList.class);
                    TypeHelper.registerParameterizedType(closed(CSharpList.class, Integer.class));
                    System.out.println("read-reg2    : " + describe(JsonSerializer.deserialize("[1,2]", null, CSharpList.class)));
                }
                case "peek-nested" -> {
                    TypeHelper.registerParameterizedType(
                        closed(CSharpList.class, closed(CSharpList.class, Integer.class)));
                    for (int i = 1; i <= 3; i++) {
                        System.out.println("peek-nested " + i + ": " + java.util.Arrays.toString(
                            TypeHelper.getGenericArguments(CSharpList.class)));
                    }
                }
                default -> System.out.println("unknown mode " + mode);
            }
        } catch (Throwable t) {
            System.out.println(mode + " -> THREW " + t.getClass().getName() + ": " + t.getMessage());
        }
    }

    static String describe(Object value) {
        if (value instanceof java.util.List<?> list) {
            StringBuilder sb = new StringBuilder("[");
            for (Object element : list) {
                sb.append(element == null ? "null" : element.getClass().getSimpleName()).append(' ');
            }
            return value + " elements=" + sb.toString().trim() + "]";
        }
        return String.valueOf(value);
    }
}
