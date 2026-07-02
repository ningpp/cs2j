package io.github.ningpp.compat;

import java.util.Comparator;
import java.util.Iterator;
import java.util.NoSuchElementException;
import java.util.PriorityQueue;

public class CSharpPriorityQueue<E, P> {
    private final PriorityQueue<PriorityEntry<E, P>> pq;

    public CSharpPriorityQueue() {
        this.pq = new PriorityQueue<>((a, b) -> {
            @SuppressWarnings("unchecked")
            Comparable<Object> cp = (Comparable<Object>) a.priority;
            return cp.compareTo(b.priority);
        });
    }

    public CSharpPriorityQueue(Comparator<? super P> priorityComparator) {
        this.pq = new PriorityQueue<>((a, b) -> priorityComparator.compare(a.priority, b.priority));
    }

    public void enqueue(E element, P priority) {
        pq.add(new PriorityEntry<>(element, priority));
    }

    public E dequeue() {
        PriorityEntry<E, P> entry = pq.poll();
        if (entry == null) {
            throw new NoSuchElementException("PriorityQueue is empty");
        }
        return entry.element;
    }

    public E peek() {
        PriorityEntry<E, P> entry = pq.peek();
        if (entry == null) {
            throw new NoSuchElementException("PriorityQueue is empty");
        }
        return entry.element;
    }

    public void clear() {
        pq.clear();
    }

    public int getCount() {
        return pq.size();
    }

    public E tryDequeue() {
        PriorityEntry<E, P> entry = pq.poll();
        if (entry == null) {
            return null;
        }
        return entry.element;
    }

    public E tryPeek() {
        PriorityEntry<E, P> entry = pq.peek();
        if (entry == null) {
            return null;
        }
        return entry.element;
    }

    public int ensureCapacity(int capacity) {
        // PriorityQueue doesn't expose ensureCapacity; no-op
        return pq.size();
    }

    public void trimExcess() {
        // PriorityQueue doesn't expose trimExcess; no-op
    }

    public Iterator<E> iterator() {
        return new Iterator<E>() {
            private final Iterator<PriorityEntry<E, P>> it = pq.iterator();

            @Override
            public boolean hasNext() {
                return it.hasNext();
            }

            @Override
            public E next() {
                return it.next().element;
            }
        };
    }

    // Helper class for priority tracking
    private static class PriorityEntry<E, P> {
        final E element;
        final P priority;

        @SuppressWarnings("unchecked")
        PriorityEntry(E element, P priority) {
            this.element = element;
            this.priority = priority;
        }
    }
}
