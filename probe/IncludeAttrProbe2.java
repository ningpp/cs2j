import dotnet.text.json.Serialization.Tests.PropertyVisibilityTests;
import io.github.ningpp.compat.PropertyInfo;

/** What does the property enumeration return for a private-accessor property? */
public class IncludeAttrProbe2 {

    public static void main(String[] args) throws Exception {
        Class<?> type = Class.forName(
            "dotnet.text.json.Serialization.Tests.PropertyVisibilityTests$ClassWithPrivateProperty_WithJsonIncludeProperty");
        System.out.println("declared methods:");
        for (java.lang.reflect.Method m : type.getDeclaredMethods()) {
            System.out.println("   " + m + " annotations=" + java.util.Arrays.toString(m.getAnnotations()));
        }
        System.out.println("getProperties(type, 54) count = " + count(type, 54));
        System.out.println("getProperties(type, 38) count = " + count(type, 38));
        System.out.println("getProperties(type, 0xFFFF) count = " + count(type, 0xFFFF));
    }

    static int count(Class<?> type, int flags) {
        int n = 0;
        for (PropertyInfo p : PropertyInfo.getProperties(type, flags)) {
            n++;
            System.out.println("   property " + p.getName() + " getter=" + p.getGetter());
        }
        return n;
    }
}
