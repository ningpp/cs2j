using System.IO;
class Sample : System.IDisposable {
    readonly StreamReader streamReader;
    Sample(Stream s) { streamReader = new StreamReader(s); }
    protected virtual void Dispose(bool disposing) {
        if (disposing)
            streamReader.Close();
    }
    public void Dispose() { Dispose(true); }
}
