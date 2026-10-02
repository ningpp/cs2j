namespace SamplePartialLib;

/// <summary>
/// Classic partial method: only a declaration, no implementation. The compiler
/// removes call sites; the partial-merge pass leaves this single-part type alone.
/// </summary>
public partial class ClassicPartial
{
    partial void BeforeSave();

    public void Save()
    {
        BeforeSave();
    }
}
