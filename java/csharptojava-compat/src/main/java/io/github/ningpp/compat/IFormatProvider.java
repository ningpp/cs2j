package io.github.ningpp.compat;

/** Replacement for System.IFormatProvider. */
public interface IFormatProvider {
    Object getFormat(Class<?> formatType);
}
