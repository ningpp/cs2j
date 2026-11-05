package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.stream.StreamSupport;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

/**
 * C# Enumerable.OfType&lt;T&gt;() must filter by runtime type: the compat
 * ListenerCollection mixes DefaultTraceListener with derived listeners, and the
 * generated typed ToArray materializes a T[] from the filtered sequence.
 */
class TraceListenerCollectionOfTypeTest {

    private static final class Redirector extends DefaultTraceListener {
    }

    @Test
    void ofTypeClassFilterReturnsOnlyMatchingInstances() {
        Trace.ListenerCollection listeners = new Trace.ListenerCollection();
        listeners.add(new DefaultTraceListener());
        Redirector redirector = new Redirector();
        listeners.add(redirector);

        Redirector[] array = StreamSupport.stream(
                listeners.ofType(Redirector.class).spliterator(), false)
            .toArray(Redirector[]::new);

        assertEquals(1, array.length);
        assertSame(redirector, array[0]);
    }

    @Test
    void ofTypeClassFilterReturnsEmptyWhenNoInstanceMatches() {
        Trace.ListenerCollection listeners = new Trace.ListenerCollection();
        listeners.add(new DefaultTraceListener());

        int count = 0;
        for (Redirector ignored : listeners.ofType(Redirector.class)) {
            count++;
        }

        assertEquals(0, count);
    }
}
