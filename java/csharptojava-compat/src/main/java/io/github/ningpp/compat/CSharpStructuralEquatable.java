package io.github.ningpp.compat;

public interface CSharpStructuralEquatable {
    boolean equals(Object other, CSharpEqualityComparer comparer);
    int hashCode(CSharpEqualityComparer comparer);
}
