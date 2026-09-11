package io.github.ningpp.compat;

import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.Parameter;

public final class MethodInfo implements MemberInfo {
    private final Method method;
    /** Runtime type arguments recorded by {@link #makeGenericMethod(Class[])}. */
    private final Class<?>[] genericTypeArguments;

    public MethodInfo(Method method) {
        this(method, null);
    }

    private MethodInfo(Method method, Class<?>[] genericTypeArguments) {
        this.method = method;
        this.method.setAccessible(true);
        this.genericTypeArguments = genericTypeArguments;
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
        Object[] effectiveArgs = withRuntimeClassArguments(args);
        try {
            return method.invoke(target, adaptArgs(effectiveArgs));
        } catch (IllegalArgumentException ex) {
            Class<?>[] pts = method.getParameterTypes();
            StringBuilder sb = new StringBuilder();
            sb.append(method).append(" target=").append(target == null ? "null" : target.getClass().getName());
            int argCount = effectiveArgs == null ? 0 : effectiveArgs.length;
            for (int i = 0; i < argCount; i++) {
                sb.append(" arg").append(i).append('[').append(i < pts.length ? pts[i].getName() : "?").append("]=")
                  .append(effectiveArgs[i] == null ? "null" : effectiveArgs[i].getClass().getName());
            }
            throw new IllegalArgumentException(sb.toString(), ex);
        } catch (ReflectiveOperationException ex) {
            throw new TargetInvocationException("Method invocation failed", ex);
        }
    }

    /**
     * Supplies the runtime {@code Class} tokens that the converter appends to generic
     * methods whose C# signature has none (the port carries {@code T} at runtime as an
     * extra {@code Class} parameter named {@code t<T>Class} / {@code _cs2j_<T>}).
     *
     * <p>C# call sites reach such a method through
     * {@code GetMethod(name, ...).MakeGenericMethod(t1, t2).Invoke(target, args)}, so the
     * recorded type arguments are matched to the appended parameters by name; parameters
     * the caller already supplied are left untouched, and when no parameter name matches
     * a type parameter the arguments are passed through unchanged (so a mismatch fails
     * loudly instead of silently binding the wrong type).</p>
     */
    private Object[] withRuntimeClassArguments(Object[] args) {
        if (genericTypeArguments == null || genericTypeArguments.length == 0) {
            return args;
        }
        Parameter[] parameters = method.getParameters();
        int supplied = args == null ? 0 : args.length;
        if (supplied >= parameters.length) {
            return args;
        }
        Object[] effective = java.util.Arrays.copyOf(args == null ? new Object[0] : args, parameters.length);
        boolean filled = false;
        for (int i = supplied; i < parameters.length; i++) {
            if (parameters[i].getType() != Class.class) {
                continue;
            }
            int typeArgumentIndex = typeArgumentIndexOf(parameters[i].getName());
            if (typeArgumentIndex >= 0 && typeArgumentIndex < genericTypeArguments.length) {
                effective[i] = genericTypeArguments[typeArgumentIndex];
                filled = true;
            }
        }
        return filled ? effective : args;
    }

    /** Maps an appended runtime-class parameter name to its type-parameter position. */
    private int typeArgumentIndexOf(String parameterName) {
        if (parameterName == null) {
            return -1;
        }
        String typeParameterName = null;
        if (parameterName.startsWith("t") && parameterName.endsWith("Class") && parameterName.length() > 7) {
            typeParameterName = parameterName.substring(1, parameterName.length() - 5);
        } else if (parameterName.startsWith("_cs2j_")) {
            typeParameterName = parameterName.substring("_cs2j_".length());
        }
        if (typeParameterName == null || typeParameterName.isEmpty()) {
            return -1;
        }
        java.lang.reflect.TypeVariable<Method>[] typeParameters = method.getTypeParameters();
        for (int i = 0; i < typeParameters.length; i++) {
            String declared = typeParameters[i].getName();
            if (declared.equals(typeParameterName)
                || declared.equals("T" + typeParameterName)
                || declared.equalsIgnoreCase(typeParameterName)) {
                return i;
            }
        }
        return -1;
    }

