import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Tests.StringIListWrapper;
import io.github.ningpp.compat.CSharpGenericIList;
import io.github.ningpp.compat.TypeHelper;

/**
 * Which type does the converter factory actually see, and what is cached for it?
 *
 * <pre>
 *   java FactoryProbe
 * </pre>
 */
public class FactoryProbe {

    public static void main(String[] args) throws Exception {
        JsonSerializerOptions options = new JsonSerializerOptions();
        for (Class<?> type : new Class<?>[] {
                StringIListWrapper.class,
                CSharpGenericIList.class,
                io.github.ningpp.compat.CSharpList.class }) {
            Object converter = options.getConverter(type);
            System.out.println(type.getSimpleName() + " -> " + converter.getClass().getSimpleName()
                + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter));
        }
        // Does the factory receive the concrete class at all?
        dotnet.text.json.Serialization.Converters.IEnumerableConverterFactory factory =
            new dotnet.text.json.Serialization.Converters.IEnumerableConverterFactory();
        System.out.println("canConvert(StringIListWrapper) = " + factory.canConvert(StringIListWrapper.class));
        Object created = factory.createConverter(StringIListWrapper.class, options);
        System.out.println("createConverter(StringIListWrapper) = " + created.getClass().getSimpleName()
            + " elementType=" + created.getClass().getMethod("getElementType").invoke(created));
    }
}
