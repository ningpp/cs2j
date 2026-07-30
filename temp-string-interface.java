public interface INameScope {
    public Object get(String name, String ns);
    public Object set(String name, String ns, Object value);
}

public class NameTable implements INameScope {
    public Object get(String name, String ns) {
        return null;
    }
    public Object set(String name, String ns, Object value) {
        return value;
    }
}

