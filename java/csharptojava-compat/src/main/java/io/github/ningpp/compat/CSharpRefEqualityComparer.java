package io.github.ningpp.compat;

public class CSharpRefEqualityComparer implements CSharpGenericEqualityComparer<Object> {

    private static final CSharpRefEqualityComparer INSTANCE = new CSharpRefEqualityComparer();

    public static CSharpRefEqualityComparer getInstance() {
        return INSTANCE;
    }

    private CSharpRefEqualityComparer() {
    }

    @Override
    public boolean equals(Object x, Object y) {
        return x == y;
    }

    @Override
    public int hashCode(Object obj) {
        return System.identityHashCode(obj);
    }
}
