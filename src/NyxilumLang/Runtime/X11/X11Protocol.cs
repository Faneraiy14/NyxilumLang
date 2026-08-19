using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/x11-protocol.js - кодування запитів і розбір
// відповідей. Лише та частина протоколу X11, що потрібна для мінімального
// GUI: CreateWindow/MapWindow, малювання (GC/PolyFillRectangle/PolyLine),
// backbuffer (Pixmap/CopyArea/GetImage), клавіатура (GetKeyboardMapping),
// заголовок вікна й WM_DELETE_WINDOW (InternAtom/ChangeProperty/SendEvent).
// Усі write-виклики - явно little-endian (BinaryPrimitives.*LittleEndian),
// НЕ покладаємось на ендіанність хоста - той самий підхід, що й у JS-версії
// (writeUInt32LE тощо), незалежно від того, яка платформа збирає .NET.
public static class X11Protocol
{
    public static class WindowClass { public const int CopyFromParent = 0, InputOutput = 1, InputOnly = 2; }

    public static class CW
    {
        public const uint BackPixmap = 0x00000001, BackPixel = 0x00000002, BorderPixmap = 0x00000004,
            BorderPixel = 0x00000008, BitGravity = 0x00000010, WinGravity = 0x00000020,
            BackingStore = 0x00000040, BackingPlanes = 0x00000080, BackingPixel = 0x00000100,
            OverrideRedirect = 0x00000200, SaveUnder = 0x00000400, EventMask = 0x00000800,
            DontPropagate = 0x00001000, Colormap = 0x00002000, Cursor = 0x00004000;
    }

    // Наперед визначені атоми X11 (фіксовані номери, InternAtom не потрібен).
    public static class Atom { public const uint ATOMTYPE = 4, STRING = 31, WM_NAME = 39; }

    public static class GC
    {
        public const uint Function = 0x00000001, PlaneMask = 0x00000002, Foreground = 0x00000004,
            Background = 0x00000008, LineWidth = 0x00000010, GraphicsExposures = 0x00010000;
    }

    public static class EventMask
    {
        public const uint KeyPress = 0x00000001, KeyRelease = 0x00000002, ButtonPress = 0x00000004,
            ButtonRelease = 0x00000008, EnterWindow = 0x00000010, LeaveWindow = 0x00000020,
            PointerMotion = 0x00000040, Exposure = 0x00008000, StructureNotify = 0x00020000;
    }

    public static int Pad(int n) => (4 - (n % 4)) % 4;

    // value-mask/value-list вимагають, щоб значення йшли в порядку зростання
    // номера біта, незалежно від порядку, у якому їх передав викликач.
    private static (uint mask, List<uint> list) EncodeValueList(Dictionary<uint, uint> values)
    {
        var bits = new List<uint>(values.Keys);
        bits.Sort();
        uint mask = 0;
        var list = new List<uint>();
        foreach (var bit in bits) { mask |= bit; list.Add(values[bit]); }
        return (mask, list);
    }

    public sealed class CreateWindowArgs
    {
        public uint Wid, Parent; public short X, Y; public ushort Width, Height;
        public byte Depth; public uint Visual; public ushort BorderWidth = 0;
        public Dictionary<uint, uint> Values = new();
    }

