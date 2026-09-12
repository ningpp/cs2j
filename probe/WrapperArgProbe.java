import com.fasterxml.jackson.core.type.TypeReference;
import io.github.ningpp.compat.TypeHelper;

/** What does the compat layer register for a generic wrapper whose argument is an array? */
public class WrapperArgProbe {

    public static void main(String[] args) throws Exception {
        java.lang.reflect.Type closed = new TypeReference<dotnet.text.json.Serialization.Tests.GenericListWrapper<String[]>>() {}.getType();
        System.out.println("closed = " + closed);
        if (closed instanceof java.lang.reflect.ParameterizedType pt) {
            System.out.println("raw = " + pt.getRawType());
            for (java.lang.reflect.Type a : pt.getActualTypeArguments()) {
                System.out.println("  arg = " + a + " (" + a.getClass().getName() + ") -> raw "
                    + TypeHelper.toRawClass(a));
            }
        }
    }
}
