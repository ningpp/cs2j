package io.github.ningpp.compat;

import java.util.Objects;
import java.util.function.Supplier;

public final class Lazy<T> {
    private Supplier<? extends T> supplier;
    private T value;
    private boolean valueCreated;

    public Lazy(Supplier<? extends T> supplier) {
        this.supplier = Objects.requireNonNull(supplier, "supplier");
    }

    public Lazy(Supplier<? extends T> supplier, boolean isThreadSafe) {
        this(supplier);
    }

    public Lazy(T value) {
        this.value = value;
        this.valueCreated = true;
    }

    public T getValue() {
        if (!valueCreated) {
            value = supplier.get();
            supplier = null;
            valueCreated = true;
        }
        return value;
    }

    public boolean isValueCreated() {
        return valueCreated;
    }
}
