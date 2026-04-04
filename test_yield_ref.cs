using System.Collections.Generic;

class Processor
{
    IEnumerable<int> Process()
    {
        int state = 0;
        yield return Advance(ref state);
        yield return Advance(ref state);
    }

    int Advance(ref int s) { return s++; }
}
