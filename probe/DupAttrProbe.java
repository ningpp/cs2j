import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.PropertyVisibilityTests;

/** Which exception does the duplicate-[JsonInclude] check actually raise? */
public class DupAttrProbe {

    public static void main(String[] args) throws Exception {
        Class<?> type = null;
        for (Object[] row : PropertyVisibilityTests.nonPublicProperty_WithJsonInclude_Invalid_data1()) {
            type = (Class<?>) row[0];
            break;
        }
        System.out.println("type = " + type);
        try {
            JsonSerializer.deserialize_0tp("", type, null);
            System.out.println("deserialize: NO EXCEPTION");
        } catch (Throwable t) {
            System.out.println("deserialize threw " + t.getClass().getName() + ": " + t.getMessage());
            if (t.getCause() != null) {
                System.out.println("  cause = " + t.getCause().getClass().getName() + ": " + t.getCause().getMessage());
            }
        }
        try {
            JsonSerializer.serialize_0tp(io.github.ningpp.compat.Activator.createInstance(type), type, null);
            System.out.println("serialize: NO EXCEPTION");
        } catch (Throwable t) {
            System.out.println("serialize threw " + t.getClass().getName() + ": " + t.getMessage());
        }
    }
}
