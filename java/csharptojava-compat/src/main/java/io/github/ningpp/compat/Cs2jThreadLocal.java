package io.github.ningpp.compat;

import java.util.List;
import java.util.function.Supplier;

/**
 * Compat counterpart of {@code System.Threading.ThreadLocal<T>}.
 *
 * <p>The C09 preprocessor rewrites {@code [ThreadStatic]} fields into holders
 * of this type; read/write sites are lowered to the bean-style accessors
 * {@link #getValue()} / {@link #setValue(Object)}, which are the converter's
 * default property-lowering targets and delegate to the inherited
 * {@link #get()} / {@link #set(Object)} per-thread slot semantics.</p>
 *
 * <p>Decisions for the members that have no direct Java equivalent:</p>
 * <ul>
 *   <li>{@code Dispose()} maps to {@link #dispose()} and releases the
 *       <em>current</em> thread's slot ({@link #remove()}); cross-thread slot
 *       disposal that .NET performs is unavailable on the JVM.</li>
 *   <li>{@code IsValueCreated} is tracked per thread via {@link #isValueCreated()}.</li>
 *   <li>{@code trackAllValues=true} and the {@code Values} property cannot be
 *       represented in Java and fail fast with
 *       {@link UnsupportedOperationException}.</li>
 * </ul>
 */
public class Cs2jThreadLocal<T> extends ThreadLocal<T> {

    private final Supplier<? extends T> valueFactory;
    private final ThreadLocal<Boolean> valueCreated = ThreadLocal.withInitial(() -> false);

    public Cs2jThreadLocal() {
        this.valueFactory = null;
    }

    public Cs2jThreadLocal(Supplier<? extends T> valueFactory) {
        this.valueFactory = valueFactory;
    }

    public Cs2jThreadLocal(Supplier<? extends T> valueFactory, boolean trackAllValues) {
        if (trackAllValues) {
            throw new UnsupportedOperationException(
                    "System.Threading.ThreadLocal(trackAllValues: true) has no Java equivalent.");
        }
        this.valueFactory = valueFactory;
    }

    @Override
    protected T initialValue() {
        // Matches C#: the factory runs once per thread on first access; if it
        // throws, java.lang.ThreadLocal does not cache and the next get() retries.
        return valueFactory == null ? null : valueFactory.get();
    }

    @Override
    public T get() {
        T value = super.get();
        valueCreated.set(true);
        return value;
    }

    @Override
    public void set(T value) {
        super.set(value);
        valueCreated.set(true);
    }

    @Override
    public void remove() {
        super.remove();
        valueCreated.set(false);
    }

    /** C# {@code ThreadLocal<T>.Value} getter (property lowering target). */
    public T getValue() {
        return get();
    }

    /** C# {@code ThreadLocal<T>.Value} setter (property lowering target). */
    public void setValue(T value) {
        set(value);
    }

    /** C# {@code ThreadLocal<T>.IsValueCreated}. */
    public boolean isValueCreated() {
        return valueCreated.get();
    }

    /** C# {@code ThreadLocal<T>.Values}: only available with trackAllValues=true, which the Java half rejects. */
    public List<T> getValues() {
        throw new UnsupportedOperationException(
                "System.Threading.ThreadLocal.Values (trackAllValues) has no Java equivalent.");
    }

    /**
     * C# {@code ThreadLocal<T>.Dispose()}: drops the current thread's slot.
     * Cross-thread disposal performed by .NET has no JVM equivalent.
     */
    public void dispose() {
        remove();
    }
}
