using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/x11-connection.js - connection setup handshake
// з X-сервером. Протокол X11 - бінарний, поверх сирого байтового потоку
// (тут - Unix-сокет /tmp/.X11-unix/X{N}). Реалізовано рівно ту частину,
// яка потрібна для мінімального клієнта: підключення, автентифікація
// (MIT-MAGIC-COOKIE-1), розбір відповіді сервера (екрани/візуали/формати
// пікселів).
public sealed class X11Visual
{
    public uint VisualId; public byte VisualClass; public byte BitsPerRgbValue;
    public ushort ColormapEntries; public uint RedMask, GreenMask, BlueMask;
}

public sealed class X11Depth
{
    public byte Depth;
    public List<X11Visual> Visuals = new();
}

public sealed class X11Screen
{
    public uint Root, DefaultColormap, WhitePixel, BlackPixel, CurrentInputMasks;
    public ushort WidthInPixels, HeightInPixels, WidthInMillimeters, HeightInMillimeters;
    public uint RootVisual;
    public byte RootDepth;
    public List<X11Depth> Depths = new();
}

public sealed class X11Connection
{
    public Socket Socket = null!;
    public X11ByteReader Reader = null!;
    public ushort ProtoMajor, ProtoMinor;
    public uint ReleaseNumber, ResourceIdBase, ResourceIdMask;
    public string Vendor = "";
    public ushort MaxRequestLength;
    public List<X11Screen> Screens = new();
    public byte MinKeycode, MaxKeycode;
    public byte ImageByteOrder;

    private static int Pad(int n) => (4 - (n % 4)) % 4;

    // display - напр. ":0" чи ":0.1" (номер після крапки - екран, не
    // дисплей, для автентифікації неважливий). null -> $DISPLAY, чи ":0"
    // якщо змінна не встановлена.
    public static X11Connection Connect(string? display = null)
    {
        display ??= Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        var match = Regex.Match(display, @"^:(\d+)(?:\.(\d+))?$");
        if (!match.Success) throw new Exception($"Не розпізнано формат DISPLAY: \"{display}\"");
        var displayNum = match.Groups[1].Value;

        var socketPath = $"/tmp/.X11-unix/X{displayNum}";
        var cookie = XAuth.GetAuthCookie(displayNum);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));

        var reader = new X11ByteReader(socket);

        // ---------- Запит на підключення ----------
        var authName = System.Text.Encoding.ASCII.GetBytes(cookie.Name);
        var authData = cookie.Data;
        int nameLen = authName.Length, dataLen = authData.Length;

        var header = new byte[12];
        header[0] = 0x6c; // 'l' - little-endian (наша реальна архітектура)
        header[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 11);  // protocol-major-version
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0);   // protocol-minor-version
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)nameLen);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)dataLen);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 0);

        var request = new byte[12 + nameLen + Pad(nameLen) + dataLen + Pad(dataLen)];
        int off = 0;
        header.CopyTo(request.AsSpan(off)); off += 12;
        authName.CopyTo(request.AsSpan(off)); off += nameLen + Pad(nameLen);
        authData.CopyTo(request.AsSpan(off)); off += dataLen + Pad(dataLen);
        socket.Send(request);

        // ---------- Відповідь ----------
        var head = reader.ReadBytes(8);
        byte success = head[0];
        ushort protoMajor = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
        ushort protoMinor = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4));
        ushort additionalLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)); // у 4-байтових словах

        var rest = reader.ReadBytes(additionalLen * 4);

        if (success == 0)
        {
            byte reasonLen = head[1];
            string reason = System.Text.Encoding.ASCII.GetString(rest, 0, reasonLen);
            socket.Close();
            throw new Exception($"X11 Connection Failed: {reason}");
        }
        if (success == 2)
        {
            socket.Close();
            throw new Exception("X11 сервер вимагає Authenticate-етап (не підтримується - MIT-MAGIC-COOKIE-1 мав вистачити)");
        }

        // ---------- Розбір Success-відповіді ----------
        int p = 0;
        byte U8() => rest[p++];
        ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(rest.AsSpan(p)); p += 2; return v; }
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(p)); p += 4; return v; }
        void Skip(int n) => p += n;
        byte[] Bytes(int n) { var v = new byte[n]; Array.Copy(rest, p, v, 0, n); p += n; return v; }

        var conn = new X11Connection { Socket = socket, Reader = reader, ProtoMajor = protoMajor, ProtoMinor = protoMinor };
        conn.ReleaseNumber = U32();
        conn.ResourceIdBase = U32();
        conn.ResourceIdMask = U32();
        U32(); // motionBufferSize - не використовується
        int vendorLen = U16();
        conn.MaxRequestLength = U16();
        int numScreens = U8();
        int numFormats = U8();
        conn.ImageByteOrder = U8();
        U8(); U8(); U8(); // bitmapFormatBitOrder/ScanlineUnit/ScanlinePad - не використовуються
        conn.MinKeycode = U8();
        conn.MaxKeycode = U8();
        Skip(4); // unused

        conn.Vendor = System.Text.Encoding.ASCII.GetString(Bytes(vendorLen));
        Skip(Pad(vendorLen));

        for (int i = 0; i < numFormats; i++) { U8(); U8(); U8(); Skip(5); } // formats - не використовуються далі

        for (int i = 0; i < numScreens; i++)
        {
            var screen = new X11Screen
            {
                Root = U32(), DefaultColormap = U32(), WhitePixel = U32(), BlackPixel = U32(), CurrentInputMasks = U32(),
                WidthInPixels = U16(), HeightInPixels = U16(), WidthInMillimeters = U16(), HeightInMillimeters = U16()
            };
            U16(); U16(); // minInstalledMaps/maxInstalledMaps - не використовуються
            screen.RootVisual = U32();
            U8(); U8(); // backingStores/saveUnders - не використовуються
            screen.RootDepth = U8();
            int numDepths = U8();

            for (int d = 0; d < numDepths; d++)
            {
                var depth = new X11Depth { Depth = U8() };
                Skip(1);
                int numVisuals = U16();
                Skip(4);
                for (int v = 0; v < numVisuals; v++)
                {
                    depth.Visuals.Add(new X11Visual
                    {
                        VisualId = U32(), VisualClass = U8(), BitsPerRgbValue = U8(), ColormapEntries = U16(),
                        RedMask = U32(), GreenMask = U32(), BlueMask = U32()
                    });
                    Skip(4);
                }
                screen.Depths.Add(depth);
            }
            conn.Screens.Add(screen);
        }

        return conn;
    }
}
