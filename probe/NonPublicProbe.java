import dotnet.text.json.Serialization.Tests.PropertyVisibilityTests;
import io.github.ningpp.compat.PropertyInfo;

/** Is the non-public marker visible and does the setter gate answer null? */
public class NonPublicProbe {

    public static void main(String[] args) throws Exception {
        for (String name : new String[] {
                "ClassWithPrivateSetterAndGetter",
                "MyStruct_WithNonPublicAccessors_WithTypeAttribute" }) {
            Class<?> type = Class.forName("dotnet.text.json.Serialization.Tests.PropertyVisibilityTests$" + name);
            System.out.println("== " + name);
            for (PropertyInfo p : PropertyInfo.getProperties(type, 54)) {
                String getterAnn = p.getGetter() == null ? "no getter"
                    : java.util.Arrays.toString(p.getGetter().getAnnotations());
                System.out.println("   " + p.getName()
                    + " getter=" + getterAnn
                    + " setMethod=" + p.getSetMethod()
                    + " isPublicAccessor=" + p.getIsPublicAccessor());
            }
        }
    }
}
