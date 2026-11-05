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
        /**
         * Type-erased variant: it only casts every element, so mixed collections
         * (DefaultTraceListener + a derived redirector) later fail with
         * ArrayStoreException when stored into the derived-type array. Kept for
         * existing generated code.
         */
        @Deprecated
        public <T> Iterable<T> ofType() {
            List<T> result = new ArrayList<>();
            for (Object item : this) {
                @SuppressWarnings("unchecked")
                T cast = (T)item;
                result.add(cast);
            }
            return result;
        }

        /** C# <c>Enumerable.OfType&lt;T&gt;()</c>: keeps only instances of {@code type}. */
        public <T> Iterable<T> ofType(Class<T> type) {
            List<T> result = new ArrayList<>();
            for (Object item : this) {
                if (type.isInstance(item)) {
                    result.add(type.cast(item));
                }
            }
            return result;
        }
    }
}
