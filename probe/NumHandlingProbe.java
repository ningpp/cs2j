import dotnet.text.json.JsonClassInfo;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.JsonNumberHandlingAttribute;
import dotnet.text.json.Serialization.Tests.NumberHandlingTests;
import io.github.ningpp.compat.TypeHelper;

/** Does the type-level [JsonNumberHandling] reach JsonClassInfo as a materialized attribute? */
public class NumHandlingProbe {

    public static void main(String[] args) throws Exception {
        Class<?> customList = Class.forName("dotnet.text.json.Serialization.Tests.NumberHandlingTests$MyCustomList");
        System.out.println("annotation present = "
            + customList.isAnnotationPresent(io.github.ningpp.compat.CompatJsonNumberHandling.class));

        Object[] attrs = TypeHelper.getCustomAttributes(customList, JsonNumberHandlingAttribute.class, false);
        System.out.println("TypeHelper attrs = " + java.util.Arrays.toString(attrs));

        Object viaOptions = JsonSerializerOptions.getAttributeThatCanHaveMultiple(customList, JsonNumberHandlingAttribute.class);
        System.out.println("getAttributeThatCanHaveMultiple = " + viaOptions
            + (viaOptions == null ? "" : " handling=" + ((JsonNumberHandlingAttribute) viaOptions).getHandling()));

        try {
            java.lang.reflect.Method m = JsonClassInfo.class.getDeclaredMethod("getNumberHandlingForType", Class.class);
            m.setAccessible(true);
            System.out.println("getNumberHandlingForType = " + m.invoke(null, customList));
        } catch (Throwable t) {
            System.out.println("getNumberHandlingForType failed: " + t);
        }
    }
}
