namespace Demo
{

    class Path
    {
        internal PathEdge FirstEdge { get; set; }

        internal PathEdge LastEdge { get; set; }

        internal void SetFirstEdge(PathEdge edge)
        {
            LastEdge = FirstEdge = edge;
            edge.Path = this;
        }
    }
    class PathEdge
    {
        internal Path Path { get; set; }
    }

}