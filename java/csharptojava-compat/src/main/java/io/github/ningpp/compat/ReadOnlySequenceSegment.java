package io.github.ningpp.compat;

/**
 * Compat for System.IO.Pipelines.ReadOnlySequenceSegment&lt;T&gt;.
 */
public abstract class ReadOnlySequenceSegment<T> {
    private ReadOnlyMemory<T> memory;
    private ReadOnlySequenceSegment<T> next;
    private long runningIndex;

    public ReadOnlyMemory<T> getMemory() {
        return memory;
    }

    public void setMemory(ReadOnlyMemory<T> value) {
        this.memory = value;
    }

    public ReadOnlySequenceSegment<T> getNext() {
        return next;
    }

    public void setNext(ReadOnlySequenceSegment<T> value) {
        this.next = value;
    }

    public long getRunningIndex() {
        return runningIndex;
    }

    public void setRunningIndex(long value) {
        this.runningIndex = value;
    }
}
