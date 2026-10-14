using System.Runtime.CompilerServices;

namespace NyxilumLang.VM;

// Стек значень VM на масиві: Push/Pop без версіонування й зайвих перевірок
// Stack<T> (найчастіші операції інтерпретатора).
public sealed class ValueStack
{
    private object?[] _items = new object?[256];
    private int _count;

    public int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(object? v)
    {
        if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        _items[_count++] = v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object Pop()
    {
        if (_count == 0) throw new InvalidOperationException("Stack empty");
        var v = _items[--_count];
        _items[_count] = null; // не тримати сміття від збирача
        return v!;
    }

    // Як Stack<T>.ElementAt: 0 - верхівка.
    public object ElementAt(int fromTop) => _items[_count - 1 - fromTop]!;
}
