import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayList;
import java.util.List;

/**
 * Prints the property (getter) order that io.github.ningpp.compat.PropertyInfo.getProperties
 * hands to the JSON serializer, so the order can be compared across JVM runs.
 */
public class PropOrderProbe {

    public static void main(String[] args) throws Exception {
        Class<?> type = Class.forName(args[0]);

        List<String> reflectionOrder = new ArrayList<>();
        for (Method m : type.getMethods()) {
            if (Modifier.isStatic(m.getModifiers())) continue;
            if (m.getParameterCount() != 0) continue;
            if (m.getReturnType() == void.class) continue;
            String n = m.getName();
            if ((n.startsWith("get") && n.length() > 3)
                    || (n.startsWith("is") && n.length() > 2
                        && (m.getReturnType() == boolean.class || m.getReturnType() == Boolean.class))) {
                reflectionOrder.add(n);
            }
        }
        System.out.println("getMethods      : " + reflectionOrder);

        List<String> declaredOrder = new ArrayList<>();
        for (Method m : type.getDeclaredMethods()) {
            if (Modifier.isStatic(m.getModifiers())) continue;
            if (m.getParameterCount() != 0) continue;
            if (m.getReturnType() == void.class) continue;
            String n = m.getName();
            if ((n.startsWith("get") && n.length() > 3)
                    || (n.startsWith("is") && n.length() > 2
                        && (m.getReturnType() == boolean.class || m.getReturnType() == Boolean.class))) {
                declaredOrder.add(n);
            }
        }
        System.out.println("getDeclaredMeths: " + declaredOrder);

        List<String> compatOrder = new ArrayList<>();
        for (Object p : io.github.ningpp.compat.PropertyInfo.getProperties(type)) {
            compatOrder.add(((io.github.ningpp.compat.PropertyInfo) p).getName());
        }
        System.out.println("compat order    : " + compatOrder);
    }
}
