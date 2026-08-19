namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/xid.js - видача унікальних ідентифікаторів
// ресурсів (XID). Сервер віддає клієнту resourceIdBase + resourceIdMask
// при підключенні (X11Connection.cs); кожен новий ресурс (вікно, pixmap,
// gc, ...) клієнт вигадує сам за формулою base | (n & mask).
public sealed class XidAllocator
{
    private readonly uint _base, _mask;
    private uint _counter;

    public XidAllocator(uint resourceIdBase, uint resourceIdMask)
    {
        _base = resourceIdBase;
        _mask = resourceIdMask;
    }

    public uint Alloc()
    {
        _counter++;
        if (_counter > _mask) throw new System.Exception("XID вичерпано (лічильник перевищив resourceIdMask)");
        return _base | (_counter & _mask);
    }
}
