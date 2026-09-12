import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.StringIListWrapper;
import io.github.ningpp.compat.CSharpGenericIList;
import io.github.ningpp.compat.TypeHelper;

/** Reproduces the generic element-type erasure: every list element arrives as a JsonElement. */
public class GenericElementProbe {

    public static void main(String[] args) {
        System.out.println("== getGenericArguments(StringIListWrapper) = "
            + java.util.Arrays.toString(TypeHelper.getGenericArguments(StringIListWrapper.class)));
        System.out.println("== getGenericArguments(CSharpGenericIList) = "
            + java.util.Arrays.toString(TypeHelper.getGenericArguments(CSharpGenericIList.class)));

        dotnet.text.json.JsonSerializerOptions options = new dotnet.text.json.JsonSerializerOptions();
        try {
            Object converter = options.getConverter(StringIListWrapper.class);
            System.out.println("converter = " + converter.getClass().getName());
            java.lang.reflect.Method getElementType = converter.getClass().getMethod("getElementType");
            System.out.println("converter.getElementType() = " + getElementType.invoke(converter));
            java.lang.reflect.Method getTypeToConvert = converter.getClass().getMethod("getTypeToConvert");
            System.out.println("converter.getTypeToConvert() = " + getTypeToConvert.invoke(converter));
        } catch (Throwable t) {
            System.out.println("options.getConverter failed: " + t);
        }

        StringIListWrapper result = (StringIListWrapper) JsonSerializer.deserialize("[\"1\",\"2\"]", null, StringIListWrapper.class);
        int count = 0;
        for (String s : result) {
            System.out.println("element = " + s + " (" + (s == null ? "null" : s.getClass().getName()) + ")");
            count++;
        }
        System.out.println("count = " + count);
    }
}
