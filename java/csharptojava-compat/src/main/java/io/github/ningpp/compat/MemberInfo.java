package io.github.ningpp.compat;

import java.util.List;

public interface MemberInfo {
    String getName();

    Class<?> getDeclaringType();

    default List<Object> getCustomAttributes(boolean inherit) {
        return List.of();
    }
}
