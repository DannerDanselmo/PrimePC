namespace PrimePC.Sensors;

/// <summary>Buffer circular de capacidade fixa (não é thread-safe; o chamador sincroniza).</summary>
public sealed class RingBuffer<T>
{
    private T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Count { get; private set; }
    public int Capacity => _items.Length;

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public T[] ToArray()
    {
        var result = new T[Count];
        for (int i = 0; i < Count; i++)
            result[i] = _items[(_start + i) % _items.Length];
        return result;
    }

    /// <summary>Altera a capacidade preservando os itens mais recentes.</summary>
    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var current = ToArray();
        _items = new T[capacity];
        _start = 0;
        Count = 0;
        foreach (var item in current.Skip(Math.Max(0, current.Length - capacity)))
            Add(item);
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }
}
