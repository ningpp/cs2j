package io.github.ningpp.compat;

import java.lang.reflect.Constructor;
import java.lang.reflect.Modifier;
import java.lang.reflect.Parameter;

public final class ConstructorInfo implements MemberInfo {
    private final Constructor<?> constructor;

    public ConstructorInfo(Constructor<?> constructor) {
        this.constructor = constructor;
        this.constructor.setAccessible(true);
    }

    public Constructor<?> getConstructor() {
        return constructor;
    }

    @Override
    public String getName() {
        return constructor.getName();
    }

    @Override
    public Class<?> getDeclaringType() {
        return constructor.getDeclaringClass();
    }

    public boolean getIsStatic() {
        return false;
    }

    public boolean getIsPublic() {
        return Modifier.isPublic(constructor.getModifiers());
    }

    @Override
    public Parameter[] getParameters() {
        return constructor.getParameters();
    }

    public Object invoke(Object... args) {
        try {
            return constructor.newInstance(args);
        } catch (ReflectiveOperationException e) {
            throw new RuntimeException(e);
        }
    }

    @Override
    public Object[] getCustomAttributes(boolean inherit) {
        return constructor.getAnnotations();
    }

    @Override
    public Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
        return constructor.getAnnotationsByType((Class) attributeType);
    }
}
