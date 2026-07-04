package io.github.ningpp.compat;

import java.util.Comparator;

public interface CSharpComparer extends Comparator<Object> {
    int compare(Object a, Object b);
}
