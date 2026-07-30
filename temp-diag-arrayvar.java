import io.github.ningpp.compat.CSharpGenericIterable;
import java.util.ArrayList;
import io.github.ningpp.compat.CSharpList;
import io.github.ningpp.compat.ArgumentNullException;

public class Point {
    public int X;
    public int Y;
}

public class Sample {
    void test(CSharpGenericIterable<Point> source, Point[] arr) {
        {
        var pts = test_ProceduralLinq1(source, source);
        for (Point p : pts) {

        }
        }
        {
        Point[] pts = arr;
        for (Point p : pts) {

        }
        }
    }
    CSharpGenericIterable<Point> test_ProceduralLinq1(CSharpGenericIterable<Point> _linqitems, CSharpGenericIterable<Point> source) {
        CSharpList<Point> _yieldResult = new CSharpList<Point>();
        if (_linqitems == null) {
        throw new ArgumentNullException();
        }
        for (Point _linqitem : _linqitems) {
        if (_linqitem.X > 0) {
        _yieldResult.add(_linqitem);
        }
        }
        return _yieldResult;
    }
}

