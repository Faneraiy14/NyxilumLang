using System.Net.WebSockets;
using System.Text;

namespace NyxilumLang.Runtime.Modules;

// Мінімальний WebSocket-клієнт (System.Net.WebSockets.ClientWebSocket з
// БКЛ - фреймінг/handshake/TLS уже реалізовані там, переписувати нема
// сенсу). Додано конкретно заради Discord Gateway: на відміну від
// Telegram (звичайний HTTPS+JSON), Discord вимагає постійне з'єднання
// для отримання подій у реальному часі - REST API там лише для
// НАДСИЛАННЯ (httpRequest із заголовком Authorization цілком вистачає).
//
// Socket типізований АБСТРАКТНИМ WebSocket, не конкретним ClientWebSocket:
// серверний прийом з'єднання (HttpListenerContext.AcceptWebSocketAsync,
// HttpModule.cs) віддає інший конкретний тип, що НЕ успадковує
// ClientWebSocket, але SendAsync/ReceiveAsync/CloseAsync нижче однаково
// визначені на самому абстрактному класі - той самий NxWebSocket і ті самі
// wsSend/wsReceive/wsClose працюють для клієнтського й серверного боку.
public sealed class NxWebSocket : IDisposable
{
    public WebSocket Socket { get; }
    public NxWebSocket(WebSocket socket) => Socket = socket;
    public void Dispose() => Socket.Dispose();
}

public static class WebSocketModule
{
    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["wsConnect"] = WsConnect;
        registry["wsSend"] = WsSend;
        registry["wsReceive"] = WsReceive;
        registry["wsClose"] = WsClose;
    }

    private static object? WsConnect(object[] args)
    {
        Sandbox.CheckNetwork();
        string url = args[0].ToString()!;
        var socket = new ClientWebSocket();
        socket.ConnectAsync(new Uri(url), CancellationToken.None).GetAwaiter().GetResult();
        return new NxWebSocket(socket);
    }

    private static object? WsSend(object[] args)
    {
        var ws = (NxWebSocket)args[0];
        var bytes = Encoding.UTF8.GetBytes(args[1]?.ToString() ?? "");
        ws.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
            .GetAwaiter().GetResult();
        return null;
    }

    // timeoutMs - скільки максимум чекати наступне повідомлення; null,
    // якщо за цей час нічого не прийшло. Це навмисно, а не "чекати
    // назавжди": викликач (напр. lib/discord.nx) сам мусить регулярно
    // прокидатись, щоб устигнути надіслати heartbeat за розкладом
    // Discord - постійне блокування без тайм-ауту зробило б це неможливим.
    private static object? WsReceive(object[] args)
    {
        var ws = (NxWebSocket)args[0];
        int timeoutMs = args.Length > 1 ? Convert.ToInt32(args[1]) : 30000;

        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[16384];
        var sb = new StringBuilder();
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = ws.Socket.ReceiveAsync(buffer, cts.Token).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // На відміну від тайм-ауту (return null - "нічого нового,
                    // спробуй ще") розрив з'єднання - це кінець сесії:
                    // кидаємо, щоб виклик (напр. dPollLoop) не крутився
                    // навічно на null, а помітив закриття й завершив цикл.
                    throw new Exception(
                        $"WebSocket закрито сервером (код {(int?)result.CloseStatus}, причина: {result.CloseStatusDescription})");
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);
            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            return null; // тайм-аут - не помилка, а сигнал "нічого не прийшло"
        }
    }

    private static object? WsClose(object[] args)
    {
        var ws = (NxWebSocket)args[0];
        try
        {
            ws.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // з'єднання вже розірване з іншого боку (напр. Discord після
            // невдалого Identify) - закривати вже нема що, це не помилка.
        }
        ws.Dispose();
        return null;
    }
}
