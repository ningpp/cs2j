// 测试 using 别名功能
using System;
using System.Collections.Generic;

// 简单别名
using MyPoint = Geometry.Point;

// 命名空间别名
using GP = Geometry.Point;

// 泛型别名
using IntList = List<int>;
using StringDict = Dictionary<string, string>;

namespace Geometry
{
    public class Point
    {
        public int X { get; set; }
        public int Y { get; set; }

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }

        public double Distance()
        {
            return Math.Sqrt(X * X + Y * Y);
        }
    }

    public class PointExtensions
    {
        public static void Print(Point p)
        {
            Console.WriteLine($"({p.X}, {p.Y})");
        }
    }
}

namespace MyApp
{
    class Program
    {
        static void Main()
        {
            // 使用别名 MyPoint
            MyPoint p1 = new MyPoint(10, 20);

            // 使用别名 GP
            GP p2 = new GP(5, 15);

            // 使用泛型别名
            IntList numbers = new IntList();
            numbers.Add(1);
            numbers.Add(2);

            StringDict dict = new StringDict();
            dict["key"] = "value";

            Console.WriteLine(p1.Distance());
        }
    }
}
