using System.Diagnostics;
using System.Text;

namespace NyxilumLang.Runtime.Modules;

// Зовнішні процеси ОС — на відміну від spawn()/workerJoin() (Concurrency-
// Module.cs), які запускають NyxilumLang-функцію в новому воркері всередині
// того ж .NET-процесу, тут йдеться про справжній ЗОВНІШНІЙ виконуваний файл
// (java, git, будь-що) — без цього написати лаунчер (запустити java -jar ...)
// неможливо в принципі.
public static class ProcessModule
{
    // Опаковий "хендл" — .nx-скрипт передає його назад у procWait/procKill/...,
    // сам System.Diagnostics.Process лишається всередині (як guiWindow()
    // повертає Form, а не малює нею напряму в .nx-коді).
    public sealed class ProcHandle
    {
        public Process Proc = null!;
        public readonly StringBuilder Out = new();
        public readonly StringBuilder Err = new();
        public readonly object Lock = new();
        public StreamWriter? Input;
    }

    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["procStart"] = Start;
        registry["procRun"] = args => Run(Start(args));
        registry["procWait"] = args => {
            var h = (ProcHandle)args[0];
            h.Proc.WaitForExit();
            return (double)h.Proc.ExitCode;
        };
        registry["procIsRunning"] = args => {
            var h = (ProcHandle)args[0];
            try { return !h.Proc.HasExited; } catch { return false; }
        };
        registry["procKill"] = args => {
            var h = (ProcHandle)args[0];
            // Kill() на вже завершений процес кидає InvalidOperationException —
            // "убити" те, чого вже нема, не помилка виклику, а нормальний стан
            // гонки (гра сама закрилась саме між HasExited і Kill()).
            try { if (!h.Proc.HasExited) h.Proc.Kill(entireProcessTree: true); } catch { }
            return null;
        };
        registry["procPid"] = args => (double)((ProcHandle)args[0]).Proc.Id;
        registry["procExitCode"] = args => {
            var h = (ProcHandle)args[0];
            return h.Proc.HasExited ? (double)h.Proc.ExitCode : null;
        };
        // Те, що процес встиг вивести ВІД СТАРТУ (не лише "нове відтоді") —
        // .nx-скрипт сам вирішує, як часто перечитувати й показувати
        // (напр. в GUI-поле логів гри раз на секунду таймером).
        // procWrite(h, текст) - у stdin процесу, запущеного з interactive: true
        // (рядок для readLine - з "\n" у кінці). procCloseInput(h) - кінець вводу.
        registry["procWrite"] = args => {
            var h = (ProcHandle)args[0];
            if (h.Input == null) throw new Exception("procWrite: процес запущено без опції interactive: true");
            try { h.Input.Write(args[1]?.ToString() ?? ""); } catch (IOException) { }
            return null;
        };
        registry["procCloseInput"] = args => {
            var h = (ProcHandle)args[0];
            try { h.Input?.Close(); } catch (IOException) { }
            return null;
        };
        registry["procOutput"] = args => { lock (((ProcHandle)args[0]).Lock) return ((ProcHandle)args[0]).Out.ToString(); };
        registry["procErrorOutput"] = args => { lock (((ProcHandle)args[0]).Lock) return ((ProcHandle)args[0]).Err.ToString(); };
    }

    // procStart(команда, [аргументи]?, мапа_опцій?) -> хендл, НЕ блокує.
    // Опції (NxMap, як у httpRequest headers): cwd (рядок), env (мапа).
    private static ProcHandle Start(object[] args)
    {
        Sandbox.CheckProcess();
        var cmd = args[0]?.ToString() ?? throw new Exception("procStart: порожня команда");
        var nxArgs = args.Length > 1 && args[1] is List<object> list ? list : new List<object>();
        var options = args.Length > 2 ? args[2] as NxMap : null;
        // interactive: true - діалогова програма (IDE запускає код учня):
        // stdin відкритий для procWrite, вивід читається ПОСИМВОЛЬНО, тож
        // підказка без переходу рядка ("Як тебе звати? ") видна одразу.
        bool interactive = options != null && options.Entries.TryGetValue("interactive", out var inter) && inter is bool ib && ib;

        var psi = new ProcessStartInfo {
            FileName = cmd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = interactive,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var a in nxArgs) psi.ArgumentList.Add(a?.ToString() ?? "");

        if (options != null)
        {
            if (options.Entries.TryGetValue("cwd", out var cwd) && cwd != null)
                psi.WorkingDirectory = cwd.ToString()!;
            if (options.Entries.TryGetValue("env", out var envObj) && envObj is NxMap envMap)
                foreach (var kv in envMap.Entries)
                    psi.Environment[kv.Key.ToString() ?? ""] = kv.Value?.ToString() ?? "";
        }
        // rawArgs: рядок аргументів як є (без екранування .NET). Потрібно для
        // cmd.exe /s /c "команда": cmd розбирає лапки по-своєму, і \" від
        // ArgumentList ламав би git commit -m "два слова".
        if (options != null && options.Entries.TryGetValue("rawArgs", out var raw) && raw != null)
        {
            psi.ArgumentList.Clear();
            psi.Arguments = raw.ToString();
        }

        // PID батька - для NX_EXIT_WITH_PARENT=1 у дочірній nx-програмі
        // (Nx IDE: гра на сцені не має лишатись жити, якщо IDE впала).
        psi.Environment["NX_PARENT_PID"] = Environment.ProcessId.ToString();

        var handle = new ProcHandle();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // OutputDataReceived/ErrorDataReceived спрацьовують на ThreadPool-
        // потоці .NET, паралельно з тим, як .nx-скрипт робить щось інше —
        // тому lock навколо StringBuilder, а не звичайний Append.
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (handle.Lock) handle.Out.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (handle.Lock) handle.Err.AppendLine(e.Data); };
        handle.Proc = proc;

        try { proc.Start(); }
        catch (Exception ex) { throw new Exception($"Не вдалося запустити процес '{cmd}': {ex.Message}"); }
        if (interactive)
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            handle.Input = new StreamWriter(proc.StandardInput.BaseStream, utf8) { AutoFlush = true };
            PumpRaw(proc.StandardOutput, handle, handle.Out);
            PumpRaw(proc.StandardError, handle, handle.Err);
        }
        else
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        return handle;
    }

    private static void PumpRaw(StreamReader reader, ProcHandle handle, StringBuilder target)
    {
        var t = new Thread(() =>
        {
            var buf = new char[4096];
            try
            {
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    lock (handle.Lock) target.Append(buf, 0, n);
            }
            catch { }
        }) { IsBackground = true };
        t.Start();
    }

    // procRun — те саме, що procStart, але БЛОКУЄ до завершення й одразу
    // повертає результат. Для коротких команд (напр. "java -version"),
    // не для гри — ту показово запускати через procStart, інакше GUI
    // лаунчера "зависає" на весь час сесії Minecraft.
    private static object Run(ProcHandle handle)
    {
        handle.Proc.WaitForExit();
        lock (handle.Lock)
        {
            // Проста мапа полів, як gc_stats() — БЕЗ "__type" (те поле лише
            // для методів struct-літералів, CALL_METHOD; тут звичайні дані,
            // dot-доступ (result.exitCode) працює й без нього, STRUCT_GET
            // просто читає ключ зі словника).
            return new Dictionary<string, object> {
                ["exitCode"] = (double)handle.Proc.ExitCode,
                ["stdout"] = handle.Out.ToString(),
                ["stderr"] = handle.Err.ToString()
            };
        }
    }
}
