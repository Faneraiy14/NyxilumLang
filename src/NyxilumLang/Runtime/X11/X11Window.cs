using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/window.js - ергономічна обгортка над сирим
// протоколом. Консолідує все, що напрацьовано й перевірено в rawgui:
//   - малювати НАПРЯМУ у вікно до першого Expose ризиковано (WM/композитор
//     може стерти вміст при reparent) -> завжди малюємо в offscreen Pixmap
//     (backbuffer) і копіюємо на вікно через CopyArea лише в repaint()
//     (сам клас викликає це по Expose).
//   - GetImage напряму з вікна падає з BadMatch під композитором -
//     backbuffer-архітектура це обходить: усе читання пікселів (для
//     тестів) робиться з backbuffer, не з вікна.
//   - Reply/Error/Event розрізняються через X11Client - без цього паралельні
//     перемальовування й вхідні події конфліктували б.
public sealed class X11Window
{
    public X11Client Client { get; }
    public uint Wid, Gc, Pixmap;
    public X11Screen Screen;
    public X11Visual Visual;
    public int Width, Height;

    public event Action? OnExpose;
    public event Action<X11Event>? OnMouseDown;
    public event Action<X11Event>? OnMouseUp;
    public event Action<X11Event>? OnKey;
    public event Action? OnClose;
    public event Action? OnDeleteRequest;
    public event Action<int, int>? OnResize;

    private bool _hasDeleteRequestListener;
    private uint _background;
    private XidAllocator _allocId = null!;
    private uint _wmProtocolsAtom, _wmDeleteAtom;

    private X11Window(X11Client client, uint wid, uint gc, uint pixmap, X11Screen screen, X11Visual visual, int width, int height)
    {
        Client = client; Wid = wid; Gc = gc; Pixmap = pixmap; Screen = screen; Visual = visual; Width = width; Height = height;
        client.OnEvent += OnEvent;
    }

    public static X11Window Create(int x = 100, int y = 100, int width = 400, int height = 300, uint background = 0x1a1a22, string? title = null, string? display = null)
    {
        var client = X11Client.Connect(display);
        var screen = client.Conn.Screens[0];
        var rootDepthInfo = screen.Depths.Find(d => d.Depth == screen.RootDepth)!;
        var visual = rootDepthInfo.Visuals.Find(v => v.VisualId == screen.RootVisual)!;
        var allocId = new XidAllocator(client.Conn.ResourceIdBase, client.Conn.ResourceIdMask);

        uint wid = allocId.Alloc(), gc = allocId.Alloc(), pixmap = allocId.Alloc();

        client.Send(X11Protocol.BuildCreateWindow(new X11Protocol.CreateWindowArgs
        {
            Wid = wid, Parent = screen.Root, X = (short)x, Y = (short)y, Width = (ushort)width, Height = (ushort)height,
            Depth = screen.RootDepth, Visual = screen.RootVisual,
            Values = new()
            {
                [X11Protocol.CW.BackPixel] = background,
                [X11Protocol.CW.EventMask] = X11Protocol.EventMask.Exposure | X11Protocol.EventMask.StructureNotify
                    | X11Protocol.EventMask.ButtonPress | X11Protocol.EventMask.ButtonRelease | X11Protocol.EventMask.KeyPress
            }
        }));
        client.Send(X11Protocol.BuildCreateGC(gc, screen.Root, new() { [X11Protocol.GC.Foreground] = background, [X11Protocol.GC.GraphicsExposures] = 0 }));
        client.Send(X11Protocol.BuildCreatePixmap(pixmap, screen.Root, screen.RootDepth, (ushort)width, (ushort)height));

        var win = new X11Window(client, wid, gc, pixmap, screen, visual, width, height) { _background = background, _allocId = allocId };
        win.Clear(background);

        // WM_PROTOCOLS/WM_DELETE_WINDOW не є наперед визначеними атомами
        // (на відміну від WM_NAME) - їх треба "заінтернити" в сервера. Без
        // цього клік по хрестику вікна просто вбиває з'єднання (сервер
        // знищує вікно), замість того щоб дати застосунку шанс самому
        // вирішити, що робити при закритті.
        win._wmProtocolsAtom = X11Protocol.ParseInternAtomReply(client.Request(X11Protocol.BuildInternAtom("WM_PROTOCOLS")));
        win._wmDeleteAtom = X11Protocol.ParseInternAtomReply(client.Request(X11Protocol.BuildInternAtom("WM_DELETE_WINDOW")));
        var atomData = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(atomData, win._wmDeleteAtom);
        client.Send(X11Protocol.BuildChangeProperty(wid, win._wmProtocolsAtom, X11Protocol.Atom.ATOMTYPE, 32, atomData));

        if (title != null) win.SetTitle(title);

        client.Send(X11Protocol.BuildMapWindow(wid));
        return win;
    }

