package io.github.ningpp.compat;

import java.lang.reflect.Parameter;
import java.util.List;

public interface MemberInfo {
    String getName();

    Class<?> getDeclaringType();

    default Parameter[] getParameters() {
        return new Parameter[0];
    }

    default List<Object> getCustomAttributes(boolean inherit) {
        return List.of();
    }
}
