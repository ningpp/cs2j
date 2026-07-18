package io.github.ningpp.compat;

import java.lang.reflect.Parameter;

public interface MemberInfo extends ICustomAttributeProvider {
    String getName();

    Class<?> getDeclaringType();

    default Parameter[] getParameters() {
        return new Parameter[0];
    }

    @Override
    default Object[] getCustomAttributes(boolean inherit) {
        return new Object[0];
    }
}
