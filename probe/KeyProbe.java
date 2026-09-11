import io.github.ningpp.compat.TypeHelper;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.Type;

/** Prints the converter-cache key computed for two different member registrations. */
public final class KeyProbe {

    static Type closedGeneric(Class<?> raw, Class<?>... args) {
        return new ParameterizedType() {
            public Type[] getActualTypeArguments() { return args.clone(); }
            public Type getRawType() { return raw; }
            public Type getOwnerType() { return null; }
            public String toString() { return raw.getSimpleName() + java.util.Arrays.toString(args); }
        };
    }

    public static void main(String[] args) throws Exception {
        Class<?> list = Class.forName("io.github.ningpp.compat.CSharpList");

        TypeHelper.registerMemberType(closedGeneric(list, String.class));
        Object key1 = TypeHelper.converterCacheKey(list);
        System.out.println("after [String] : " + key1 + " hashCode=" + key1.hashCode());
        System.out.println("  getGenericArguments = " + java.util.Arrays.toString(TypeHelper.getGenericArguments(list)));


        TypeHelper.registerMemberType(closedGeneric(list, Integer.class));
        Object key2 = TypeHelper.converterCacheKey(list);
        System.out.println("after [Integer]: " + key2 + " hashCode=" + key2.hashCode());
        System.out.println("  getGenericArguments = " + java.util.Arrays.toString(TypeHelper.getGenericArguments(list)));

        System.out.println("keys equal? " + key1.equals(key2));
    }
}
