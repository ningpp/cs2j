package io.github.ningpp.compat;

/**
 * System.Net.Cache.RequestCacheLevel compatibility enum values (int constants).
 */
public final class RequestCacheLevel {

    public static final int Default = 0;
    public static final int BypassCache = 1;
    public static final int CacheOnly = 2;
    public static final int CacheIfAvailable = 3;
    public static final int Revalidate = 4;
    public static final int Reload = 5;
    public static final int NoCacheNoStore = 6;

    private RequestCacheLevel() {
    }
}
