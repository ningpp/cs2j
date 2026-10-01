package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BiConsumer;
import java.util.function.Function;

import org.junit.jupiter.api.Test;

class DelegateHelperTest {
    @Test
    void combineInvokesBothDelegatesInOrder() {
        StringBuilder calls = new StringBuilder();
        BiConsumer<Object, String> first = (sender, value) -> calls.append("first:").append(value).append(';');
        BiConsumer<Object, String> second = (sender, value) -> calls.append("second:").append(value).append(';');

        BiConsumer<Object, String> combined = DelegateHelper.combine(first, second);

        combined.accept(this, "value");

        assertEquals("first:value;second:value;", calls.toString());
    }

    @Test
    void removeDropsOnlyMatchingDelegateFromCombinedInvocationList() {
        AtomicInteger firstCalls = new AtomicInteger();
        AtomicInteger secondCalls = new AtomicInteger();
        BiConsumer<Object, String> first = (sender, value) -> firstCalls.incrementAndGet();
        BiConsumer<Object, String> second = (sender, value) -> secondCalls.incrementAndGet();
        BiConsumer<Object, String> combined = DelegateHelper.combine(first, second);

        BiConsumer<Object, String> remaining = DelegateHelper.remove(combined, first);

        remaining.accept(this, "value");

        assertEquals(0, firstCalls.get());
        assertEquals(1, secondCalls.get());
    }

    @Test
    void combinedDelegateReturnsLastDelegateResult() {
        Function<String, String> first = value -> "first:" + value;
        Function<String, String> second = value -> "second:" + value;

        Function<String, String> combined = DelegateHelper.combine(first, second);

        assertEquals("second:value", combined.apply("value"));
    }

    @Test
    void removeDropsLastMatchingDelegateOnly() {
        AtomicInteger calls = new AtomicInteger();
        Runnable handler = calls::incrementAndGet;
        Runnable combined = DelegateHelper.combine(DelegateHelper.combine(handler, handler), handler);

        Runnable remaining = DelegateHelper.remove(combined, handler);

        remaining.run();

        assertEquals(2, calls.get());
    }

    // ---- delegateEquals: C# invocation-list equality (J4) ----

    @Test
    void delegateEquals_sameMulticastList_returnsTrue() {
        Runnable a = () -> { };
        Runnable left = DelegateHelper.combine(a, a);
        Runnable right = DelegateHelper.combine(a, a);

        // Distinct proxy instances, but identical invocation lists.
        assertFalse(left == right);
        assertTrue(DelegateHelper.delegateEquals(left, right));
    }

    @Test
    void delegateEquals_differentInvocationOrder_returnsFalse() {
        Runnable first = () -> { };
        Runnable second = () -> { };

        Runnable left = DelegateHelper.combine(first, second);
        Runnable right = DelegateHelper.combine(second, first);

        assertFalse(DelegateHelper.delegateEquals(left, right));
    }

    @Test
    void delegateEquals_nullMatrix() {
        Runnable a = () -> { };

        assertTrue(DelegateHelper.delegateEquals(null, null));
        assertFalse(DelegateHelper.delegateEquals(null, a));
        assertFalse(DelegateHelper.delegateEquals(a, null));
        assertTrue(DelegateHelper.delegateEquals(a, a));
    }

    @Test
    void delegateEquals_multicastVsSingleItem_returnsFalse() {
        Runnable a = () -> { };

        assertFalse(DelegateHelper.delegateEquals(DelegateHelper.combine(a, a), a));
        assertFalse(DelegateHelper.delegateEquals(a, DelegateHelper.combine(a, a)));
    }

    @Test
    void multicastProxy_equals_followsInvocationList() {
        Runnable a = () -> { };
        Runnable firstProxy = DelegateHelper.combine(a, a);
        Runnable equalProxy = DelegateHelper.combine(a, a);

        // The proxy's Object.equals override is routed through delegateEquals.
        assertTrue(firstProxy.equals(equalProxy));
        assertFalse(firstProxy.equals(null));
        assertTrue(firstProxy.equals(firstProxy));

        // Equal proxies must share a hashCode (Object contract).
        assertEquals(firstProxy.hashCode(), equalProxy.hashCode());
    }
}
