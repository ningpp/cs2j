package io.github.ningpp.compat;

import java.lang.reflect.Method;
import java.lang.reflect.InvocationTargetException;
import java.util.ArrayList;
import java.util.List;

/**
 * Bridges C# System.Reflection.PropertyInfo to Java reflection.
 * C# properties have getter/setter methods; Java uses separate getter/setter methods.
 * This class wraps a Java getter Method to provide C# PropertyInfo semantics.
 */
public final class PropertyInfo {

    private final Method getter;
    private final String name;

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
            return getter.invoke(obj, index != null ? index : new Object[0]);
        } catch (IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    public Object getValue(Object obj) {
        try {
            return getter.invoke(obj);
        } catch (IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    public Method getGetter() {
        return getter;
    }

    /**
     * Returns all public properties (getter methods) of the given class.
     * A property is defined as a public method starting with "get" (non-void return)
     * or "is" (boolean return), with zero parameters.
     * Bridges C# Type.GetProperties() semantics.
     */
    public static PropertyInfo[] getProperties(Class<?> clazz) {
        List<PropertyInfo> props = new ArrayList<>();
        for (Method m : clazz.getMethods()) {
            // Skip void returns and methods with parameters
            if (m.getParameterCount() != 0) continue;
            if (m.getReturnType() == void.class) continue;
            // Skip getClass() and hashCode() and toString() etc.
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
}
