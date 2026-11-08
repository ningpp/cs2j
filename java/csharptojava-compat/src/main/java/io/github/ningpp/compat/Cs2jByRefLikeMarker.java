package io.github.ningpp.compat;

/**
 * Runtime marker for C# types that were declared as {@code ref struct} and
 * lowered to an ordinary struct by the C#-to-C# preprocessing chain.
 *
 * <p>Java has no stack-only type, so the byref-like-ness that reflection observes
 * through {@code System.Type.IsByRefLike} is carried by this interface instead.
 * The converted C# declaration implements the marker; the converter maps the
 * support-library interface to this compat type via TypeMappings.json.</p>
 *
 * @see TypeHelper#getIsByRefLike(Class)
 */
public interface Cs2jByRefLikeMarker {
}
