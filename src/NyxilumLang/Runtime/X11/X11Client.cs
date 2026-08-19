using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/x11-client.js - цикл диспетчеризації Reply/Error/
// Event. Проблема, яку це вирішує (виявлена емпірично в самому rawgui):
// сервер шле Event-пакети (Expose, ConfigureNotify, ReparentNotify від WM,
// ...) АСИНХРОННО, у будь-який момент, упереміш із Reply на наші запити-
// з-відповіддю. Наївний "надіслав запит - прочитав рівно 32(+N) байт як
// відповідь" ламається, щойно подія прилітає між ними.
//
// Рішення - один постійний фоновий потік читання пакетів із сокета. Кожен
// 32-байтовий пакет має byte0: 0=Error, 1=Reply, 2+=Event. Reply/Error
// розбираються по FIFO-черзі очікувачів (X11 гарантує порядок відповідей
// на запити ОДНОГО з'єднання) - Event ніколи не займає місце в цій черзі,
// а йде окремим маршрутом (подія OnEvent).
//
// На відміну від JS-версії (async/Promise), тут запит-з-відповіддю
// (Request) синхронно БЛОКУЄ потік виклику через ManualResetEventSlim,
// доки фоновий потік не покладе результат - узгоджується з синхронною
// природою NyxilumLang-білтінів (як readFile), без async у сигнатурах.
public sealed class X11Client
{
    public X11Connection Conn { get; }
    public event Action<X11Event>? OnEvent;
    public event Action<Exception>? OnClose;

    private sealed class Waiter
    {
        public readonly ManualResetEventSlim Signal = new(false);
        public X11Reply? Result;
        public Exception? Error;
    }

    private readonly Queue<Waiter> _pending = new();
    private readonly object _pendingLock = new();
    private volatile bool _closed;
    private readonly Thread _loopThread;

    private X11Client(X11Connection conn)
    {
        Conn = conn;
        _loopThread = new Thread(Loop) { IsBackground = true, Name = "X11-dispatch" };
        _loopThread.Start();
    }

    public static X11Client Connect(string? display = null) => new(X11Connection.Connect(display));

    private void Loop()
    {
        while (!_closed)
        {
            byte[] head;
            try { head = Conn.Reader.ReadBytes(32); }
            catch (Exception ex)
            {
                _closed = true;
                FailAllPending(ex);
                OnClose?.Invoke(ex);
                return;
            }
            byte type = head[0];

            if (type == 0)
            {
                byte errorCode = head[1];
                ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
                var err = new Exception($"X11 Error: code={errorCode} seq={seq}");
                Waiter? w = DequeueWaiter();
                if (w != null) { w.Error = err; w.Signal.Set(); }
                continue;
            }

            if (type == 1)
            {
                uint replyLength = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
                byte[] extra = replyLength > 0 ? Conn.Reader.ReadBytes((int)(replyLength * 4)) : Array.Empty<byte>();
                Waiter? w = DequeueWaiter();
                if (w != null) { w.Result = new X11Reply { Head = head, Extra = extra }; w.Signal.Set(); }
                continue;
            }

            OnEvent?.Invoke(X11Event.Parse(head));
        }
    }

    private Waiter? DequeueWaiter() { lock (_pendingLock) return _pending.Count > 0 ? _pending.Dequeue() : null; }

    private void FailAllPending(Exception ex)
    {
        lock (_pendingLock)
        {
            while (_pending.Count > 0) { var w = _pending.Dequeue(); w.Error = ex; w.Signal.Set(); }
        }
    }

    // Запит БЕЗ відповіді (CreateWindow, MapWindow, PolyFillRectangle, ...)
    public void Send(byte[] buf) => Conn.Socket.Send(buf);

    // Запит З відповіддю (QueryTree, GetGeometry, GetImage, ...) - додає
    // очікувача в FIFO-чергу ПЕРЕД записом у сокет, щоб не було вікна
    // гонки з фоновим потоком читання, потім блокує до Signal.Set().
    public X11Reply Request(byte[] buf)
    {
        var waiter = new Waiter();
        lock (_pendingLock) _pending.Enqueue(waiter);
        Conn.Socket.Send(buf);
        waiter.Signal.Wait();
        if (waiter.Error != null) throw waiter.Error;
        return waiter.Result!;
    }

    public void Close()
    {
        _closed = true;
        try { Conn.Socket.Close(); } catch { }
    }
}
