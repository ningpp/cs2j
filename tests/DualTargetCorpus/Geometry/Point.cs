namespace DualTarget.Geometry
{
    public record Point(int X, int Y)
    {
        public Point Move(int dx, int dy) => new Point(X + dx, Y + dy);

        public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
    }
}
