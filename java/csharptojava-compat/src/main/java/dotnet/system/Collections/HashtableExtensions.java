package dotnet.system.Collections;

import io.github.ningpp.compat.ByRef;
import io.github.ningpp.compat.CSharpHashtable;

/**
 * Compat override for the dotnet-text-regularexpressions copy of
 * HashtableExtensions. That prebuilt jar was compiled against the older
 * void ByRef.setValue ABI; this copy uses the current value-returning setValue.
 */
public final class HashtableExtensions {
    private HashtableExtensions() {
    }

    @SuppressWarnings("unchecked")
    public static <T> boolean tryGetValue(CSharpHashtable table, Object key, ByRef<T> value) {
        if (table != null && table.containsKey(key)) {
            value.setValue((T) table.get(key));
            return true;
        }
        value.setValue(null);
        return false;
    }
}
