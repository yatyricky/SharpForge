namespace SFLib.Collections;

public sealed class Queue<T>
{
    public int Count => 0;

    public void Enqueue(T item)
    {
    }

    public T Dequeue() => default!;

    public T Peek() => default!;

    public void Clear()
    {
    }

    public bool Contains(T item) => false;
    public T[] ToArray() => default!;
    public Enumerator GetEnumerator() => default!;

    public class Enumerator
    {
        public T Current => default!;
        public bool MoveNext() => false;
    }
}