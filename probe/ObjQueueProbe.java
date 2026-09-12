import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.CSharpObjQueue;
import io.github.ningpp.compat.CSharpObjStack;
import io.github.ningpp.compat.TypeHelper;

/** Why is a non-generic queue serialized as an object? */
public class ObjQueueProbe {

    public static void main(String[] args) throws Exception {
        System.out.println("CSharpObjQueue -> CSharpGenericIterable assignable = "
            + TypeHelper.isAssignableFrom(io.github.ningpp.compat.CSharpGenericIterable.class, CSharpObjQueue.class));
        System.out.println("isNonGenericStackOrQueue = "
            + dotnet.text.json.Serialization.IEnumerableConverterFactoryHelpers.isNonGenericStackOrQueue(CSharpObjQueue.class));

        JsonSerializerOptions options = new JsonSerializerOptions();
        describe("CSharpObjQueue", options.getConverter(CSharpObjQueue.class));
        describe("CSharpObjStack", options.getConverter(CSharpObjStack.class));

        CSharpObjQueue queue = new CSharpObjQueue(new CSharpList<Integer>(java.util.List.of(1, 2)));
        System.out.println("serialized = " + JsonSerializer.serialize(CSharpObjQueue.class, queue, null));
    }

    static void describe(String label, Object converter) throws Exception {
        System.out.println(label + " -> " + converter.getClass().getName()
            + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter)
            + " classType=" + converter.getClass().getMethod("getClassType").invoke(converter));
    }
}
