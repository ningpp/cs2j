using System;
using System.Globalization;

class Program
{
    static void Main()
    {
        // Force invariant culture so number formatting matches Java's Double.toString closely enough
        // for the primitive members (we compare with a tolerance on doubles anyway).
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // ---- Point ----
        var p0 = new System.Windows.Point();
        P("Point()", p0);
        var p1 = new System.Windows.Point(3.5, -2.0);
        P("Point(3.5,-2.0)", p1);
        Console.WriteLine("Point.X=" + p1.X + " Point.Y=" + p1.Y);
        var p2 = new System.Windows.Point(1, 2);
        p2.Offset(3, 4);
        P("Point.Offset(3,4)", p2);
        Reliability("Point.Equals(1,2)==(1,2)", new System.Windows.Point(1, 2) == new System.Windows.Point(1, 2));
        Reliability("Point.Equals(1,2)!=(2,1)", new System.Windows.Point(1, 2) == new System.Windows.Point(2, 1));
        Reliability("Point.Hash(1,2)", new System.Windows.Point(1, 2).GetHashCode() == new System.Windows.Point(1, 2).GetHashCode());
        P("Point.Add((1,2)+V(1,2))", System.Windows.Point.Add(new System.Windows.Point(1, 2), new System.Windows.Vector(1, 2)));
        P("Point.Subtract((1,2)-V(3,4))", System.Windows.Point.Subtract(new System.Windows.Point(1, 2), new System.Windows.Vector(3, 4)));
        P("Point.SubtractToVector", System.Windows.Point.Subtract(new System.Windows.Point(5, 5), new System.Windows.Point(1, 2)));
        Console.WriteLine("Point.ToString=" + new System.Windows.Point(1.5, 2.25).ToString());

        // ---- Size ----
        var s0 = new System.Windows.Size();
        P("Size()", s0);
        var s1 = new System.Windows.Size(100, 50);
        P("Size(100,50)", s1);
        Reliability("Size.Empty.IsEmpty", System.Windows.Size.Empty.IsEmpty);
        Reliability("Size(10,10).IsEmpty", new System.Windows.Size(10, 10).IsEmpty);
        Reliability("Size.Equals", new System.Windows.Size(10, 20) == new System.Windows.Size(10, 20));
        Reliability("Size.NotEquals", new System.Windows.Size(10, 20) != new System.Windows.Size(20, 10));
        Console.WriteLine("Size.ToString=" + new System.Windows.Size(10, 20).ToString());
        Console.WriteLine("Size.Empty.ToString=" + System.Windows.Size.Empty.ToString());

        // ---- Rect ----
        var r0 = new System.Windows.Rect(10, 20, 100, 50);
        P("Rect(10,20,100,50)", r0);
        try { var rNeg = new System.Windows.Rect(10, 20, -100, -50); P("Rect(10,20,-100,-50)", rNeg); }
        catch (Exception ex) { Console.WriteLine("Rect(10,20,-100,-50) THROWS: " + ex.GetType().Name); }
        try { var rNeg2 = new System.Windows.Rect(10, 20, 100, -50); P("Rect(10,20,100,-50)", rNeg2); }
        catch (Exception ex) { Console.WriteLine("Rect(10,20,100,-50) THROWS: " + ex.GetType().Name); }
        var rEmpty = System.Windows.Rect.Empty;
        P("Rect.Empty", rEmpty);
        Reliability("Rect.Empty.IsEmpty", rEmpty.IsEmpty);
        var r2p = new System.Windows.Rect(new System.Windows.Point(10, 20), new System.Windows.Point(0, 0));
        P("Rect(pt(10,20),pt(0,0))", r2p);
        Reliability("Rect(pt,pt).X", r2p.X);
        var rSize = new System.Windows.Rect(new System.Windows.Size(50, 60));
        P("Rect(Size(50,60))", rSize);
        var rLocSize = new System.Windows.Rect(new System.Windows.Point(5, 6), new System.Windows.Size(50, 60));
        P("Rect(pt(5,6),Size(50,60))", rLocSize);
        // corners
        Console.WriteLine("Rect.corners=" + r0.Left + "," + r0.Top + "," + r0.Right + "," + r0.Bottom);
        Console.WriteLine("Rect.TopLeft=" + r0.TopLeft + " BottomRight=" + r0.BottomRight);
        // contains
        Reliability("Rect.Contains(pt(5,5))", new System.Windows.Rect(0, 0, 10, 10).Contains(new System.Windows.Point(5, 5)));
        Reliability("Rect.Contains(edge 10,10)", new System.Windows.Rect(0, 0, 10, 10).Contains(new System.Windows.Point(10, 10)));
        Reliability("Rect.Contains(11,5)", new System.Windows.Rect(0, 0, 10, 10).Contains(new System.Windows.Point(11, 5)));
        Reliability("Rect.Contains(rect in)", new System.Windows.Rect(0, 0, 20, 20).Contains(new System.Windows.Rect(2, 2, 5, 5)));
        Reliability("Rect.Contains(rect larger)", new System.Windows.Rect(0, 0, 20, 20).Contains(new System.Windows.Rect(0, 0, 30, 30)));
        // intersect / union
        P("Rect.Intersect((0,0,20,20),(10,10,20,20))", System.Windows.Rect.Intersect(new System.Windows.Rect(0, 0, 20, 20), new System.Windows.Rect(10, 10, 20, 20)));
        P("Rect.Union((0,0,10,10),(20,30,5,5))", System.Windows.Rect.Union(new System.Windows.Rect(0, 0, 10, 10), new System.Windows.Rect(20, 30, 5, 5)));
        // intersectsWith (touch edge = false)
        Reliability("Rect.IntersectsWith(touch (10,10))", new System.Windows.Rect(0, 0, 10, 10).IntersectsWith(new System.Windows.Rect(10, 10, 5, 5)));
        Reliability("Rect.IntersectsWith(overlap (9,9))", new System.Windows.Rect(0, 0, 10, 10).IntersectsWith(new System.Windows.Rect(9, 9, 5, 5)));
        Reliability("Rect.IntersectsWith(disjoint (11,0))", new System.Windows.Rect(0, 0, 10, 10).IntersectsWith(new System.Windows.Rect(11, 0, 5, 5)));
        // offset / inflate
        var rOff = new System.Windows.Rect(1, 2, 10, 10);
        rOff.Offset(100, 200);
        P("Rect.Offset(100,200)", rOff);
        var rInf = new System.Windows.Rect(5, 5, 10, 10);
        rInf.Inflate(2, 3);
        P("Rect.Inflate(2,3)", rInf);
        Console.WriteLine("Rect.ToString=" + r0.ToString());
        Console.WriteLine("Rect.Empty.ToString=" + System.Windows.Rect.Empty.ToString());

        // ---- CornerRadius ----
        var cr = new System.Windows.CornerRadius(5);
        Console.WriteLine("CornerRadius(5)=" + cr);
        var cr4 = new System.Windows.CornerRadius(1, 2, 3, 4);
        Console.WriteLine("CornerRadius(1,2,3,4)=" + cr4);
        Reliability("CornerRadius.Equals", cr == new System.Windows.CornerRadius(5));
        Console.WriteLine("CornerRadius.TopLeft=" + cr4.TopLeft + " BottomLeft=" + cr4.BottomLeft);

        // ---- Color ----
        var c1 = System.Windows.Media.Color.FromArgb(255, 10, 20, 30);
        Console.WriteLine("Color.FromArgb=" + c1.ToString());
        var c2 = System.Windows.Media.Color.FromRgb(1, 2, 3);
        Console.WriteLine("Color.FromRgb=" + c2);
        Reliability("Color.Equals", System.Windows.Media.Color.FromArgb(1, 2, 3, 4) == System.Windows.Media.Color.FromArgb(1, 2, 3, 4));
        Reliability("Color.EqualsFalse", System.Windows.Media.Color.FromArgb(1, 2, 3, 4) == System.Windows.Media.Color.FromArgb(2, 2, 3, 4));
        Console.WriteLine("Color.A-R-G-B=" + c1.A + "," + c1.R + "," + c1.G + "," + c1.B);
        Reliability("Color.Hash", System.Windows.Media.Color.FromArgb(1, 2, 3, 4).GetHashCode() == System.Windows.Media.Color.FromArgb(1, 2, 3, 4).GetHashCode());

        // ---- Matrix ----
        var m0 = new System.Windows.Media.Matrix();
        Reliability("Matrix().IsIdentity", m0.IsIdentity);
        var m1 = new System.Windows.Media.Matrix(2, 1, 1, 3, 5, -7);
        var inv = m1;
        inv.Invert();
        Console.WriteLine("Matrix.Invert=" + inv.ToString());
        var mScale = new System.Windows.Media.Matrix();
        mScale.Scale(2, 3);
        Console.WriteLine("Matrix.Scale(2,3).ToString=" + mScale);
        var mT = new System.Windows.Media.Matrix();
        mT.Translate(5, -3);
        Console.WriteLine("Matrix.Translate(5,-3).ToString=" + mT);
        var mR = new System.Windows.Media.Matrix();
        mR.Rotate(90);
        Console.WriteLine("Matrix.Rotate(90).ToString=" + mR);
        P("Matrix.Transform(pt(3,4)) with (2,0,0,2,10,20)", new System.Windows.Media.Matrix(2, 0, 0, 2, 10, 20).Transform(new System.Windows.Point(3, 4)));
        Reliability("Matrix.HasInverse(nonzero)", new System.Windows.Media.Matrix(2, 0, 0, 2, 0, 0).HasInverse);
        Reliability("Matrix.HasInverse(zero)", new System.Windows.Media.Matrix(0, 0, 0, 0, 0, 0).HasInverse);
        // MatrixTransform
        var mt = new System.Windows.Media.MatrixTransform(2, 0, 0, 2, 5, 5);
        P("MatrixTransform.Transform(pt(1,2))", mt.Transform(new System.Windows.Point(1, 2)));
        Console.WriteLine("MatrixTransform.Matrix=" + mt.Matrix.ToString());

        // ---- Matrix operator order (Append vs Prepend) ----
        var mA = new System.Windows.Media.Matrix();
        mA.Scale(2, 2);
        mA.Translate(10, 0); // append
        P("Matrix append scale->translate", mA.ToString());
        P("Matrix append scale->translate pt(3,4)", mA.Transform(new System.Windows.Point(3, 4)));
        var mP = new System.Windows.Media.Matrix();
        mP.Scale(2, 2);
        mP.TranslatePrepend(10, 0); // prepend
        P("Matrix prepend scale->translate", mP.ToString());
        P("Matrix prepend scale->translate pt(3,4)", mP.Transform(new System.Windows.Point(3, 4)));
        // rotateAt
        var mRA = new System.Windows.Media.Matrix();
        mRA.RotateAt(90, 5, 5);
        P("Matrix.RotateAt(90@5,5)=ToString", mRA.ToString());
        P("Matrix.RotateAt(90@5,5) pt(5,5)", mRA.Transform(new System.Windows.Point(5, 5)));
        var mPP = new System.Windows.Media.Matrix(1, 0, 0, 1, 5, 5);
        mPP.Append(new System.Windows.Media.Matrix(1, 0, 0, 1, 3, 4));
        P("Matrix.Append(translate)", mPP.ToString());
        P("Matrix.Append(translate) pt(0,0)", mPP.Transform(new System.Windows.Point(0, 0)));
        var mPre = new System.Windows.Media.Matrix(1, 0, 0, 1, 5, 5);
        mPre.Prepend(new System.Windows.Media.Matrix(1, 0, 0, 1, 3, 4));
        P("Matrix.Prepend(translate)", mPre.ToString());
        P("Matrix.Prepend(translate) pt(0,0)", mPre.Transform(new System.Windows.Point(0, 0)));
        // identity
        var mId = new System.Windows.Media.Matrix();
        mId.SetIdentity();
        P("Matrix.SetIdentity", mId.ToString());
        // ---- Rect.Transform via matrix (static form) ----
        var rectT = new System.Windows.Rect(0, 0, 10, 10);
        var mTrans = new System.Windows.Media.Matrix(2, 0, 0, 2, 5, 5);
        P("Rect.Transform((0,0,10,10) x m)", System.Windows.Rect.Transform(rectT, mTrans));
        // in-place instance form
        var rectT2 = new System.Windows.Rect(0, 0, 10, 10);
        rectT2.Transform(mTrans);
        P("Rect.Transform.instance", rectT2.ToString());

        // asymmetric invert
        var mAsym = new System.Windows.Media.Matrix(2, 3, 4, 5, 7, 11);
        var invAsym = mAsym;
        invAsym.Invert();
        Console.WriteLine("Matrix.InvertAsym=" + invAsym.ToString());
        P("Matrix.InvertAsym pt(1,1) roundtrip", mAsym.Transform(invAsym.Transform(new System.Windows.Point(1, 1))));
    }

    static void P(string label, object o) => Console.WriteLine(label + " => " + o);
    static void Reliability(string label, object o) => Console.WriteLine(label + " => " + o);
}
