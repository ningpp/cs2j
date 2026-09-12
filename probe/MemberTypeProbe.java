import io.github.ningpp.compat.PropertyInfo;
import io.github.ningpp.compat.TypeHelper;
import io.github.ningpp.compat.CSharpQueue;

/**
 * Does reading a member's declared type publish its type arguments, so the converter factory
 * can resolve the element type? (The failing converter has elementType=Object for a
 * CSharpQueue&lt;SimpleTestClass&gt; member.)
 */
public class MemberTypeProbe {

    public static class Holder {
        private CSharpQueue<dotnet.text.json.Serialization.Tests.SimpleTestClass> myQueue;

        public CSharpQueue<dotnet.text.json.Serialization.Tests.SimpleTestClass> getMyQueue() {
            return myQueue;
        }

        public void setMyQueue(CSharpQueue<dotnet.text.json.Serialization.Tests.SimpleTestClass> value) {
            this.myQueue = value;
        }
    }

    public static void main(String[] args) throws Exception {
        PropertyInfo property = null;
        for (PropertyInfo candidate : PropertyInfo.getProperties(Holder.class, 54)) {
            if (candidate.getName().equals("MyQueue")) {
                property = candidate;
            }
        }
        System.out.println("property = " + property);
        if (property == null) {
            return;
        }
        System.out.println("getter = " + property.getGetter());
        System.out.println("getter generic return = "
            + (property.getGetter() == null ? "n/a" : property.getGetter().getGenericReturnType()));
        System.out.println("before: getGenericArguments(CSharpQueue) = "
            + java.util.Arrays.toString(TypeHelper.getGenericArguments(CSharpQueue.class)));
        Class<?> declared = property.getPropertyType();
        System.out.println("declared = " + declared);
        System.out.println("after:  getGenericArguments(CSharpQueue) = "
            + java.util.Arrays.toString(TypeHelper.getGenericArguments(CSharpQueue.class)));
    }
}
