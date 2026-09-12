import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Converters.IEnumerableConverterFactory;
import dotnet.text.json.Serialization.Converters.ObjectConverterFactory;
import io.github.ningpp.compat.CSharpObjQueue;
import io.github.ningpp.compat.CSharpObjStack;
import io.github.ningpp.compat.CSharpGenericIterable;
import io.github.ningpp.compat.TypeHelper;

/** Which factory claims the non-generic queue, and in what order are factories asked? */
public class ObjQueueProbe2 {

    public static void main(String[] args) throws Exception {
        System.out.println("isAssignableFrom(CSharpGenericIterable, CSharpObjQueue) = "
            + TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpObjQueue.class));
        System.out.println("isAssignableFrom(CSharpIterable, CSharpObjQueue) = "
            + TypeHelper.isAssignableFrom(io.github.ningpp.compat.CSharpIterable.class, CSharpObjQueue.class));
        System.out.println("isAssignableFrom(Iterable, CSharpObjQueue) = "
            + TypeHelper.isAssignableFrom(Iterable.class, CSharpObjQueue.class));

        IEnumerableConverterFactory enumFactory = new IEnumerableConverterFactory();
        ObjectConverterFactory objFactory = new ObjectConverterFactory();
        System.out.println("enumFactory.canConvert(CSharpObjQueue) = " + enumFactory.canConvert(CSharpObjQueue.class));
        System.out.println("objFactory.canConvert(CSharpObjQueue) = " + objFactory.canConvert(CSharpObjQueue.class));

        JsonSerializerOptions options = new JsonSerializerOptions();
        java.lang.reflect.Field factories = JsonSerializerOptions.class.getDeclaredField("s_defaultFactoryConverters");
        factories.setAccessible(true);
        for (Object c : (Object[]) factories.get(null)) {
            System.out.println("   " + c.getClass().getSimpleName()
                + " canConvert=" + ((dotnet.text.json.Serialization.JsonConverter) c).canConvert(CSharpObjQueue.class));
        }
        try {
            Object created = enumFactory.createConverter(CSharpObjQueue.class, options);
            System.out.println("enumFactory.createConverter = " + created.getClass().getSimpleName());
        } catch (Throwable t) {
            System.out.println("enumFactory.createConverter FAILED: " + t);
        }
    }
}
