import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.PropertyVisibilityTests;

/** privateSetter: a public setter with a private getter — why does the getter read null afterwards? */
public class PrivateSetterProbe {

    public static void main(String[] args) throws Exception {
        Object obj = JsonSerializer.deserialize("{\"MyString\":\"Hello\"}", null,
            PropertyVisibilityTests.ClassWithPrivateSetterAndGetter.class);
        java.lang.reflect.Method getter = obj.getClass().getDeclaredMethod("getMyString");
        getter.setAccessible(true);
        System.out.println("private getter -> " + getter.invoke(obj));
        java.lang.reflect.Field field = obj.getClass().getDeclaredField("myString");
        field.setAccessible(true);
        System.out.println("field          -> " + field.get(obj));
    }
}