    /**
     * Mirrors .NET reflection binder behavior: .NET converts boxed values to
     * the parameter's primitive type (e.g. an Int32 box passed to a short
     * parameter succeeds). Java reflection requires the wrapper type to match
     * the primitive parameter exactly, so convert each argument to the declared
     * parameter type's wrapper before invoking.
     */
    private Object[] adaptArgs(Object[] args) {
        if (args == null || args.length == 0) {
            return args;
        }
        Class<?>[] paramTypes = method.getParameterTypes();
        Object[] adapted = new Object[args.length];
        for (int i = 0; i < args.length; i++) {
            adapted[i] = args[i];
            if (i >= paramTypes.length || args[i] == null) {
                continue;
            }
            Class<?> param = paramTypes[i];
            Class<?> actual = args[i].getClass();
            if (param.isInstance(args[i])) {
                continue;
            }
            // .NET allows passing a Dictionary<K,V> (or List<T> etc.) to a
            // non-generic IDictionary/IList/ICollection/ISet parameter via
            // implicit interface conversion. Java reflection requires the exact
            // parameter type, so bridge generic implementations to the
            // non-generic interfaces here.
            if (param == CSharpIDictionary.class && args[i] instanceof CSharpGenericIDictionary<?, ?>) {
                adapted[i] = CSharpIDictionary.from((CSharpGenericIDictionary<?, ?>) args[i]);
                continue;
            }
            if (param == CSharpIList.class && args[i] instanceof CSharpGenericIList<?>) {
                adapted[i] = CSharpIList.from((CSharpGenericIList<?>) args[i]);
                continue;
            }
            if (param == CSharpICollection.class
                && (args[i] instanceof CSharpGenericIList<?> || args[i] instanceof CSharpGenericIDictionary<?, ?>)) {
                adapted[i] = CSharpICollection.from(args[i]);
                continue;
            }
            Class<?> wrapper = primitiveWrapper(param);
            if (wrapper != null) {
                if (actual != wrapper && actual != param) {
                    adapted[i] = convertNumber(args[i], wrapper);
                }
            } else if (args[i] instanceof Number
                       && param != Number.class
                       && !param.isInstance(args[i])) {
                // e.g. Integer value for an int[] element / Object param handled
                // elsewhere; here just widen or narrow numeric arguments.
                if (Number.class.isAssignableFrom(param)) {
                    adapted[i] = convertNumber(args[i], param);
                }
            }
        }
        return adapted;
    }

    private static Class<?> primitiveWrapper(Class<?> type) {
        if (!type.isPrimitive()) {
            return null;
        }
        if (type == byte.class) {
            return Byte.class;
        }
        if (type == short.class) {
            return Short.class;
        }
        if (type == int.class) {
            return Integer.class;
        }
        if (type == long.class) {
            return Long.class;
        }
        if (type == float.class) {
            return Float.class;
        }
        if (type == double.class) {
            return Double.class;
        }
        if (type == char.class) {
            return Character.class;
        }
        if (type == boolean.class) {
            return Boolean.class;
        }
        return null;
    }

    private static Object convertNumber(Object value, Class<?> target) {
        if (!(value instanceof Number n)) {
            return value;
        }
        if (target == Integer.class) {
            return n.intValue();
        }
        if (target == Short.class) {
            return (short) n.intValue();
        }
        if (target == Byte.class) {
            return (byte) n.intValue();
        }
        if (target == Long.class) {
            return n.longValue();
        }
        if (target == Float.class) {
            return n.floatValue();
        }
        if (target == Double.class) {
            return n.doubleValue();
        }
        return value;
    }

    public Object invoke(Object target, java.util.List<?> args) {
        return invoke(target, args == null ? new Object[0] : args.toArray());
    }

    /**
     * Bridges C# MethodInfo.MakeGenericMethod(Type[]).
     * <p>
     * Java reflection has no direct equivalent because generic type information
     * is erased at runtime. The type arguments are recorded on the returned
     * MethodInfo so that a following {@link #invoke(Object, Object...)} can supply
     * the runtime {@code Class} tokens the converter appends to such methods.
     */
    public MethodInfo makeGenericMethod(Class<?>... typeArguments) {
        if (typeArguments == null || typeArguments.length == 0) {
            return this;
        }
        return new MethodInfo(method, typeArguments.clone());
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
