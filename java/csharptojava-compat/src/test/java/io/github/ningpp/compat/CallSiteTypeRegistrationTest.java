package io.github.ningpp.compat;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Call-site registrations are the lowest priority: a member that published its own type
 * arguments through {@code PropertyInfo}/{@code FieldInfo} must keep them, because
 * registering unconditionally at every call site used to override the member layers and
 * cost more tests than it fixed (12 regressions for 6 wins on the SDK).
 */
class CallSiteTypeRegistrationTest {

    @AfterEach
    void forgetRegistrations() {
        TypeHelper.clearThreadRegistrations();
    }

    @Test
    void callSiteArguments_areUsedWhenNothingElseIsInEffect() {
        TypeHelper.registerCallSiteType(
            TypeHelper.parameterized(CSharpList.class, Integer.class));

        assertArrayEquals(new Class<?>[] { Integer.class },
            TypeHelper.getGenericArguments(CSharpList.class));
    }

    @Test
    void memberLayerWinsOverTheCallSiteLayer() {
        TypeHelper.registerMemberType(
            TypeHelper.parameterized(CSharpList.class, String.class));
        TypeHelper.registerCallSiteType(
            TypeHelper.parameterized(CSharpList.class, Integer.class));

        assertArrayEquals(new Class<?>[] { String.class },
            TypeHelper.getGenericArguments(CSharpList.class),
            "a member's published arguments must not be overridden by a call-site registration");
    }

    @Test
    void callSiteArguments_coverEveryNestedLayer() {
        TypeHelper.registerCallSiteType(TypeHelper.parameterized(
            CSharpList.class, TypeHelper.parameterized(CSharpList.class, Integer.class)));

        assertArrayEquals(new Class<?>[] { CSharpList.class }, TypeHelper.getGenericArguments(CSharpList.class));
        assertArrayEquals(new Class<?>[] { Integer.class }, TypeHelper.getGenericArguments(CSharpList.class));
    }

    @Test
    void closedCallSiteGenericType_returnsTheErasedRawClass() {
        Class<?> raw = TypeHelper.closedCallSiteGenericType(
            TypeHelper.parameterized(CSharpList.class, Integer.class));

        assertSame(CSharpList.class, raw);
        assertArrayEquals(new Class<?>[] { Integer.class }, TypeHelper.getGenericArguments(CSharpList.class));
    }
}
