using System.Net;
using System.Text;
using NyxilumLang.VM;

namespace NyxilumLang.Runtime.Modules;

public static class HttpModule
{
    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["httpServer"] = CreateServer;
        registry["httpGet"] = HttpGet;
        registry["urlStatus"] = UrlStatus;
        registry["httpPost"] = HttpPost;
        registry["httpRequest"] = HttpRequest;
    }

    private static object? UrlStatus(object[] args)
    {
        Sandbox.CheckNetwork();
        try
        {
            string url = args[0].ToString()!;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = client.GetAsync(url).GetAwaiter().GetResult();
            return (double)(int)response.StatusCode;
        }
        catch
        {
            return -1.0;
        }
    }

    // httpServer(port, handler) — handler(request) викликається на кожен
    // запит. Раніше передавав лише (path, method): жодного способу
    // прочитати тіло POST-запиту (форми, JSON API, вебхуки) чи заголовки -
    // будь-який застосунок складніший за "віддати статичний текст" був
    // неможливий. request - мапа {path, method, body, query, headers}:
    // ОДИН аргумент, а не (path, method, body, ...) позиційно, навмисно -
    // InvokeFunctionValue штовхає ВСІ передані аргументи на спільний стек,
    // а функція знімає РІВНО стільки, скільки в неї своїх параметрів;
    // додавання ще одних позиційних аргументів до наявного (path, method)
    // лишило б зайве значення висіти на стеку для наявних 2-параметрових
    // handler'ів. Мапа з одним аргументом уникає цього назавжди - розширити
    // новим ключем (напр. cookies) пізніше можна без жодного зламу.
    //
    // Відповідь handler: звичайний рядок (як і раніше - тіло, статус 200,
    // text/html) АБО мапа {status?, body?, contentType?} для повного
    // контролю (потрібно для API, що мають повертати конкретні коди: 201
    // Created, 404 Not Found, JSON замість HTML тощо).
    private static object? CreateServer(object[] args)
    {
        Sandbox.CheckNetwork();
        int port = Convert.ToInt32(args[0]);
        var handlerRef = (NxFunctionRef)args[1];
        var vm = VirtualMachine.Current!;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        Console.WriteLine($"[Nx] Сервер запущено на порту {port}");

        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
            }
            catch (HttpListenerException)
            {
                break;
            }

            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;
            string query = context.Request.Url?.Query ?? "";
            if (query.StartsWith('?')) query = query.Substring(1);

            string requestBody = "";
            if (context.Request.HasEntityBody)
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                requestBody = reader.ReadToEnd();
            }

            var headersMap = new NxMap();
            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key == null) continue;
                headersMap.Entries[key] = context.Request.Headers[key] ?? "";
            }

            var requestMap = new NxMap();
            requestMap.Entries["path"] = path;
            requestMap.Entries["method"] = method;
            requestMap.Entries["body"] = requestBody;
            requestMap.Entries["query"] = query;
            requestMap.Entries["headers"] = headersMap;

            int statusCode = 200;
            string body;
            string contentType = "text/html; charset=utf-8";
            try
            {
                var result = vm.InvokeFunctionValue(handlerRef, new object[] { requestMap });
                if (result is NxMap resultMap)
                {
                    if (resultMap.Entries.TryGetValue("status", out var s)) statusCode = Convert.ToInt32(s);
                    if (resultMap.Entries.TryGetValue("body", out var b)) body = b?.ToString() ?? "";
                    else body = "";
                    if (resultMap.Entries.TryGetValue("contentType", out var ct)) contentType = ct?.ToString() ?? contentType;
                }
                else
                {
                    body = result?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                statusCode = 500;
                body = "Internal Server Error: " + ex.Message;
            }

            var buffer = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }

        return null;
    }

    private static object? HttpGet(object[] args)
    {
        Sandbox.CheckNetwork();
        string url = args[0].ToString()!;
        using var client = new HttpClient();
        return client.GetStringAsync(url).GetAwaiter().GetResult();
    }

    // httpPost(url, body) — тіло POST-запиту як рядок, за замовчуванням
    // application/json (найчастіший випадок - надіслати JSON у REST API/
    // вебхук). Повертає лише тіло відповіді, як httpGet, для найпростішого
    // "надіслати й прочитати" сценарію без потреби перевіряти статус-код.
    private static object? HttpPost(object[] args)
    {
        Sandbox.CheckNetwork();
        string url = args[0].ToString()!;
        string body = args.Length > 1 ? args[1]?.ToString() ?? "" : "";
        using var client = new HttpClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = client.PostAsync(url, content).GetAwaiter().GetResult();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    // httpRequest(url, method, body?, headers?) — довільний HTTP-метод
    // (PUT/DELETE/PATCH тощо), на відміну від httpGet/httpPost, повертає
    // МАПУ {status, body}, бо для довільних методів код відповіді зазвичай
    // важливіший за саме тіло (204 No Content, 404 тощо). headers - мапа
    // (newMap/mapSet), потрібна для API з авторизацією через заголовок
    // (напр. Discord REST: "Authorization" -> "Bot <token>"), а не в URL,
    // як у Telegram.
    private static object? HttpRequest(object[] args)
    {
        Sandbox.CheckNetwork();
        string url = args[0].ToString()!;
        string method = args.Length > 1 ? args[1]?.ToString()?.ToUpperInvariant() ?? "GET" : "GET";
        string body = args.Length > 2 ? args[2]?.ToString() ?? "" : "";
        var headers = args.Length > 3 ? args[3] as NxMap : null;

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (!string.IsNullOrEmpty(body))
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (headers != null)
        {
            foreach (var kv in headers.Entries)
            {
                string key = kv.Key?.ToString() ?? "";
                string value = kv.Value?.ToString() ?? "";
                if (!request.Headers.TryAddWithoutValidation(key, value))
                    request.Content?.Headers.TryAddWithoutValidation(key, value);
            }
        }

        var response = client.SendAsync(request).GetAwaiter().GetResult();
        string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        var map = new NxMap();
        map.Entries["status"] = (double)(int)response.StatusCode;
        map.Entries["body"] = responseBody;
        return map;
    }
}
