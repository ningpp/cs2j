namespace DualTarget.Models
{
    public abstract class Shape
    {
        public string Name { get; set; } = "shape";

        public abstract double Area();

        public virtual string Describe()
        {
            return Name + " area=" + Area();
        }
    }

    public interface IMeasurable
    {
        double Perimeter();
    }
}
