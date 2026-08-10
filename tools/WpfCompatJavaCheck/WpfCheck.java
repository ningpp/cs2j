import dotnet.wpf.swing.*;
import dotnet.wpf.swing.media.*;
import dotnet.wpf.swing.media.imaging.*;

public class WpfCheck {
    public static void main(String[] args) {
        // ---- Point ----
        Point p0 = new Point();
        p("Point()", p0);
        Point p1 = new Point(3.5, -2.0);
        p("Point(3.5,-2.0)", p1);
        System.out.println("Point.X=" + fmt(p1.getX()) + " Point.Y=" + fmt(p1.getY()));
        Point p2 = new Point(1, 2);
        p2.offset(3, 4);
        p("Point.Offset(3,4)", p2);
        r("Point.Equals(1,2)==(1,2)", new Point(1, 2).equals(new Point(1, 2)));
        r("Point.Equals(1,2)!=(2,1)", new Point(1, 2).equals(new Point(2, 1)));
        r("Point.Hash(1,2)", new Point(1, 2).hashCode() == new Point(1, 2).hashCode());
        r("Point.Add((1,2)+V(1,2))", Point.add(new Point(1, 2), new Vector(1, 2)));
        r("Point.Subtract((1,2)-V(3,4))", Point.subtract(new Point(1, 2), new Vector(3, 4)));
        System.out.println("Point.SubtractToVector => " + Point.subtractToVector(new Point(5, 5), new Point(1, 2)));
        System.out.println("Point.ToString=" + new Point(1.5, 2.25).toString());

        // ---- Size ----
        Size s0 = new Size();
        p("Size()", s0);
        Size s1 = new Size(100, 50);
        p("Size(100,50)", s1);
        r("Size.Empty.IsEmpty", Size.EMPTY.isEmpty());
        r("Size(10,10).IsEmpty", new Size(10, 10).isEmpty());
        r("Size.Equals", new Size(10, 20).equals(new Size(10, 20)));
        r("Size.NotEquals", !new Size(10, 20).equals(new Size(20, 10)));
        System.out.println("Size.ToString=" + new Size(10, 20).toString());
        System.out.println("Size.Empty.ToString=" + Size.EMPTY.toString());

        // ---- Rect ----
        Rect r0 = new Rect(10, 20, 100, 50);
        p("Rect(10,20,100,50)", r0);
        try { p("Rect(10,20,-100,-50)", new Rect(10, 20, -100, -50)); }
        catch (Exception ex) { System.out.println("Rect(10,20,-100,-50) THROWS: " + ex.getClass().getSimpleName()); }
        try { p("Rect(10,20,100,-50)", new Rect(10, 20, 100, -50)); }
        catch (Exception ex) { System.out.println("Rect(10,20,100,-50) THROWS: " + ex.getClass().getSimpleName()); }
        Rect rEmpty = Rect.EMPTY;
        p("Rect.Empty", rEmpty);
        r("Rect.Empty.IsEmpty", rEmpty.isEmpty());
        Rect r2p = new Rect(new Point(10, 20), new Point(0, 0));
        p("Rect(pt(10,20),pt(0,0))", r2p);
        r("Rect(pt,pt).X", fmt(r2p.getX()));
        Rect rSize = new Rect(new Size(50, 60));
        p("Rect(Size(50,60))", rSize);
        Rect rLocSize = new Rect(new Point(5, 6), new Size(50, 60));
        p("Rect(pt(5,6),Size(50,60))", rLocSize);
        System.out.println("Rect.corners=" + fmt(r0.getLeft()) + "," + fmt(r0.getTop()) + ","
                + fmt(r0.getRight()) + "," + fmt(r0.getBottom()));
        System.out.println("Rect.TopLeft=" + r0.getTopLeft() + " BottomRight=" + r0.getBottomRight());
        r("Rect.Contains(pt(5,5))", new Rect(0, 0, 10, 10).contains(new Point(5, 5)));
        r("Rect.Contains(edge 10,10)", new Rect(0, 0, 10, 10).contains(new Point(10, 10)));
        r("Rect.Contains(11,5)", new Rect(0, 0, 10, 10).contains(new Point(11, 5)));
        r("Rect.Contains(rect in)", new Rect(0, 0, 20, 20).contains(new Rect(2, 2, 5, 5)));
        r("Rect.Contains(rect larger)", new Rect(0, 0, 20, 20).contains(new Rect(0, 0, 30, 30)));
        p("Rect.Intersect((0,0,20,20),(10,10,20,20))", Rect.intersect(new Rect(0, 0, 20, 20), new Rect(10, 10, 20, 20)));
        p("Rect.Union((0,0,10,10),(20,30,5,5))", Rect.union(new Rect(0, 0, 10, 10), new Rect(20, 30, 5, 5)));
        r("Rect.IntersectsWith(touch (10,10))", new Rect(0, 0, 10, 10).intersectsWith(new Rect(10, 10, 5, 5)));
        r("Rect.IntersectsWith(overlap (9,9))", new Rect(0, 0, 10, 10).intersectsWith(new Rect(9, 9, 5, 5)));
        r("Rect.IntersectsWith(disjoint (11,0))", new Rect(0, 0, 10, 10).intersectsWith(new Rect(11, 0, 5, 5)));
        Rect rOff = new Rect(1, 2, 10, 10);
        rOff.offset(100, 200);
        p("Rect.Offset(100,200)", rOff);
        Rect rInf = new Rect(5, 5, 10, 10);
        rInf.inflate(2, 3);
        p("Rect.Inflate(2,3)", rInf);
        System.out.println("Rect.ToString=" + r0.toString());
        System.out.println("Rect.Empty.ToString=" + Rect.EMPTY.toString());

        // ---- CornerRadius ----
        CornerRadius cr = new CornerRadius(5);
        System.out.println("CornerRadius(5)=" + cr);
        CornerRadius cr4 = new CornerRadius(1, 2, 3, 4);
        System.out.println("CornerRadius(1,2,3,4)=" + cr4);
        r("CornerRadius.Equals", cr.equals(new CornerRadius(5)));
        System.out.println("CornerRadius.TopLeft=" + fmt(cr4.getTopLeft()) + " BottomLeft=" + fmt(cr4.getBottomLeft()));

        // ---- Color ----
        Color c1 = Color.fromArgb(255, 10, 20, 30);
        System.out.println("Color.FromArgb=" + c1.toString());
        Color c2 = Color.fromRgb(1, 2, 3);
        System.out.println("Color.FromRgb=" + c2);
        r("Color.Equals", Color.fromArgb(1, 2, 3, 4).equals(Color.fromArgb(1, 2, 3, 4)));
        r("Color.EqualsFalse", Color.fromArgb(1, 2, 3, 4).equals(Color.fromArgb(2, 2, 3, 4)));
        System.out.println("Color.A-R-G-B=" + fmt(c1.getA()) + "," + fmt(c1.getR()) + ","
                + fmt(c1.getG()) + "," + fmt(c1.getB()));
        r("Color.Hash", Color.fromArgb(1, 2, 3, 4).hashCode() == Color.fromArgb(1, 2, 3, 4).hashCode());

        // ---- Matrix ----
        Matrix m0 = new Matrix();
        r("Matrix().IsIdentity", m0.isIdentity());
        Matrix m1 = new Matrix(2, 1, 1, 3, 5, -7);
        Matrix inv = new Matrix(m1);
        inv.invert();
        System.out.println("Matrix.Invert=" + inv.toString());
        Matrix mScale = new Matrix();
        mScale.scale(2, 3);
        System.out.println("Matrix.Scale(2,3).ToString=" + mScale);
        Matrix mT = new Matrix();
        mT.translate(5, -3);
        System.out.println("Matrix.Translate(5,-3).ToString=" + mT);
        Matrix mR = new Matrix();
        mR.rotate(90);
        System.out.println("Matrix.Rotate(90).ToString=" + mR);
        p("Matrix.Transform(pt(3,4)) with (2,0,0,2,10,20)", new Matrix(2, 0, 0, 2, 10, 20).transform(new Point(3, 4)));
        r("Matrix.HasInverse(nonzero)", new Matrix(2, 0, 0, 2, 0, 0).hasInverse());
        r("Matrix.HasInverse(zero)", new Matrix(0, 0, 0, 0, 0, 0).hasInverse());
        MatrixTransform mt = new MatrixTransform(2, 0, 0, 2, 5, 5);
        p("MatrixTransform.Transform(pt(1,2))", mt.transform(new Point(1, 2)));
        System.out.println("MatrixTransform.Matrix=" + mt.getMatrix().toString());

        // ---- Matrix operator order (Append vs Prepend) ----
        Matrix mA = new Matrix();
        mA.scale(2, 2);
        mA.translate(10, 0); // append
        p("Matrix append scale->translate", mA.toString());
        p("Matrix append scale->translate pt(3,4)", mA.transform(new Point(3, 4)));
        Matrix mP = new Matrix();
        mP.scale(2, 2);
        mP.translatePrepend(10, 0); // prepend
        p("Matrix prepend scale->translate", mP.toString());
        p("Matrix prepend scale->translate pt(3,4)", mP.transform(new Point(3, 4)));
        Matrix mRA = new Matrix();
        mRA.rotateAt(90, 5, 5);
        p("Matrix.RotateAt(90@5,5)=ToString", mRA.toString());
        p("Matrix.RotateAt(90@5,5) pt(5,5)", mRA.transform(new Point(5, 5)));
        Matrix mPP = new Matrix(1, 0, 0, 1, 5, 5);
        mPP.append(new Matrix(1, 0, 0, 1, 3, 4));
        p("Matrix.Append(translate)", mPP.toString());
        p("Matrix.Append(translate) pt(0,0)", mPP.transform(new Point(0, 0)));
        Matrix mPre = new Matrix(1, 0, 0, 1, 5, 5);
        mPre.prepend(new Matrix(1, 0, 0, 1, 3, 4));
        p("Matrix.Prepend(translate)", mPre.toString());
        p("Matrix.Prepend(translate) pt(0,0)", mPre.transform(new Point(0, 0)));
        Matrix mId = new Matrix();
        mId.setIdentity();
        p("Matrix.SetIdentity", mId.toString());

        // ---- Rect.Transform via matrix (static form) ----
        Rect rectT = new Rect(0, 0, 10, 10);
        Matrix mTrans = new Matrix(2, 0, 0, 2, 5, 5);
        p("Rect.Transform((0,0,10,10) x m)", Rect.transform(rectT, mTrans));
        // in-place instance form
        Rect rectT2 = new Rect(0, 0, 10, 10);
        rectT2.transform(mTrans);
        p("Rect.Transform.instance", rectT2.toString());

        // asymmetric invert
        Matrix mAsym = new Matrix(2, 3, 4, 5, 7, 11);
        Matrix invAsym = new Matrix(mAsym);
        invAsym.invert();
        System.out.println("Matrix.InvertAsym=" + invAsym.toString());
        p("Matrix.InvertAsym pt(1,1) roundtrip", mAsym.transform(invAsym.transform(new Point(1, 1))));
    }

    static String fmt(double d) { return NetFormat.format(d); }
    static void p(String label, Object o) { System.out.println(label + " => " + o); }
    static void r(String label, Object o) { System.out.println(label + " => " + (o instanceof Boolean ? (((Boolean) o) ? "True" : "False") : o)); }
}