    public void SetTitle(string title)
    {
        var data = System.Text.Encoding.ASCII.GetBytes(title);
        Client.Send(X11Protocol.BuildChangeProperty(Wid, X11Protocol.Atom.WM_NAME, X11Protocol.Atom.STRING, 8, data));
    }

    // Позначає, що виклик коду підписався на 'delete-request' - тоді сам
    // X11Window НЕ закриває вікно автоматично при хрестику, залишаючи
    // рішення викликачу (guiOnClose тощо). Без підписника - типова
    // поведінка (закрити), як і в rawgui.
    public void MarkHasDeleteRequestListener() => _hasDeleteRequestListener = true;

    private void OnEvent(X11Event evt)
    {
        if (evt.Window != Wid && evt.EventWindow != Wid) return;

        if (evt.Name == "Expose" && evt.Count == 0)
        {
            Blit();
            OnExpose?.Invoke();
        }
        else if (evt.Name == "ButtonPress") OnMouseDown?.Invoke(evt);
        else if (evt.Name == "ButtonRelease") OnMouseUp?.Invoke(evt);
        else if (evt.Name == "KeyPress") OnKey?.Invoke(evt);
        else if (evt.Name == "DestroyNotify") OnClose?.Invoke();
        else if (evt.Name == "ClientMessage" && evt.MessageType == _wmProtocolsAtom && evt.Data.Length > 0 && evt.Data[0] == _wmDeleteAtom)
        {
            OnDeleteRequest?.Invoke();
            if (!_hasDeleteRequestListener) Close();
        }
        else if (evt.Name == "ConfigureNotify" && (evt.Width != Width || evt.Height != Height))
        {
            Resize(evt.Width, evt.Height);
        }
    }

    // Backbuffer має фіксований розмір із моменту CreatePixmap - при зміні
    // розміру вікна (WM/користувач тягне за край) старий Pixmap просто не
    // покриває нову область. Перестворюємо його під новий розмір, даємо
    // застосунку шанс перемалювати вміст (OnResize - той самий принцип,
    // що й Expose: ніколи не припускати, що старий вміст переживає зміну
    // поверхні), і показуємо результат.
    private void Resize(int width, int height)
    {
        uint newPixmap = _allocId.Alloc();
        Client.Send(X11Protocol.BuildCreatePixmap(newPixmap, Screen.Root, Screen.RootDepth, (ushort)width, (ushort)height));
        uint oldPixmap = Pixmap;
        Pixmap = newPixmap;
        Width = width; Height = height;
        Clear(_background);
        OnResize?.Invoke(width, height);
        Client.Send(X11Protocol.BuildFreePixmap(oldPixmap));
        Blit();
    }

    // Малює в backbuffer. Нічого не з'являється на екрані, поки не
    // покликати Repaint() (або поки не прийде Expose).
    public void FillRect(int x, int y, int width, int height, uint color)
    {
        Client.Send(X11Protocol.BuildChangeGC(Gc, new() { [X11Protocol.GC.Foreground] = color }));
        Client.Send(X11Protocol.BuildPolyFillRectangle(Pixmap, Gc, new[] { new X11Protocol.Rect((short)x, (short)y, (ushort)width, (ushort)height) }));
    }

    public void DrawText(string text, int x, int y, int scale = 2, uint color = 0xffffff)
    {
        Client.Send(X11Protocol.BuildChangeGC(Gc, new() { [X11Protocol.GC.Foreground] = color }));
        var rects = TextRender.BuildTextRects(text, x, y, scale);
        if (rects.Count > 0) Client.Send(X11Protocol.BuildPolyFillRectangle(Pixmap, Gc, rects));
    }

    public void Clear(uint? color = null) => FillRect(0, 0, Width, Height, color ?? _background);

    // Копіює backbuffer на видиме вікно. Безпечно викликати будь-коли -
    // якщо вікно ще не отримало перший Expose, копія просто чекає на
    // сервері в черзі запитів разом з рештою.
    public void Repaint() => Blit();

    private void Blit() => Client.Send(X11Protocol.BuildCopyArea(Pixmap, Wid, Gc, 0, 0, 0, 0, (ushort)Width, (ushort)Height));

    // Для тестів/перевірки: читає піксель напряму з backbuffer (Pixmap -
    // без обмежень видимості, на відміну від GetImage із самого вікна).
    public uint GetPixel(int x, int y)
    {
        var reply = Client.Request(X11Protocol.BuildGetImage(Pixmap, (short)x, (short)y, 1, 1));
        return BinaryPrimitives.ReadUInt32LittleEndian(reply.Extra) & 0x00ffffffu;
    }

    public void Close() => Client.Close();
}
