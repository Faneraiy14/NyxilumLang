using System.Buffers.Binary;
using System.Collections.Generic;

namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/x11-events.js - розбір Event-пакетів (32 байти,
// byte0 = код події, з можливим встановленим бітом 0x80 якщо подія прийшла
// через SendEvent - для наших потреб байдуже, тож завжди скидається (& 0x7f).
public sealed class X11Event
{
    public byte Code;
    public string Name = "";
    // KeyPress/KeyRelease/ButtonPress/ButtonRelease
    public byte Detail;
    public uint Time, Root, EventWindow, Child;
    public short RootX, RootY, EventX, EventY;
    public ushort State;
    // Expose
    public uint Window;
    public ushort X, Y, Width, Height, Count;
    // ClientMessage
    public byte Format;
    public uint MessageType;
    public uint[] Data = System.Array.Empty<uint>();

    private static readonly Dictionary<byte, string> EventNames = new()
    {
        [2] = "KeyPress", [3] = "KeyRelease", [4] = "ButtonPress", [5] = "ButtonRelease",
        [6] = "MotionNotify", [7] = "EnterNotify", [8] = "LeaveNotify", [9] = "FocusIn", [10] = "FocusOut",
        [12] = "Expose", [17] = "DestroyNotify", [18] = "UnmapNotify", [19] = "MapNotify",
        [21] = "ReparentNotify", [22] = "ConfigureNotify", [28] = "PropertyNotify", [33] = "ClientMessage"
    };

    public static X11Event Parse(byte[] head)
    {
        byte code = (byte)(head[0] & 0x7f);
        string name = EventNames.TryGetValue(code, out var n) ? n : $"Unknown({code})";
        var evt = new X11Event { Code = code, Name = name };

        switch (name)
        {
            case "KeyPress":
            case "KeyRelease":
            case "ButtonPress":
            case "ButtonRelease":
                evt.Detail = head[1];
                evt.Time = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                evt.Root = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
                evt.EventWindow = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
                evt.Child = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16));
                evt.RootX = BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(20));
                evt.RootY = BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(22));
                evt.EventX = BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(24));
                evt.EventY = BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(26));
                evt.State = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(28));
                break;
            case "Expose":
                evt.Window = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                evt.X = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
                evt.Y = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
                evt.Width = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(12));
                evt.Height = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14));
                evt.Count = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(16));
                break;
            case "DestroyNotify":
            case "UnmapNotify":
            case "MapNotify":
                evt.EventWindow = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                evt.Window = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
                break;
            case "ClientMessage":
                evt.Format = head[1];
                evt.Window = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                evt.MessageType = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
                evt.Data = new uint[5];
                for (int i = 0; i < 5; i++)
                    evt.Data[i] = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12 + i * 4));
                break;
            case "ConfigureNotify":
                evt.EventWindow = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                evt.Window = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
                evt.X = (ushort)BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16));
                evt.Y = (ushort)BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(18));
                evt.Width = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(20));
                evt.Height = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(22));
                break;
        }
        return evt;
    }
}