    public static byte[] BuildCreateWindow(CreateWindowArgs a)
    {
        var (mask, list) = EncodeValueList(a.Values);
        const int fixedLen = 32;
        var buf = new byte[fixedLen + list.Count * 4];
        buf[0] = 1; // opcode CreateWindow
        buf[1] = a.Depth;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)((fixedLen + list.Count * 4) / 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), a.Wid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), a.Parent);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(12), a.X);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(14), a.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16), a.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(18), a.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20), a.BorderWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22), (ushort)WindowClass.InputOutput);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), a.Visual);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), mask);
        int off = fixedLen;
        foreach (var v in list) { BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), v); off += 4; }
        return buf;
    }

    public static byte[] BuildMapWindow(uint wid)
    {
        var buf = new byte[8];
        buf[0] = 8; // opcode MapWindow
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), wid);
        return buf;
    }

    public static byte[] BuildQueryTree(uint wid)
    {
        var buf = new byte[8];
        buf[0] = 15;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), wid);
        return buf;
    }

    public static byte[] BuildGetGeometry(uint drawable)
    {
        var buf = new byte[8];
        buf[0] = 14;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), drawable);
        return buf;
    }

    public static byte[] BuildCreateGC(uint cid, uint drawable, Dictionary<uint, uint> values)
    {
        var (mask, list) = EncodeValueList(values);
        const int fixedLen = 16;
        var buf = new byte[fixedLen + list.Count * 4];
        buf[0] = 55; // opcode CreateGC
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)((fixedLen + list.Count * 4) / 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), cid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), drawable);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), mask);
        int off = fixedLen;
        foreach (var v in list) { BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), v); off += 4; }
        return buf;
    }

    public static byte[] BuildChangeGC(uint gc, Dictionary<uint, uint> values)
    {
        var (mask, list) = EncodeValueList(values);
        const int fixedLen = 12;
        var buf = new byte[fixedLen + list.Count * 4];
        buf[0] = 56; // opcode ChangeGC
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)((fixedLen + list.Count * 4) / 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), gc);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), mask);
        int off = fixedLen;
        foreach (var v in list) { BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), v); off += 4; }
        return buf;
    }

    public readonly struct Rect { public readonly short X, Y; public readonly ushort Width, Height;
        public Rect(short x, short y, ushort w, ushort h) { X = x; Y = y; Width = w; Height = h; } }

    public static byte[] BuildPolyFillRectangle(uint drawable, uint gc, IReadOnlyList<Rect> rects)
    {
        const int fixedLen = 12;
        var buf = new byte[fixedLen + rects.Count * 8];
        buf[0] = 70; // opcode PolyFillRectangle
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)((fixedLen + rects.Count * 8) / 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), drawable);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), gc);
        int off = fixedLen;
        foreach (var r in rects)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(off), r.X);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(off + 2), r.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 4), r.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 6), r.Height);
            off += 8;
        }
        return buf;
    }

    public readonly struct Point { public readonly short X, Y; public Point(short x, short y) { X = x; Y = y; } }

    public static byte[] BuildPolyLine(uint drawable, uint gc, IReadOnlyList<Point> points, byte coordinateMode = 0)
    {
        const int fixedLen = 12;
        var buf = new byte[fixedLen + points.Count * 4];
        buf[0] = 65; // opcode PolyLine
        buf[1] = coordinateMode;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)((fixedLen + points.Count * 4) / 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), drawable);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), gc);
        int off = fixedLen;
        foreach (var p in points)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(off), p.X);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(off + 2), p.Y);
            off += 4;
        }
        return buf;
    }

    public static byte[] BuildCreatePixmap(uint pid, uint drawable, byte depth, ushort width, ushort height)
    {
        var buf = new byte[16];
        buf[0] = 53; // opcode CreatePixmap
        buf[1] = depth;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), drawable);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14), height);
        return buf;
    }

    public static byte[] BuildCopyArea(uint src, uint dst, uint gc, short srcX, short srcY, short dstX, short dstY, ushort width, ushort height)
    {
        var buf = new byte[28];
        buf[0] = 62; // opcode CopyArea
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), src);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), dst);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), gc);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(16), srcX);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(18), srcY);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(20), dstX);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(22), dstY);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24), width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(26), height);
        return buf;
    }

    public static byte[] BuildFreePixmap(uint pixmap)
    {
        var buf = new byte[8];
        buf[0] = 54; // opcode FreePixmap
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), pixmap);
        return buf;
    }

    public static byte[] BuildGetImage(uint drawable, short x, short y, ushort width, ushort height, byte format = 2, uint planeMask = 0xffffffff)
    {
        var buf = new byte[20];
        buf[0] = 73; // opcode GetImage
        buf[1] = format; // 2 = ZPixmap
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), drawable);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(8), x);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(10), y);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14), height);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), planeMask);
        return buf;
    }

    public static byte[] BuildInternAtom(string name, bool onlyIfExists = false)
    {
        var nameBuf = System.Text.Encoding.ASCII.GetBytes(name);
        const int fixedLen = 8;
        int total = fixedLen + nameBuf.Length + Pad(nameBuf.Length);
        var buf = new byte[total];
        buf[0] = 16; // opcode InternAtom
        buf[1] = (byte)(onlyIfExists ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)(total / 4));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)nameBuf.Length);
        nameBuf.CopyTo(buf.AsSpan(8));
        return buf;
    }

    public static uint ParseInternAtomReply(X11Reply reply) => BinaryPrimitives.ReadUInt32LittleEndian(reply.Head.AsSpan(8));

    public static byte[] BuildChangeProperty(uint window, uint property, uint type, byte format, byte[] data, byte mode = 0)
    {
        // data: сирі байти; кількість "одиниць формату" (n) - для format=8
        // це data.Length, для format=32 - data.Length/4.
        int unitSize = format / 8;
        int n = data.Length / unitSize;
        const int fixedLen = 24;
        int total = fixedLen + data.Length + Pad(data.Length);
        var buf = new byte[total];
        buf[0] = 18; // opcode ChangeProperty
        buf[1] = mode;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)(total / 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), window);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), property);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), type);
        buf[16] = format;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)n);
        data.CopyTo(buf.AsSpan(24));
        return buf;
    }

    public static byte[] BuildGetKeyboardMapping(byte firstKeycode, byte count)
    {
        var buf = new byte[8];
        buf[0] = 101; // opcode GetKeyboardMapping
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 2);
        buf[4] = firstKeycode;
        buf[5] = count;
        return buf;
    }

    public static Dictionary<byte, uint[]> ParseGetKeyboardMappingReply(X11Reply reply, byte firstKeycode, int count)
    {
        byte keysymsPerKeycode = reply.Head[1];
        var map = new Dictionary<byte, uint[]>();
        for (int i = 0; i < count; i++)
        {
            var keysyms = new uint[keysymsPerKeycode];
            for (int k = 0; k < keysymsPerKeycode; k++)
                keysyms[k] = BinaryPrimitives.ReadUInt32LittleEndian(reply.Extra.AsSpan((i * keysymsPerKeycode + k) * 4));
            map[(byte)(firstKeycode + i)] = keysyms;
        }
        return map;
    }

    public static byte[] BuildClientMessageEvent(uint window, uint type, uint[] data, byte format = 32)
    {
        var buf = new byte[32];
        buf[0] = 33; // код події ClientMessage
        buf[1] = format;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), window);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), type);
        for (int i = 0; i < 5; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12 + i * 4), i < data.Length ? data[i] : 0);
        return buf;
    }

    public static byte[] BuildSendEvent(uint destination, byte[] eventBuffer, bool propagate = false, uint eventMask = 0)
    {
        var buf = new byte[44];
        buf[0] = 25; // opcode SendEvent
        buf[1] = (byte)(propagate ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 11);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), destination);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), eventMask);
        eventBuffer.CopyTo(buf.AsSpan(12));
        return buf;
    }

    public sealed class GeometryReply
    {
        public byte Depth; public uint Root; public short X, Y; public ushort Width, Height, BorderWidth;
    }

    public static GeometryReply ParseGetGeometryReply(X11Reply reply) => new()
    {
        Depth = reply.Head[1],
        Root = BinaryPrimitives.ReadUInt32LittleEndian(reply.Head.AsSpan(8)),
        X = BinaryPrimitives.ReadInt16LittleEndian(reply.Head.AsSpan(12)),
        Y = BinaryPrimitives.ReadInt16LittleEndian(reply.Head.AsSpan(14)),
        Width = BinaryPrimitives.ReadUInt16LittleEndian(reply.Head.AsSpan(16)),
        Height = BinaryPrimitives.ReadUInt16LittleEndian(reply.Head.AsSpan(18)),
        BorderWidth = BinaryPrimitives.ReadUInt16LittleEndian(reply.Head.AsSpan(20))
    };

    public sealed class QueryTreeReply { public uint Root, Parent; public List<uint> Children = new(); }

    public static QueryTreeReply ParseQueryTreeReply(X11Reply reply)
    {
        var result = new QueryTreeReply
        {
            Root = BinaryPrimitives.ReadUInt32LittleEndian(reply.Head.AsSpan(8)),
            Parent = BinaryPrimitives.ReadUInt32LittleEndian(reply.Head.AsSpan(12))
        };
        int numChildren = BinaryPrimitives.ReadUInt16LittleEndian(reply.Head.AsSpan(16));
        for (int i = 0; i < numChildren; i++)
            result.Children.Add(BinaryPrimitives.ReadUInt32LittleEndian(reply.Extra.AsSpan(i * 4)));
        return result;
    }

    public static byte[] BuildGetProperty(uint window, uint property, uint type = 0, bool deleteProp = false, uint longOffset = 0, uint longLength = 32)
    {
        var buf = new byte[24];
        buf[0] = 20; // opcode GetProperty
        buf[1] = (byte)(deleteProp ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), window);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), property);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), type); // 0 = AnyPropertyType
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), longOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), longLength);
        return buf;
    }

    public static byte[] ParseGetPropertyReply(X11Reply reply)
    {
        byte format = reply.Head[1];
        uint valueLength = BinaryPrimitives.ReadUInt32LittleEndian(reply.Head.AsSpan(16)); // "одиниць формату"
        int unitSize = format == 0 ? 0 : format / 8;
        var result = new byte[valueLength * unitSize];
        Array.Copy(reply.Extra, result, result.Length);
        return result;
    }
}

// Reply/Error - обидва рівно 32 байти (Head) + опційні додаткові дані
// (Extra, довжина = replyLength*4). Читає X11Client._loop() (порт
// rawgui/src/x11-client.js), не сам протокол напряму - тут лише DTO.
public sealed class X11Reply
{
    public byte[] Head = System.Array.Empty<byte>();
    public byte[] Extra = System.Array.Empty<byte>();
}
