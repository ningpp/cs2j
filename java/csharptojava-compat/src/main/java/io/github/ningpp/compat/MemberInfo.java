package io.github.ningpp.compat;

import java.lang.reflect.Parameter;
import java.util.ArrayList;
import java.util.List;

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

    /**
     * C# {@code MemberInfo.GetCustomAttributes(Type, bool)} shape. The trimmed
     * BCL surfaces it through the CustomAttributeExtensions extension, whose
     * conversion keeps the receiver as the first argument; concrete member kinds
     * (ConstructorInfo, MethodInfo, …) override {@code getCustomAttributes} with
     * real lookups, so the default filters the base view by runtime type.
     */
    default Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
        List<Object> result = new ArrayList<>();
        for (Object attribute : getCustomAttributes(inherit)) {
            if (attributeType.isInstance(attribute)) {
                result.add(attribute);
            }
        }
        return result.toArray();
    }
}
