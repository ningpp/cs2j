package io.github.ningpp.compat;

public interface CSharpGenericIterable<T> extends Iterable<T> {
    @Override
    CSharpGenericEnumerator<T> iterator();

    static <T> CSharpGenericIterable<T> from(Iterable<T> iterable) {
        return new CSharpGenericIterable<>() {
            @Override
            public CSharpGenericEnumerator<T> iterator() {
                return CSharpGenericEnumerator.from(iterable.iterator());
            }
        };
    }
}
