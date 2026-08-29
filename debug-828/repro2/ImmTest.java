import io.github.ningpp.compat.JsonSerializerCompat;
import io.github.ningpp.compat.IImmutableList;

public class ImmTest {
    public static void main(String[] args) throws Exception {
        try {
            Object v = JsonSerializerCompat.deserialize(new com.fasterxml.jackson.core.type.TypeReference<IImmutableList<Integer>>() {}, "[1,2]");
            System.out.println("OK: " + v + " type=" + v.getClass().getName() + " size=" + ((IImmutableList)v).size());
        } catch (Throwable t) {
            System.out.println("THROW: " + t);
            t.printStackTrace(System.out);
        }
    }
}
