#nullable enable
#pragma warning disable CS8981, CS1591

using SFLib.Interop;

namespace StdLib;

/// <summary>
/// A basic list backed by a Lua sequential table.
/// Uses table.insert/table.remove for array operations.
/// C# indexer (0-based) maps to Lua table (1-based) via get_Item/set_Item.
/// </summary>
public class List<T> : IEnumerable<T>
{
    private LuaObject _items;
    private int _size;

    public int Count => _size;

    public T this[int index]
    {
        get => default!;
        set { }
    }

    public void Add(T item)
    {
        table.insert(_items, item);
        _size = _size + 1;
    }

    public void Clear()
    {
        _items = LuaInterop.CreateTable();
        _size = 0;
    }

    public bool Remove(T item)
    {
        var index = IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        table.remove(_items, index + 1);
        _size = _size - 1;
    }

    public int IndexOf(T item)
    {
        for (var i = 0; i < _size; i++)
        {
            if (Equals(this[i], item)) return i;
        }

        return -1;
    }
}
