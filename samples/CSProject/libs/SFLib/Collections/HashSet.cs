namespace SFLib.Collections;

public sealed class HashSet<T>
{
    public int Count => 0;

    public bool Add(T item) => false;
    public bool Contains(T item) => false;
    public bool Remove(T item) => false;
    public void Clear()
    {
    }
    public T[] ToArray() => default!;
    public Enumerator GetEnumerator() => default!;

    public class Enumerator
    {
        public T Current => default!;
        public bool MoveNext() => false;
    }
}