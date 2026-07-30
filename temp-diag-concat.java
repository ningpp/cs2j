import io.github.ningpp.compat.CSharpGenericIList;
import io.github.ningpp.compat.CSharpGenericIterable;
import java.util.ArrayList;
import io.github.ningpp.compat.CSharpList;
import java.util.stream.StreamSupport;
import java.util.stream.Collectors;
import io.github.ningpp.compat.ArgumentNullException;

public class Sample {
    public static CSharpGenericIterable<String> concatLists(CSharpGenericIList<String> strs1, CSharpGenericIList<String> strs2) {
        return CSharpGenericIterable.from(concatLists_ProceduralLinq1(CSharpGenericIterable.from(strs1), strs2, strs1, CSharpGenericIterable.from(strs2.stream().map(str -> str).collect(CSharpList.toCSharpList()))));
    }
    static CSharpGenericIterable<String> concatLists_ProceduralLinq1(CSharpGenericIterable<String> _linqitems, CSharpGenericIList<String> strs2, CSharpGenericIList<String> strs1, CSharpGenericIterable<String> _second) {
        if (_linqitems == null) {
        throw new ArgumentNullException();
        }
        var _list = new CSharpList<String>();
        for (String _linqitem : _linqitems) {
        var _linqitem1 = _linqitem;
        _list.add(_linqitem1);
        }
        for (String _concatItem : _second) {
        _list.add(_concatItem);
        }
        return CSharpGenericIterable.from(_list);
    }
}

