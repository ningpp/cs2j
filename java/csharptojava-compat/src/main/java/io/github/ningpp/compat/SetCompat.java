package io.github.ningpp.compat;

import java.util.Set;

/**
 * Static bridges for C# {@code ISet<T>} relational operations. Java's
 * {@link Set} has no isSubsetOf/isSupersetOf surface, so converted instance
 * calls ({@code expected.IsSubsetOf(actual)}) map here with the receiver as
 * the first argument.
 */
public final class SetCompat {

    private SetCompat() { /* utility class */ }

    /** Mirrors ISet&lt;T&gt;.IsSubsetOf(IEnumerable&lt;T&gt; other). */
    public static boolean isSubsetOf(Set<?> set, Iterable<?> other) {
        requireSet(set);
        for (Object item : set) {
            if (!contains(other, item)) {
                return false;
            }
        }
        return true;
    }

    /** Mirrors ISet&lt;T&gt;.IsSupersetOf(IEnumerable&lt;T&gt; other). */
    public static boolean isSupersetOf(Set<?> set, Iterable<?> other) {
        requireSet(set);
        for (Object item : other) {
            if (!set.contains(item)) {
                return false;
            }
        }
        return true;
    }

    /** Mirrors ISet&lt;T&gt;.SetEquals(IEnumerable&lt;T&gt; other). */
    public static boolean setEquals(Set<?> set, Iterable<?> other) {
        requireSet(set);
        int count = 0;
        for (Object item : other) {
            if (!set.contains(item)) {
                return false;
            }
            count++;
        }
        return count == set.size();
    }

    /** Mirrors ISet&lt;T&gt;.Overlaps(IEnumerable&lt;T&gt; other). */
    public static boolean overlaps(Set<?> set, Iterable<?> other) {
        requireSet(set);
        for (Object item : other) {
            if (set.contains(item)) {
                return true;
            }
        }
        return false;
    }

    private static void requireSet(Set<?> set) {
        if (set == null) {
            throw new NullPointerException("set");
        }
    }

    private static boolean contains(Iterable<?> items, Object target) {
        for (Object item : items) {
            if (item == null ? target == null : item.equals(target)) {
                return true;
            }
        }
        return false;
    }
}
