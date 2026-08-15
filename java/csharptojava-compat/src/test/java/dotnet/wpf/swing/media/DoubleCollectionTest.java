package dotnet.wpf.swing.media;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

class DoubleCollectionTest {

    @Test
    void add_acceptsIntLiteral_ImplicitWidening() {
        DoubleCollection dc = new DoubleCollection();
        dc.add(1);
        dc.add(2.5);
        assertEquals(2, dc.size());
        assertEquals(1.0, dc.get(0));
        assertEquals(2.5, dc.get(1));
    }

    @Test
    void addAtIndex_andSet_acceptIntLiteral() {
        DoubleCollection dc = new DoubleCollection();
        dc.add(0, 1);
        dc.set(0, 3);
        assertEquals(3.0, dc.get(0));
    }
}
