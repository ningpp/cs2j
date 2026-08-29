import dotnet.text.json.JsonSerializer;
import io.github.ningpp.compat.CSharpArrayList;
import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.ArrayHelper;

public class ArrayListWrite {
    public static void main(String[] args) throws Exception {
        CSharpArrayList al = new CSharpArrayList();
        al.add(1); al.add(2);
        try {
            String json = JsonSerializer.serialize_0tp(al, (Class) CSharpArrayList.class);
            System.out.println("ArrayList serialize: " + json);
        } catch (Throwable t) {
            System.out.println("THROW: " + t);
        }
        CSharpList<Integer> list = new CSharpList<>(ArrayHelper.toList(new Integer[] { 1, 2 }));
        try {
            String json = JsonSerializer.serialize_0tp(list, (Class) CSharpList.class);
            System.out.println("CSharpList serialize: " + json);
        } catch (Throwable t) {
            System.out.println("THROW2: " + t);
        }
    }
}
