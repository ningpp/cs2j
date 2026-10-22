package io.github.ningpp.compat;

public interface CSharpCollection extends CSharpIterable<Object> {
    @Override
    CSharpGenericEnumerator iterator();
    default int size() { return getCount(); }
    default int getCount() { return size(); }

    /**
     * C# ICollection.CopyTo(Array, int). A single Object parameter accepts both the
     * compat CSharpArray view and Java object arrays, so `copyTo(null, 0)` is no
     * longer ambiguous between overloads (the C# parameter is System.Array anyway).
     */
    default void copyTo(Object array, int index) {
        if (array == null) {
            throw new ArgumentNullException("array");
        }
        if (array instanceof CSharpArray csharpArray) {
            // C# dispatches ICollection.CopyTo(Array,int) virtually to the class's
            // own CopyTo implementation (CaptureCollection/GroupCollection write
            // through Array.SetValue and surface IndexOutOfRangeException, while
            // List<T>-backed collections validate up front with
            // ArgumentOutOfRangeException). Delegate to the CSharpArray overload:
            // classes that declare one (the rendering of their public
            // CopyTo(Array,int)) keep their own C# semantics, and the interface
            // default below preserves the generic validation for those that don't.
            copyTo(csharpArray, index);
            return;
        }
        if (array.getClass().isArray()) {
            if (array.getClass().getComponentType().isArray()) {
                throw new ArgumentException("Multidimensional arrays are not supported.");
            }
            int length = java.lang.reflect.Array.getLength(array);
            if (index < 0) {
                throw new ArgumentOutOfRangeException("destinationIndex");
            }
            if (index > length - getCount()) {
                throw new ArgumentException("Destination array was not long enough.");
            }
            int i = 0;
            for (Object element : this) {
                java.lang.reflect.Array.set(array, index + i, element);
                i++;
            }
            return;
        }
        throw new ArgumentException("array");
    }

    /**
     * C# ICollection.CopyTo(Array, int) for a compat array view. The interface
     * default carries the generic up-front validation shared by List&lt;T&gt;-style
     * implementations; converted classes that declare a public
     * CopyTo(Array, int) render this same signature and virtually override it, so
     * a call through the interface dispatches to each class's own C# semantics.
     */
    default void copyTo(CSharpArray array, int index) {
        if (array == null) {
            throw new ArgumentNullException("array");
        }
        if (array.hasNonZeroLowerBound()) {
            throw new ArgumentOutOfRangeException("destinationIndex");
        }
        if (index < 0) {
            throw new ArgumentOutOfRangeException("destinationIndex");
        }
        if (index > array.getLength() - getCount()) {
            throw new ArgumentException("Destination array was not long enough.");
        }
        int i = 0;
        for (Object element : this) {
            array.setValue(element, index + i);
            i++;
        }
    }

    boolean getIsSynchronized();
    Object getSyncRoot();
}
