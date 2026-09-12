import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Converters.IEnumerableConverterFactory;
import io.github.ningpp.compat.CSharpStack;
import io.github.ningpp.compat.TypeHelper;

/**
 * Which factory branch builds the converter for CSharpStack, and does it see the element type?
 *
 * <pre>
 *   java FactoryBranchProbe plain
 *   java FactoryBranchProbe registered
 * </pre>
 */
public class FactoryBranchProbe {

    public static void main(String[] args) {
        String mode = args.length > 0 ? args[0] : "plain";
        JsonSerializerOptions options = new JsonSerializerOptions();
        if (mode.equals("registered")) {
            TypeHelper.beginTypeRegistration();
            TypeHelper.registerParameterizedType(TypeHelper.parameterized(
                CSharpStack.class, TypeHelper.parameterized(CSharpStack.class, Integer.class)));
        }
        try {
            IEnumerableConverterFactory factory = new IEnumerableConverterFactory();
            System.out.println("canConvert = " + factory.canConvert(CSharpStack.class));
            Object converter = factory.createConverter(CSharpStack.class, options);
            System.out.println("createConverter = " + converter.getClass().getSimpleName()
                + " elementType=" + converter.getClass().getMethod("getElementType").invoke(converter));
        } catch (Throwable t) {
            System.out.println("factory FAILED: " + t);
        }
        System.out.println("getGenericArguments(CSharpStack) = "
            + java.util.Arrays.toString(TypeHelper.getGenericArguments(CSharpStack.class)));
        if (mode.equals("registered")) {
            TypeHelper.endTypeRegistration();
        }
    }
}
