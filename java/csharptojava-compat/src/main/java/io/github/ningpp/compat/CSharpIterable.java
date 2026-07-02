package io.github.ningpp.compat;

public interface CSharpIterable extends Iterable<Object> {
    @Override
    CSharpEnumerator iterator();
}
