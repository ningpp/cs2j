public interface INameScope {
    public Object get(Object name, Object ns);
    public Object set(Object name, Object ns, Object value);
}

public class NameTable implements INameScope {
    public Object get(String name, String ns) {
        return null;
    }
    public Object set(String name, String ns, Object value) {
        return value;
    }
    public Object get(Object name, Object ns) {
        return null;
    }
    public Object set(Object name, Object ns, Object value) {
        return value;
    }
}

public class StructMapping implements INameScope {
    public Object get(Object name, Object ns) {
        return null;
    }
    public Object set(Object name, Object ns, Object value) {
        return value;
    }
}

