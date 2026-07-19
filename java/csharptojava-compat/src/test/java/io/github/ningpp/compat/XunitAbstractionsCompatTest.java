package io.github.ningpp.compat;

import csharp.xunit.Abstractions.IReflectionMethodInfo;
import org.junit.jupiter.api.Test;

import java.lang.reflect.Method;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;

class XunitAbstractionsCompatTest {

    @Test
    void reflectionMethodInfoExposesCompatMethodInfoWithDeclaringType() throws NoSuchMethodException {
        Method javaMethod = String.class.getMethod("length");
        MethodInfo methodInfo = new MethodInfo(javaMethod);

        assertNotNull(methodInfo.getDeclaringType());
        assertEquals(String.class, methodInfo.getDeclaringType());

        // Verify the interface contract used by generated code: IReflectionMethodInfo.getMethodInfo()
        // must return the compat MethodInfo so callers can invoke getDeclaringType().
        IReflectionMethodInfo reflection = new IReflectionMethodInfo() {
            @Override
            public MethodInfo getMethodInfo() {
                return methodInfo;
            }

            @Override
            public csharp.xunit.Abstractions.ITypeInfo getType() {
                return null;
            }
        };

        assertEquals(String.class, reflection.getMethodInfo().getDeclaringType());
    }

    @Test
    void reflectionMethodInfoExposesCompatMethodInfoWithDeclaringClass() throws NoSuchMethodException {
        Method javaMethod = String.class.getMethod("length");
        MethodInfo methodInfo = new MethodInfo(javaMethod);

        assertNotNull(methodInfo.getDeclaringClass());
        assertEquals(String.class, methodInfo.getDeclaringClass());

        // Verify the interface contract used by generated code: IReflectionMethodInfo.getMethodInfo()
        // must return the compat MethodInfo so callers can invoke getDeclaringClass().
        IReflectionMethodInfo reflection = new IReflectionMethodInfo() {
            @Override
            public MethodInfo getMethodInfo() {
                return methodInfo;
            }

            @Override
            public csharp.xunit.Abstractions.ITypeInfo getType() {
                return null;
            }
        };

        assertEquals(String.class, reflection.getMethodInfo().getDeclaringClass());
    }
}
