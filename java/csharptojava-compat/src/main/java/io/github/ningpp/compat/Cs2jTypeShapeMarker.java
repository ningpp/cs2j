package io.github.ningpp.compat;

/**
 * Parent of every C# type-shape marker. C# type modifiers that Java cannot
 * express ({@code struct}, {@code record}, {@code readonly}, {@code ref struct})
 * are carried by empty marker interfaces instead of annotations, naming
 * conventions or runtime guessing.
 *
 * <p>The interface has no members: the converted Java class only implements it,
 * and the compat layer queries it with {@code isAssignableFrom}.</p>
 *
 * @see Cs2jTypeShape
 */
public interface Cs2jTypeShapeMarker {
}
