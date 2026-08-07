namespace DualTarget.Models
{
    public class Circle : Shape, IMeasurable
    {
        private readonly double _radius;

        public Circle(double radius)
        {
            _radius = radius;
        }

        public double Radius => _radius;

        public override double Area()
        {
            return 3.14159 * _radius * _radius;
        }

        public double Perimeter()
        {
            return 2 * 3.14159 * _radius;
        }

        public override string Describe()
        {
            return "circle radius=" + _radius;
        }
    }
}
