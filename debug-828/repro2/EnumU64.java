import dotnet.text.json.JsonSerializer;
import dotnet.text.json.Serialization.Tests.SampleEnumUInt64;

public class EnumU64 {
    public static void main(String[] args) throws Exception {
        try {
            Object v = JsonSerializer.deserialize(SampleEnumUInt64.class, "18446744073709551615");
            System.out.println("OK: " + v);
        } catch (Throwable t) {
            System.out.println("THROW: " + t);
            t.printStackTrace(System.out);
        }
        // 也测直接 reader
        try {
            var opts = new dotnet.text.json.JsonReaderOptions();
            var state = new dotnet.text.json.JsonReaderState(opts);
            var reader = new dotnet.text.json.Utf8JsonReader(new io.github.ningpp.compat.ReadOnlySpan<Integer>(io.github.ningpp.compat.Encoding.getUTF8().getBytes("18446744073709551615")), true, state.clone());
            reader.read();
            io.github.ningpp.compat.LongHolder h = new io.github.ningpp.compat.LongHolder();
            boolean ok = reader.tryGetUInt64(h);
            System.out.println("tryGetUInt64=" + ok + " value=" + h.value);
        } catch (Throwable t) {
            System.out.println("READER THROW: " + t);
        }
    }
}
