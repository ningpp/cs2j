package io.github.ningpp.compat;

/**
 * System.Threading.ThreadStart compatibility delegate.
 */
@FunctionalInterface
public interface ThreadStart {
    void run();
}
