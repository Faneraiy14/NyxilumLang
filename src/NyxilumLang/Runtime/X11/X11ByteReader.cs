using System.Net.Sockets;

namespace NyxilumLang.Runtime.X11;

// Аналог rawgui/src/byte-reader.js, але СИНХРОННИЙ (блокуючий): NyxilumLang-
// білтіни викликаються синхронно (як readFile), і X11-цикл читання в
// C#-порту крутиться на окремому потоці (X11Client.cs), тож немає потреби
// в async/Promise-чергах очікувачів, як у JS-версії - Socket.Receive сам
// блокує потік до появи даних.
public sealed class X11ByteReader
{
    private readonly Socket _socket;
    private readonly System.Collections.Generic.Queue<byte> _buffer = new();
    private readonly byte[] _chunk = new byte[65536];

    public X11ByteReader(Socket socket) { _socket = socket; }

    // Блокує, доки не набереться рівно n байт (X11 - потік байтів, відповідь
    // може прийти кількома TCP/Unix-socket фрагментами довільного розміру).
    public byte[] ReadBytes(int n)
    {
        while (_buffer.Count < n)
        {
            int read = _socket.Receive(_chunk);
            if (read == 0) throw new System.Exception("X11: з'єднання закрито сервером");
            for (int i = 0; i < read; i++) _buffer.Enqueue(_chunk[i]);
        }
        var result = new byte[n];
        for (int i = 0; i < n; i++) result[i] = _buffer.Dequeue();
        return result;
    }
}
