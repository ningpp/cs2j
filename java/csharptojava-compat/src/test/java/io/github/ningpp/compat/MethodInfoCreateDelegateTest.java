package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/** Bridges C# MethodInfo.CreateDelegate(Type): 静态方法绑定到函数式接口（Proxy 动态绑定）。 */
class MethodInfoCreateDelegateTest {

    @FunctionalInterface
    private interface Generator {
        String get();
    }

    @FunctionalInterface
    private interface TwoArg {
        int add(int a, int b);
    }

    public static String factory() {
        return "made";
    }

    public int sum(int a, int b) {
        return a + b;
    }

    @Test
    void staticMethod_BindsToFunctionalInterface() throws Exception {
        MethodInfo method = TypeHelper.getMethod(MethodInfoCreateDelegateTest.class, "factory");
        Generator generator = method.createDelegate(Generator.class);
        assertEquals("made", generator.get());
    }

    @Test
    void instanceMethod_BindsWithTarget() throws Exception {
        MethodInfo method = TypeHelper.getMethod(MethodInfoCreateDelegateTest.class, "sum");
        TwoArg add = method.createDelegate(TwoArg.class, this);
        assertEquals(5, add.add(2, 3));
    }
}
