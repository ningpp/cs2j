import dotnet.text.json.Serialization.JsonNumberHandlingAttribute;
import io.github.ningpp.compat.TypeHelper;

public class NumHandlingProbe3 {
    public static void main(String[] a) throws Exception {
        Class<?> customList = Class.forName("dotnet.text.json.Serialization.Tests.NumberHandlingTests$MyCustomList");
        System.out.println("isAnnotation = " + JsonNumberHandlingAttribute.class.isAnnotation());
        System.out.println("getAnnotationsByType length = "
            + customList.getAnnotationsByType((Class) JsonNumberHandlingAttribute.class).length);
        Object[] attrs = TypeHelper.getCustomAttributes(customList, JsonNumberHandlingAttribute.class, false);
        System.out.println("TypeHelper attrs length = " + attrs.length);
        // Call the private materializer directly to isolate it.
        java.lang.reflect.Method m = TypeHelper.class.getDeclaredMethod(
            "materializeTypeAttributeFromCompatAnnotation", Class.class, Class.class);
        m.setAccessible(true);
        System.out.println("materializer -> " + m.invoke(null, customList, JsonNumberHandlingAttribute.class));
    }
}
