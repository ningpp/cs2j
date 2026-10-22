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
            if (csharpArray.hasNonZeroLowerBound()) {
                throw new ArgumentOutOfRangeException("destinationIndex");
            }
            if (index < 0) {
                throw new ArgumentOutOfRangeException("destinationIndex");
            }
            if (index > csharpArray.getLength() - getCount()) {
                throw new ArgumentException("Destination array was not long enough.");
            }
            int i = 0;
            for (Object element : this) {
                csharpArray.setValue(element, index + i);
                i++;
            }
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

    boolean getIsSynchronized();
    Object getSyncRoot();
}
