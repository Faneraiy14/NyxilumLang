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

    // Стан для тайм-аутного wsReceive (нижче): ОДНА pending ReceiveAsync-
    // операція, що переживає тайм-аут і перевикористовується наступним
    // викликом wsReceive, замість того щоб стартувати другу паралельну
    // (WebSocket дозволяє лише один активний Receive одночасно - друга
    // кинула б "There is already one outstanding read operation").
    public Task<WebSocketReceiveResult>? PendingReceive;
    public byte[] ReceiveBuffer = new byte[16384];
    public StringBuilder PartialMessage = new();
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
    //
    // НАВМИСНО без CancellationToken на самому ReceiveAsync: скасування
    // ЦІЄЇ операції токеном не просто "перериває чекання" - воно переводить
    // WebSocket у стан Aborted (задокументована поведінка System.Net.
    // WebSockets), після чого будь-яка наступна send/receive/close на
    // тому ж сокеті кидає "invalid state" - тайм-аут ламав би з'єднання
    // назавжди замість того, щоб просто повідомити "поки нічого немає".
    // Замість цього чекаємо через Task.WhenAny(receiveTask, Delay): якщо
    // тайм-аут настав раніше - повертаємо null, а САМУ ReceiveAsync НЕ
    // чіпаємо й лишаємо доживати в NxWebSocket.PendingReceive, щоб
    // наступний виклик wsReceive забрав її результат (чи знову почекав),
    // замість того щоб почати другу паралельну (WebSocket дозволяє лише
    // одну активну Receive-операцію одночасно).
    private static object? WsReceive(object[] args)
    {
        var ws = (NxWebSocket)args[0];
        int timeoutMs = args.Length > 1 ? Convert.ToInt32(args[1]) : 30000;

        while (true)
        {
            var receiveTask = ws.PendingReceive ??= ws.Socket.ReceiveAsync(ws.ReceiveBuffer, CancellationToken.None);
            var winner = Task.WhenAny(receiveTask, Task.Delay(timeoutMs)).GetAwaiter().GetResult();
            if (winner != receiveTask)
            {
                return null; // тайм-аут - сокет неушкоджений, receiveTask лишається чекати далі
            }

            ws.PendingReceive = null;
            WebSocketReceiveResult result;
            try
            {
                result = receiveTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ws.PartialMessage.Clear();
                throw new Exception("WebSocket-з'єднання розірвано: " + ex.Message);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // На відміну від тайм-ауту (return null - "нічого нового,
                // спробуй ще") розрив з'єднання - це кінець сесії: кидаємо,
                // щоб виклик (напр. dPollLoop) не крутився навічно на null,
                // а помітив закриття й завершив цикл.
                throw new Exception(
                    $"WebSocket закрито сервером (код {(int?)result.CloseStatus}, причина: {result.CloseStatusDescription})");
            }

            ws.PartialMessage.Append(Encoding.UTF8.GetString(ws.ReceiveBuffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                var msg = ws.PartialMessage.ToString();
                ws.PartialMessage.Clear();
                return msg;
            }
            // Повідомлення ще не завершене (кілька фреймів) - одразу чекаємо
            // наступний фрейм у тому ж виклику wsReceive, з тим самим
            // тайм-аутом на решту повідомлення.
        }
    }

    // CloseAsync за протоколом чекає на close-фрейм У ВІДПОВІДЬ від іншого
    // боку, перш ніж повернути керування. Якщо той бік ніколи не прочитає
    // (напр. застряг у власному циклі відправки й не викликає wsReceive) -
    // без обмеження часу цей виклик блокував би скрипт НАЗАВЖДИ, і його
    // навіть не зловити через try/catch (це не помилка, а вічне
    // очікування). Тайм-аут перетворює це на звичайну, як завжди
    // прогнозовану (макс. 5с) операцію - з'єднання все одно Dispose()
    // нижче, навіть якщо чемний handshake не встиг завершитись.
    private static object? WsClose(object[] args)
    {
        var ws = (NxWebSocket)args[0];
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ws.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // з'єднання вже розірване з іншого боку (напр. Discord після
            // невдалого Identify), або інший бік не відповів на close за
            // 5с (тайм-аут вище) - закривати вже нема що, це не помилка.
        }
        ws.Dispose();
        return null;
    }
}
