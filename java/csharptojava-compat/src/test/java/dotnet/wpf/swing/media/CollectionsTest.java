package dotnet.wpf.swing.media;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class CollectionsTest {

    @Test
    void doubleCollection_addAndGet() {
        DoubleCollection dc = new DoubleCollection();
        dc.add(1.0);
        dc.add(2.0);
        assertEquals(2, dc.getCount());
        assertEquals(2.0, dc.get(1));
        dc.set(0, 9.0);
        assertEquals(9.0, dc.get(0));
    }

    @Test
    void doubleCollection_iterableCtor() {
        DoubleCollection dc = new DoubleCollection(java.util.Arrays.asList(1.0, 2.0, 3.0));
        assertEquals(3, dc.size());
    }

    @Test
    void doubleCollection_remove() {
        DoubleCollection dc = new DoubleCollection(java.util.Arrays.asList(1.0, 2.0, 3.0));
        dc.remove(1.0);
        assertEquals(2, dc.size());
    }

    @Test
    void pointCollection_add() {
        PointCollection pc = new PointCollection();
        pc.add(new dotnet.wpf.swing.Point(1, 2));
        assertEquals(1, pc.getCount());
        assertEquals(new dotnet.wpf.swing.Point(1, 2), pc.get(0));
    }

    @Test
    void pathSegmentCollection_andFigureCollection() {
        PathSegmentCollection sc = new PathSegmentCollection();
        sc.add(new LineSegment(new dotnet.wpf.swing.Point(1, 1), true));
        PathFigureCollection fc = new PathFigureCollection();
        PathFigure f = new PathFigure();
        f.setSegments(sc);
        fc.add(f);
        assertEquals(1, fc.size());
        assertEquals(1, fc.get(0).getSegments().size());
    }
}
