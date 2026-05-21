package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;

public final class Trace {
    public static final ListenerCollection Listeners = new ListenerCollection();

    static {
        Listeners.add(new DefaultTraceListener());
    }

    private Trace() {}

    public static ListenerCollection Listeners() {
        return Listeners;
    }

    public static final class ListenerCollection extends ArrayList<Object> {
        public <T> Iterable<T> ofType() {
            List<T> result = new ArrayList<>();
            for (Object item : this) {
                @SuppressWarnings("unchecked")
                T cast = (T)item;
                result.add(cast);
            }
            return result;
        }
    }
}
