import dotnet.text.json.JsonSerializer;
import dotnet.text.json.JsonSerializerOptions;
import dotnet.text.json.Serialization.Tests.TestClassWithNull;

/** "null" at the root must deserialize to null — which link in the chain breaks it? */
public class RootNullProbe {

    public static void main(String[] args) throws Exception {
        Object standard = JsonSerializer.deserialize("null", null, TestClassWithNull.class);
        System.out.println("deserialize(String, options, Class) -> " + standard);

        JsonSerializerOptions options = new JsonSerializerOptions();
        Object converter = options.getConverter(TestClassWithNull.class);
        System.out.println("converter = " + converter.getClass().getSimpleName());
        for (String m : new String[] { "getHandleNullOnRead", "getCanBeNull", "getClassType", "getTypeToConvert" }) {
            try {
                System.out.println("   " + m + " = " + converter.getClass().getMethod(m).invoke(converter));
            } catch (NoSuchMethodException e) {
                System.out.println("   " + m + " = <no such method>");
            }
        }
    }
}
