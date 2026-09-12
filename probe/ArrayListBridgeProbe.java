import io.github.ningpp.compat.CSharpArrayList;
import io.github.ningpp.compat.CSharpGenericIList;
import io.github.ningpp.compat.CSharpGenericIterable;
import io.github.ningpp.compat.CSharpIList;
import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.TypeHelper;

/** Which assignability checks does the collection factory make for a non-generic ArrayList? */
public class ArrayListBridgeProbe {

    public static void main(String[] args) {
        System.out.println("isAssignableFrom(CSharpGenericIterable, CSharpArrayList) = "
            + TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpArrayList.class));
        System.out.println("isAssignableFrom(CSharpIList, CSharpArrayList)           = "
            + TypeHelper.isAssignableFrom(CSharpIList.class, CSharpArrayList.class));
        System.out.println("isAssignableFrom(CSharpGenericIList, CSharpArrayList)     = "
            + TypeHelper.isAssignableFrom(CSharpGenericIList.class, CSharpArrayList.class));
        System.out.println("isAssignableFrom(CSharpIList, CSharpList)                 = "
            + TypeHelper.isAssignableFrom(CSharpIList.class, CSharpList.class));
        System.out.println("isAssignableFrom(CSharpGenericIList, CSharpList)          = "
            + TypeHelper.isAssignableFrom(CSharpGenericIList.class, CSharpList.class));
        try {
            dotnet.text.json.Serialization.Converters.IEnumerableConverterFactory factory =
                new dotnet.text.json.Serialization.Converters.IEnumerableConverterFactory();
            System.out.println("factory.canConvert(CSharpArrayList) = " + factory.canConvert(CSharpArrayList.class));
        } catch (Throwable t) {
            System.out.println("factory failed: " + t);
        }
    }
}
