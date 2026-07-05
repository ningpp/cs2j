namespace operator_prj
{
    namespace a
    {
        public class Point1
        {
            public double X, Y;

            public static Point1 operator /(double x, Point1 p)
            {
                return new Point1(x / p.X, x / p.Y);
            }

            public static Point1 Abc(double padding, Point1 p)
            {
                return padding / p;
            }
        }
    }
    namespace b
    {
        public struct Point2
        {
            public double X, Y;
            public Point2(double x, double y) { X = x; Y = y; }

            public static Point2 operator /(double x, Point2 p)
            {
                return new Point2(x / p.X, x / p.Y);
            }

            public Point2 Abc(double padding, Point2 p)
            {
                return padding / p;
            }
        }
    }
    namespace c
    {
        public class Class1
        {
            public operator_prj.b.Point2 Abc(double padding, operator_prj.b.Point2 p)
            {
                return padding / p;
            }
        }
    }
}


