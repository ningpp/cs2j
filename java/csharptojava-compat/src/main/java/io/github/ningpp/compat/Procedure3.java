package io.github.ningpp.compat;

/**
 * Compat for System.Action&lt;T1,T2,T3&gt; — three-argument procedure.
 */
@FunctionalInterface
public interface Procedure3<T1, T2, T3> {
    void value(T1 t1, T2 t2, T3 t3);
}
