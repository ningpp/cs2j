import io.github.ningpp.compat.CSharpArrayList;
import io.github.ningpp.compat.CSharpGenericIList;
import io.github.ningpp.compat.CSharpIList;
import io.github.ningpp.compat.TypeHelper;

/** Which branch does the factory take for a non-generic ArrayList after the patch? */
public class FactoryListBranchProbe {

    public static void main(String[] args) throws Exception {
        dotnet.text.json.Serialization.Converters.IEnumerableConverterFactory factory =
            new dotnet.text.json.Serialization.Converters.IEnumerableConverterFactory();
        System.out.println("canConvert                  = " + factory.canConvert(CSharpArrayList.class));
        System.out.println("help.isListTree             = "
            + dotnet.text.json.Serialization.IEnumerableConverterFactoryHelpers.getCompatibleGenericInterface(
                CSharpArrayList.class, CSharpGenericIList.class));
        System.out.println("isAssignableFrom(GenericIList, ArrayList) = "
            + TypeHelper.isAssignableFrom(CSharpGenericIList.class, CSharpArrayList.class));
        System.out.println("isAssignableFrom(IList, ArrayList)        = "
            + TypeHelper.isAssignableFrom(CSharpIList.class, CSharpArrayList.class));
        Object converter = factory.createConverter(CSharpArrayList.class,
            new dotnet.text.json.JsonSerializerOptions());
        System.out.println("createConverter -> " + converter.getClass().getName());
    }
}
