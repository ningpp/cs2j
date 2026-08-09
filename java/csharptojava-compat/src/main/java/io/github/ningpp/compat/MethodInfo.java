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

    /**
     * Alias for {@link #getDeclaringType()} used by generated code that maps
     * C# System.Reflection.MethodInfo.DeclaringType to Java reflection naming.
     */
    public Class<?> getDeclaringClass() {
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

    /** Mirrors C# MethodInfo.IsVirtual — true for non-final instance methods. */
    public boolean getIsVirtual() {
        return !Modifier.isStatic(method.getModifiers()) && !Modifier.isFinal(method.getModifiers());
    }

    /** Mirrors C# MethodInfo.IsAbstract. */
    public boolean getIsAbstract() {
        return Modifier.isAbstract(method.getModifiers());
    }

    /** Mirrors C# MethodInfo.IsGenericMethod — true when the method declares type parameters. */
    public boolean getIsGenericMethod() {
        return method.getTypeParameters().length > 0;
    }

    /** Mirrors C# MethodInfo.GetGenericArguments(). */
    public Class<?>[] getGenericArguments() {
        java.lang.reflect.TypeVariable<?>[] vars = method.getTypeParameters();
        Class<?>[] result = new Class<?>[vars.length];
        for (int i = 0; i < vars.length; i++) {
            result[i] = Object.class;
        }
        return result;
    }

    public ReturnParameter getReturnParameter() {
        return new ReturnParameter(method.getReturnType());
    }

    /**
     * Mirrors C# MethodInfo.ReturnType.
     */
    public Class<?> getReturnType() {
        return method.getReturnType();
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

    /**
     * Bridges C# MethodInfo.MakeGenericMethod(Type[]).
     * <p>
     * Java reflection has no direct equivalent because generic type information
     * is erased at runtime. The type arguments are accepted for API compatibility
     * and the same MethodInfo is returned so that generated code can chain calls.
     */
    public MethodInfo makeGenericMethod(Class<?>... typeArguments) {
        return this;
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
