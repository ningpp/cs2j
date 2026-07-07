package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import java.util.List;
import org.junit.jupiter.api.Test;

class CSharpGenericIterableCovarianceTest {
    // Mirrors the C# covariance scenario: Cluster : Node, and
    // IEnumerable<Cluster> must be assignable to IEnumerable<Node>.
    static class Node {
        final String name;

        Node(String name) {
            this.name = name;
        }
    }

    static class Cluster extends Node {
        Cluster(String name) {
            super(name);
        }
    }

    @Test
    void fromAcceptsSubtypeCollectionForSupertypeTarget() {
        // This line only compiles if CSharpGenericIterable.from is covariant
        // (Iterable<? extends T>), emulating C#'s IEnumerable<out T>.
        CSharpGenericIterable<Node> nodes = CSharpGenericIterable.from(List.of(new Cluster("a"), new Node("b")));
        assertEquals(2, nodes.size());
    }

    @Test
    void enumeratorFromAcceptsSubtypeIteratorForSupertypeTarget() {
        // This line only compiles if CSharpGenericEnumerator.from is covariant
        // (Iterator<? extends T>).
        CSharpGenericEnumerator<Node> en = CSharpGenericEnumerator.from(List.<Cluster>of(new Cluster("a")).iterator());
        assertEquals("a", en.next().name);
    }
}
