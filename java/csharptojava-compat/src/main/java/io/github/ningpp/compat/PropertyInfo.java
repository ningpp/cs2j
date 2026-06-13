package io.github.ningpp.compat;

import java.lang.reflect.Method;
import java.lang.reflect.InvocationTargetException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Bridges C# System.Reflection.PropertyInfo to Java reflection.
 * C# properties have getter/setter methods; Java uses separate getter/setter methods.
 * This class wraps a Java getter Method to provide C# PropertyInfo semantics.
 */
public final class PropertyInfo {

    private final Method getter;
    private final String name;

    // Methods that are C# methods (not properties) but follow Java getter naming convention.
    // These should be excluded when bridging C# Type.GetProperties() semantics.
    private static final Set<String> EXCLUDED_METHODS = Set.of(
        "getClass",
        "hashCode",
        "isWellFormedOriginalString",
        "getUserDrivenParsing",
        "getHasAuthority"
    );

    public PropertyInfo(Method getter) {
        this.getter = getter;
        this.getter.setAccessible(true);
        // Derive property name from getter: "getFoo" → "Foo", "isBar" → "Bar"
        String methodName = getter.getName();
        if (methodName.startsWith("get") && methodName.length() > 3) {
            this.name = methodName.substring(3);
        } else if (methodName.startsWith("is") && methodName.length() > 2) {
            this.name = methodName.substring(2);
        } else {
            this.name = methodName;
        }
    }

    public String getName() {
        return name;
    }

    public Object getValue(Object obj, Object[] index) {
        try {
            Object value = getter.invoke(obj, index != null ? index : new Object[0]);
            return wrapArray(value);
        } catch (IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    public Object getValue(Object obj) {
        try {
            Object value = getter.invoke(obj);
            return wrapArray(value);
        } catch (IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    private static Object wrapArray(Object value) {
        if (value != null && value.getClass().isArray()) {
            return new ArrayPropertyWrapper(value);
        }
        return value;
    }

    public Method getGetter() {
        return getter;
    }

    /**
     * Returns all public properties (getter methods) of the given class.
     * A property is defined as a public method starting with "get" (non-void return)
     * or "is" (boolean return), with zero parameters.
     * Bridges C# Type.GetProperties() semantics.
     * Methods in the EXCLUDED_METHODS set are excluded since they correspond
     * to C# methods (not properties).
     */
    public static PropertyInfo[] getProperties(Class<?> clazz) {
        List<PropertyInfo> props = new ArrayList<>();
        for (Method m : clazz.getMethods()) {
            // Skip void returns and methods with parameters
            if (m.getParameterCount() != 0) continue;
            if (m.getReturnType() == void.class) continue;
            // Skip methods that are C# methods (not properties)
            if (EXCLUDED_METHODS.contains(m.getName())) continue;
            String methodName = m.getName();
            if (methodName.startsWith("get") && methodName.length() > 3) {
                props.add(new PropertyInfo(m));
            } else if (methodName.startsWith("is") && methodName.length() > 2
                       && (m.getReturnType() == boolean.class || m.getReturnType() == Boolean.class)) {
                props.add(new PropertyInfo(m));
            }
        }
        return props.toArray(new PropertyInfo[0]);
    }

    /**
     * Wraps an array so that toString() returns a C#-compatible type name string
     * (e.g., "System.String[]") instead of content-based or identity hash code strings.
     * This matches C# array ToString() behavior where arrays return their type name.
     */
    static final class ArrayPropertyWrapper {
        private final Object array;

        ArrayPropertyWrapper(Object array) {
            this.array = array;
        }

        @Override
        public String toString() {
            // C# arrays return their type name from ToString(), e.g., "System.String[]"
            Class<?> componentType = array.getClass().getComponentType();
            if (componentType != null) {
                StringBuilder sb = new StringBuilder();
                sb.append("System.");
                if (componentType == String.class) {
                    sb.append("String");
                } else if (componentType == int.class) {
                    sb.append("Int32");
                } else if (componentType == byte.class) {
                    sb.append("Byte");
                } else if (componentType == char.class) {
                    sb.append("Char");
                } else if (componentType == long.class) {
                    sb.append("Int64");
                } else if (componentType == double.class) {
                    sb.append("Double");
                } else if (componentType == float.class) {
                    sb.append("Single");
                } else if (componentType == short.class) {
                    sb.append("Int16");
                } else if (componentType == boolean.class) {
                    sb.append("Boolean");
                } else {
                    sb.append("Object");
                }
                // Count array dimensions
                Class<?> type = array.getClass();
                while (type.isArray()) {
                    sb.append("[]");
                    type = type.getComponentType();
                }
                return sb.toString();
            }
            return array.toString();
        }

        @Override
        public boolean equals(Object obj) {
            if (this == obj) return true;
            if (obj instanceof ArrayPropertyWrapper) {
                return Arrays.deepEquals(new Object[]{array}, new Object[]{((ArrayPropertyWrapper) obj).array});
            }
            return false;
        }

        @Override
        public int hashCode() {
            return Arrays.deepHashCode(new Object[]{array});
        }
    }
}
