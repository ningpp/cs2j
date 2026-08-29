import io.github.ningpp.compat.JsonSerializerCompat;
import io.github.ningpp.compat.ImmutableList;

public class ImmTest2 {
    public static void main(String[] args) throws Exception {
        try {
            Object v = JsonSerializerCompat.deserialize(new com.fasterxml.jackson.core.type.TypeReference<ImmutableList<Integer>>() {}, "[1,2]");
            System.out.println("OK: " + v + " type=" + v.getClass().getName() + " size=" + ((ImmutableList)v).size());
            System.out.println("first class: " + ((ImmutableList)v).get(0).getClass().getName());
        } catch (Throwable t) {
            t.printStackTrace(System.out);
        }
    }
}
