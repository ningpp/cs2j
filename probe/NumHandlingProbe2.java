import dotnet.text.json.Serialization.JsonNumberHandlingAttribute;
import dotnet.text.json.Serialization.Tests.NumberHandlingTests;
import io.github.ningpp.compat.CompatJsonNumberHandling;

public class NumHandlingProbe2 {
    public static void main(String[] a) throws Exception {
        Class<?> customList = Class.forName("dotnet.text.json.Serialization.Tests.NumberHandlingTests$MyCustomList");
        CompatJsonNumberHandling marker = customList.getAnnotation(CompatJsonNumberHandling.class);
        System.out.println("marker = " + marker + (marker == null ? "" : " value=" + marker.value()));
        System.out.println("attributeType simpleName = " + JsonNumberHandlingAttribute.class.getSimpleName());
        System.out.println("attributeType from = " + JsonNumberHandlingAttribute.class.getProtectionDomain().getCodeSource());
        try {
            java.lang.reflect.Constructor<?> ctor = JsonNumberHandlingAttribute.class.getDeclaredConstructor(int.class);
            ctor.setAccessible(true);
            System.out.println("constructed = " + ctor.newInstance(3));
        } catch (Throwable t) {
            System.out.println("ctor failed: " + t);
        }
        System.out.println("declared ctors = " + java.util.Arrays.toString(JsonNumberHandlingAttribute.class.getDeclaredConstructors()));
    }
}
