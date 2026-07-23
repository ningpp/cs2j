package io.github.ningpp.compat;

import java.util.concurrent.ConcurrentHashMap;

public class ConcurrentHashMapHelper {
    private ConcurrentHashMapHelper() {
    }

    @SuppressWarnings({"unchecked", "rawtypes"})
    public static boolean tryAdd(ConcurrentHashMap map, Object key, Object value) {
        return map.putIfAbsent(key, value) == null;
    }
}
