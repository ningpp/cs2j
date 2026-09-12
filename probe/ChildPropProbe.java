import io.github.ningpp.compat.PropertyInfo;

/** ChildClass hides an internal IsTransient with a public new one — which declaration wins? */
public class ChildPropProbe {

    public static void main(String[] args) throws Exception {
        for (String name : new String[] { "dotnet.text.json.Tests.ChildClass", "dotnet.text.json.Tests.ChildClassVirtual" }) {
            Class<?> type = Class.forName(name);
            System.out.println("== " + name);
            for (PropertyInfo p : PropertyInfo.getProperties(type, 54)) {
                System.out.println("   " + p.getName()
                    + " declaring=" + (p.getGetter() == null ? "?" : p.getGetter().getDeclaringClass().getSimpleName())
                    + " getter=" + p.getGetter()
                    + " annotations=" + (p.getGetter() == null ? "n/a" : java.util.Arrays.toString(p.getGetter().getAnnotations()))
                    + " setMethod=" + p.getSetMethod()
                    + " accessorPublic=" + p.getIsPublicAccessor());
            }
        }
    }
}
