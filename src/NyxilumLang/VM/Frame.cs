namespace NyxilumLang.VM;

// Локальні змінні одного виклику функції. Раніше - Dictionary<int, object>
// (пошук хешем на КОЖНЕ читання/запис змінної і новий словник на кожен
// виклик) - найбільша частина часу простих циклів. Тут - масив слотів за
// номером, який дає компілятор (слоти нумеруються з 0 для кожної функції).
//
// Незаповнений слот - маркер Unset, а не null: null - звичайне значення
// змінної (var x = null), а LOAD_VAR незаданого слота має давати 0, як і
// раніше з Dictionary.TryGetValue.
public sealed class Frame
{
    private static readonly object Unset = new();
    private object?[] _slots;

    public Frame()
    {
        _slots = new object?[8];
        Array.Fill(_slots, Unset);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(int idx, out object value)
    {
        if ((uint)idx < (uint)_slots.Length)
        {
            var v = _slots[idx];
            if (!ReferenceEquals(v, Unset))
            {
                value = v!;
                return true;
            }
        }
        value = null!;
        return false;
    }

    public object this[int idx]
    {
        get => TryGetValue(idx, out var v) ? v : throw new KeyNotFoundException($"слот {idx}");
        set
        {
            if ((uint)idx >= (uint)_slots.Length) Grow(idx);
            _slots[idx] = value;
        }
    }

    private void Grow(int idx)
    {
        int old = _slots.Length;
        int size = Math.Max(old * 2, idx + 1);
        Array.Resize(ref _slots, size);
        Array.Fill(_slots, Unset, old, size - old);
    }
}
