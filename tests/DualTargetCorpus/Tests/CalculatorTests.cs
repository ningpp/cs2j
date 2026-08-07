using Xunit;
using DualTarget;
using DualTarget.Models;
using DualTarget.Geometry;

namespace DualTarget.Tests
{
    public class CalculatorTests
    {
        [Fact]
        public void Add_AddsTwoNumbers()
        {
            var calc = new Calculator();
            Assert.Equal(7, calc.Add(3, 4));
        }

        [Fact]
        public void Scale_AppliesToSum()
        {
            var calc = new Calculator(10);
            Assert.Equal(70, calc.Add(3, 4));
            Assert.Equal(3, calc.Add("1", "2"));
        }

        [Theory]
        [InlineData(1, 2, 3)]
        [InlineData(10, 20, 30)]
        [InlineData(-1, 1, 0)]
        public void Add_Theory(int a, int b, int expected)
        {
            var calc = new Calculator();
            Assert.Equal(expected, calc.Add(a, b));
        }

        [Fact]
        public void Circle_Area_And_Perimeter()
        {
            var circle = new Circle(2);
            Assert.True(circle.Area() > 12);
            Assert.True(circle.Perimeter() > 12);
            Assert.Contains("circle", circle.Describe());
        }

        [Fact]
        public void Point_Move_And_Add()
        {
            var p = new Point(1, 2);
            var moved = p.Move(3, 4);
            Assert.Equal(4, moved.X);
            Assert.Equal(6, moved.Y);
            var sum = p + new Point(10, 20);
            Assert.Equal(11, sum.X);
            Assert.Equal(22, sum.Y);
        }
    }
}
