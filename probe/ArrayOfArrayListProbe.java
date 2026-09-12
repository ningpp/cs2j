import dotnet.text.json.JsonSerializer;
import io.github.ningpp.compat.CSharpArrayList;
import io.github.ningpp.compat.CSharpIList;
import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.TypeHelper;

/** The array-of-ArrayList element converter: which type is being checked? */
public class ArrayOfArrayListProbe {

    public static void main(String[] args) {
        System.out.println("isAssignableFrom(CSharpList, CSharpArrayList)        = "
            + TypeHelper.isAssignableFrom(CSharpList.class, CSharpArrayList.class));
        System.out.println("isAssignableFrom(CSharpArrayList, CSharpList)        = "
            + TypeHelper.isAssignableFrom(CSharpArrayList.class, CSharpList.class));
        System.out.println("isAssignableFrom(CSharpArrayList, CSharpArrayList)   = "
            + TypeHelper.isAssignableFrom(CSharpArrayList.class, CSharpArrayList.class));
        System.out.println("isAssignableFrom(CSharpIList, CSharpArrayList)       = "
            + TypeHelper.isAssignableFrom(CSharpIList.class, CSharpArrayList.class));
        try {
            CSharpArrayList[] result = (CSharpArrayList[]) JsonSerializer.deserialize(
                "[[1,2],[3,4]]", null, CSharpArrayList[].class);
            System.out.println("deserialize -> len " + (result == null ? "null" : result.length));
        } catch (Throwable t) {
            System.out.println("deserialize FAILED: " + t.getClass().getSimpleName() + ": "
                + String.valueOf(t.getMessage()).split("\n")[0]);
            StackTraceElement[] st = t.getStackTrace();
            for (int i = 0; i < Math.min(6, st.length); i++) {
                System.out.println("    at " + st[i]);
            }
        }
    }
}
