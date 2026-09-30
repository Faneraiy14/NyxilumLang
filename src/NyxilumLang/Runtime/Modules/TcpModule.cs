using System.Net.Security;
using System.Net.Sockets;

namespace NyxilumLang.Runtime.Modules;

// Голий TCP - "двері" в мережу, і НІЧОГО більше. Навмисно мінімальний:
// жодного протоколу, кодування чи крипти тут немає - драйвери БД
// (lib/postgres.nx, lib/mysql.nx), байтові утиліти (lib/bytes.nx) і крипта
// (lib/crypto.nx) написані на самій NyxilumLang поверх цих п'яти функцій.
//
// Байти - звичайний масив чисел 0..255 (без окремого типу у VM).
//   tcpConnect(host, port)  -> з'єднання
//   tcpSend(conn, bytes)
//   tcpReceive(conn, n)     -> рівно n байт (чекає, доки прийдуть; кидає, якщо
//                              з'єднання закрилось раніше)
//   tcpStartTls(conn, host, verify?) - увімкнути TLS посеред з'єднання (Postgres
//                              SSLRequest, MySQL SSL-флаг у handshake). Єдиний
//                              шматок, який неможливо написати на .nx: TLS
//                              потребує криптографії рівня ОС і перевірки
//                              сертифікатів. verify=false - шифрувати без
//                              перевірки сертифіката (як sslmode=require у
//                              libpq: самопідписані сертифікати БД - норма).
//   tcpClose(conn)
public static class TcpModule
{
    private const int TimeoutMs = 30000;

    private sealed class NxTcpConnection
    {
        public required TcpClient Client;
        public required Stream Stream;
        public override string ToString() => "<tcp>";
    }

    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["tcpConnect"] = Connect;
        registry["tcpSend"] = Send;
        registry["tcpReceive"] = Receive;
        registry["tcpStartTls"] = StartTls;
        registry["tcpClose"] = Close;
    }

    private static NxTcpConnection Conn(object? arg) =>
        arg as NxTcpConnection ?? throw new Exception("очікувалось TCP-з'єднання з tcpConnect()");

    private static object? Connect(object[] args)
    {
        Sandbox.CheckNetwork();
        string host = args[0]?.ToString() ?? "";
        int port = Convert.ToInt32(args[1]);
        var client = new TcpClient { ReceiveTimeout = TimeoutMs, SendTimeout = TimeoutMs, NoDelay = true };
        if (!client.ConnectAsync(host, port).Wait(TimeoutMs))
        {
            client.Dispose();
            throw new Exception($"tcpConnect: тайм-аут підключення до {host}:{port}");
        }
        return new NxTcpConnection { Client = client, Stream = client.GetStream() };
    }

    private static object? Send(object[] args)
    {
        var conn = Conn(args[0]);
        var list = args[1] as List<object> ?? throw new Exception("tcpSend: очікувався масив байтів");
        var buffer = new byte[list.Count];
        for (int i = 0; i < list.Count; i++)
            buffer[i] = (byte)Convert.ToInt32(list[i]);
        conn.Stream.Write(buffer, 0, buffer.Length);
        conn.Stream.Flush();
        return null;
    }

    private static object? Receive(object[] args)
    {
        var conn = Conn(args[0]);
        int n = Convert.ToInt32(args[1]);
        var buffer = new byte[n];
        int read = 0;
        while (read < n)
        {
            int got = conn.Stream.Read(buffer, read, n - read);
            if (got == 0)
                throw new Exception($"tcpReceive: з'єднання закрито (отримано {read} з {n} байт)");
            read += got;
        }
        var result = new List<object>(n);
        foreach (var b in buffer)
            result.Add((double)b);
        return result;
    }

    private static object? StartTls(object[] args)
    {
        var conn = Conn(args[0]);
        string host = args[1]?.ToString() ?? "";
        bool verify = args.Length < 3 || args[2] is not bool b || b;
        var ssl = verify
            ? new SslStream(conn.Stream, leaveInnerStreamOpen: false)
            : new SslStream(conn.Stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        ssl.AuthenticateAsClient(host);
        conn.Stream = ssl;
        return null;
    }

    private static object? Close(object[] args)
    {
        var conn = Conn(args[0]);
        conn.Stream.Dispose();
        conn.Client.Dispose();
        return null;
    }
}
