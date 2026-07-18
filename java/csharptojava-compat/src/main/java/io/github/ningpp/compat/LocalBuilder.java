package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.Emit.LocalBuilder.
 */
public final class LocalBuilder {

    private final Class<?> _localType;

    public LocalBuilder(Class<?> localType) {
        this._localType = localType;
    }

    public Class<?> getLocalType() {
        return _localType;
    }
}
