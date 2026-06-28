package io.github.ningpp.compat;

import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.Parameter;

public final class MethodInfo implements MemberInfo {
    private final Method method;

    public MethodInfo(Method method) {
        this.method = method;
        this.method.setAccessible(true);
    }

    public Method getMethod() {
        return method;
    }

    @Override
    public String getName() {
        return method.getName();
    }

    @Override
    public Class<?> getDeclaringType() {
        return method.getDeclaringClass();
    }

    public boolean getIsStatic() {
        return Modifier.isStatic(method.getModifiers());
    }

    public boolean getIsPublic() {
        return Modifier.isPublic(method.getModifiers());
    }

    public boolean getIsSpecialName() {
        String name = method.getName();
        return name.startsWith("op_") || name.startsWith("get") || name.startsWith("set");
    }

    public ReturnParameter getReturnParameter() {
        return new ReturnParameter(method.getReturnType());
    }

    public Parameter[] getParameters() {
        return method.getParameters();
    }

    public Object invoke(Object target, Object... args) {
        try {
            return method.invoke(target, args);
        } catch (ReflectiveOperationException ex) {
            throw new TargetInvocationException("Method invocation failed", ex);
        }
    }

    public Object invoke(Object target, java.util.List<?> args) {
        return invoke(target, args == null ? new Object[0] : args.toArray());
    }

    public static final class ReturnParameter {
        private final Class<?> type;

        private ReturnParameter(Class<?> type) {
            this.type = type;
        }

        public Class<?> getType() {
            return type;
        }
    }
}
