namespace SampleExtensionMembers;

// NEGATIVE SAMPLE (excluded from compilation via <Compile Remove="Negatives\**">):
// indexers inside extension blocks are rejected with CS2J1209 and the whole
// block is left as extension sugar.
public static partial class BlockHosts
{
    extension(Box<int> box)
    {
        public int this[int index] => box.Value + index;
    }
}
