package io.github.ningpp.compat;

/**
 * System.Net.Cache.RequestCachePolicy compatibility class.
 */
public class RequestCachePolicy {

    private final int level;

    public RequestCachePolicy() {
        this(0);
    }

    public RequestCachePolicy(int level) {
        this.level = level;
    }

    public int getLevel() {
        return level;
    }
}
