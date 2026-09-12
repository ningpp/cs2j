package io.github.ningpp.compat;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * API the converter needs at a call site that had {@code typeof(List<List<int>>)} in C#:
 * a Java class literal erases every layer, so the emission must publish the closed generic
 * chain before the serializer performs any lookup.
 *
 * <pre>
 *   JsonSerializer.serialize(
 *       (Class&lt;CSharpList&lt;CSharpList&lt;Integer&gt;&gt;&gt;) (Class&lt;?&gt;)
 *           TypeHelper.closedGenericType(TypeHelper.parameterized(
 *               CSharpList.class, TypeHelper.parameterized(CSharpList.class, Integer.class))),
 *       value, null);
 * </pre>
 */
class ClosedGenericEmissionApiTest {

    @AfterEach
    void forgetRegistrations() {
        TypeHelper.clearThreadRegistrations();
    }

    @Test
    void closedGenericType_publishesEveryLayerOuterFirst() {
        Class<?> raw = TypeHelper.closedGenericType(TypeHelper.parameterized(
            CSharpList.class, TypeHelper.parameterized(CSharpList.class, Integer.class)));

        assertSame(CSharpList.class, raw);
        assertArrayEquals(new Class<?>[] { CSharpList.class }, TypeHelper.getGenericArguments(CSharpList.class));
        assertArrayEquals(new Class<?>[] { Integer.class }, TypeHelper.getGenericArguments(CSharpList.class));
    }

    @Test
    void closedGenericType_worksForASingleLayer() {
        Class<?> raw = TypeHelper.closedGenericType(
            TypeHelper.parameterized(CSharpList.class, Integer.class));

        assertSame(CSharpList.class, raw);
        assertArrayEquals(new Class<?>[] { Integer.class }, TypeHelper.getGenericArguments(CSharpList.class));
    }

    @Test
    void closedGenericType_ofANonGenericTypeIsItself() {
        assertSame(String.class, TypeHelper.closedGenericType(String.class));
        assertArrayEquals(new Class<?>[0], TypeHelper.getGenericArguments(String.class));
    }
}
