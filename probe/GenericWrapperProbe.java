import com.fasterxml.jackson.core.type.TypeReference;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Tests.GenericICollectionWrapper;
import dotnet.text.json.Serialization.Tests.GenericListWrapper;
import io.github.ningpp.compat.CSharpICollection;
import io.github.ningpp.compat.JsonSerializerCompat;
import io.github.ningpp.compat.TypeHelper;

/** Why does a generic wrapper still erase its element type? */
public class GenericWrapperProbe {

    public static void main(String[] args) throws Exception {
        Class<?> wrapper = GenericICollectionWrapper.class;
        System.out.println("type params = " + java.util.Arrays.toString(wrapper.getTypeParameters()));
        System.out.println("generic interfaces = " + java.util.Arrays.toString(wrapper.getGenericInterfaces()));
        System.out.println("getGenericArguments(CSharpICollection) = "
            + java.util.Arrays.toString(TypeHelper.getGenericArguments(CSharpICollection.class)));
        System.out.println("getGenericArguments(GenericICollectionWrapper) = "
            + java.util.Arrays.toString(TypeHelper.getGenericArguments(wrapper)));

        Object result = JsonSerializerCompat.deserialize(
            new TypeReference<GenericICollectionWrapper<String>>() {}, "[\"1\",\"2\"]");
        System.out.println("result = " + result);

        Object listResult = JsonSerializerCompat.deserialize(
            new TypeReference<GenericListWrapper<String[]>>() {}, "[[\"1\",\"2\"]]");
        System.out.println("listResult = " + listResult);
    }
}
