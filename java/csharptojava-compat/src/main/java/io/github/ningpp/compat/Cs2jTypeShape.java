package io.github.ningpp.compat;

/** Read-only queries over the C# type-shape markers attached by preprocessing. */
public final class Cs2jTypeShape {

    private Cs2jTypeShape() {
    }

    public static boolean isMarked(Class<?> type) {
        return has(type, Cs2jTypeShapeMarker.class);
    }

    public static boolean isStruct(Class<?> type) {
        return has(type, Cs2jStructMarker.class);
    }

    public static boolean isRecord(Class<?> type) {
        return has(type, Cs2jRecordMarker.class);
    }

    public static boolean isReadOnly(Class<?> type) {
        return has(type, Cs2jReadOnlyMarker.class);
    }

    public static boolean isByRefLike(Class<?> type) {
        return has(type, Cs2jByRefLikeMarker.class);
    }

    private static boolean has(Class<?> type, Class<?> marker) {
        return type != null && marker.isAssignableFrom(type);
    }
}
