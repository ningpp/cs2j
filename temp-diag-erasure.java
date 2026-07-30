import io.github.ningpp.compat.CSharpGenericIterable;

public class Rectangle {
}

public class Point {
}

public class Box {
    public int Count;

    public Box(CSharpGenericIterable<Point> points) {
        Count = 100;
        for (Point p : points) { Count++; }
    }

    public static Box createFrom_CSharpGenericIterable_Rectangle(CSharpGenericIterable<Rectangle> rectangles) {
        Box __inst = new Box();
        __inst.Count = 200;
        for (Rectangle r : rectangles) { __inst.Count++; }
        return __inst;
    }
    public static Box fromRectangles(CSharpGenericIterable<Rectangle> rectangles) {
        return Box.createFrom_CSharpGenericIterable_Rectangle(rectangles);
    }
}

