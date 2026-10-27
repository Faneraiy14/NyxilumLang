using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

namespace NyxilumLang.Runtime.Modules;

// Псевдотермінал (PTY): процес думає, що працює у справжньому терміналі -
// bash показує свій запрошувач, ls/git фарбують вивід, vim, top, htop, less
// малюють на весь екран, Ctrl+C зупиняє програму. procStart так не вміє:
// там stdout - труба, і програми поводяться "як у скрипті".
//
// ptyStart(команда, [аргументи]?, опції?) -> хендл. Опції: cwd, env (мапа),
//     cols, rows (розмір, типово 80x24).
// ptyRead(h)          - усе, що процес вивів відтоді, як читали востаннє
//                       (з керівними послідовностями ESC[... - їх розбирає
//                       сам термінал-емулятор, напр. Nx IDE).
// ptyWrite(h, текст)  - як натискання клавіш ("\u0003" - Ctrl+C, "\r" - Enter).
// ptyResize(h, cols, rows), ptyIsRunning(h), ptyExitCode(h), ptyKill(h).
//
// Linux: posix_openpt + posix_spawn з новою сесією (setsid), slave-кінець
// стає керівним терміналом дитини. Windows 10 1809+: ConPTY
// (CreatePseudoConsole). macOS - поки нема (Nx IDE там теж ще нема).
public static class PtyModule
{
    public sealed class PtyHandle
    {
        public readonly StringBuilder Out = new();
        public readonly object Lock = new();
        public Decoder Decoder = new UTF8Encoding(false).GetDecoder();
        public double? ExitCode;
        public bool Closed;
        // Linux
        public int Pid;
        public int Master = -1;
        // Windows
        public IntPtr PseudoConsole, Process, InputWrite, OutputRead;
    }

    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["ptyStart"] = Start;
        registry["ptyRead"] = args => {
            var h = (PtyHandle)args[0];
            lock (h.Lock)
            {
                var s = h.Out.ToString();
                h.Out.Clear();
                return s;
            }
        };
        registry["ptyWrite"] = args => {
            var h = (PtyHandle)args[0];
            var bytes = Encoding.UTF8.GetBytes(args[1]?.ToString() ?? "");
            if (h.Closed || bytes.Length == 0) return null;
            if (OperatingSystem.IsWindows()) Win.Write(h, bytes);
            else Unix.Write(h, bytes);
            return null;
        };
        registry["ptyResize"] = args => {
            var h = (PtyHandle)args[0];
            int cols = Math.Clamp(ToInt(args[1], 80), 2, 1000), rows = Math.Clamp(ToInt(args[2], 24), 2, 1000);
            if (h.Closed) return null;
            if (OperatingSystem.IsWindows()) Win.Resize(h, cols, rows);
            else Unix.Resize(h.Master, cols, rows);
            return null;
        };
        registry["ptyIsRunning"] = args => Poll((PtyHandle)args[0]) == null;
        registry["ptyExitCode"] = args => Poll((PtyHandle)args[0]);
        registry["ptyKill"] = args => {
            var h = (PtyHandle)args[0];
            if (Poll(h) != null) return null;
            if (OperatingSystem.IsWindows()) Win.Kill(h);
            else Unix.Kill(h);
            return null;
        };
    }

    private static int ToInt(object? v, int def) => v is double d ? (int)d : def;

    // null - ще працює, інакше код виходу.
    private static double? Poll(PtyHandle h)
    {
        if (h.ExitCode != null) return h.ExitCode;
        if (OperatingSystem.IsWindows()) Win.Poll(h);
        else Unix.Poll(h);
        return h.ExitCode;
    }

    private static object Start(object[] args)
    {
        Sandbox.CheckProcess();
        var cmd = args[0]?.ToString() ?? throw new Exception("ptyStart: порожня команда");
        var argList = new List<string>();
        if (args.Length > 1 && args[1] is List<object> list) foreach (var a in list) argList.Add(a?.ToString() ?? "");
        var options = args.Length > 2 ? args[2] as NxMap : null;
        string? cwd = null;
        int cols = 80, rows = 24;
        var env = new Dictionary<string, string>();
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
            env[kv.Key.ToString()!] = kv.Value?.ToString() ?? "";
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["NX_PARENT_PID"] = Environment.ProcessId.ToString();
        if (options != null)
        {
            if (options.Entries.TryGetValue("cwd", out var c) && c != null) cwd = c.ToString();
            if (options.Entries.TryGetValue("cols", out var cc)) cols = Math.Clamp(ToInt(cc, 80), 2, 1000);
            if (options.Entries.TryGetValue("rows", out var rr)) rows = Math.Clamp(ToInt(rr, 24), 2, 1000);
            if (options.Entries.TryGetValue("env", out var e) && e is NxMap em)
                foreach (var kv in em.Entries) env[kv.Key.ToString() ?? ""] = kv.Value?.ToString() ?? "";
        }
        if (cwd != null && !Directory.Exists(cwd)) throw new Exception($"ptyStart: теки '{cwd}' нема");
        var h = new PtyHandle();
        if (OperatingSystem.IsWindows()) Win.Start(h, cmd, argList, cwd, env, cols, rows);
        else if (OperatingSystem.IsLinux()) Unix.Start(h, cmd, argList, cwd, env, cols, rows);
        else throw new Exception("ptyStart: псевдотермінал поки є лише на Linux і Windows");
        return h;
    }

    private static void Append(PtyHandle h, byte[] buf, int n, bool flush)
    {
        lock (h.Lock)
        {
            var chars = new char[h.Decoder.GetCharCount(buf, 0, n, flush)];
            int k = h.Decoder.GetChars(buf, 0, n, chars, 0, flush);
            h.Out.Append(chars, 0, k);
        }
    }

    // ---------------- Linux ----------------
    private static class Unix
    {
        private const int O_RDWR = 2, O_NOCTTY = 0x100, O_CLOEXEC = 0x80000;
        private const short POSIX_SPAWN_SETSIGDEF = 0x04, POSIX_SPAWN_SETSIGMASK = 0x08, POSIX_SPAWN_SETSID = 0x80;
        private const ulong TIOCSWINSZ = 0x5414;
        private const int WNOHANG = 1, SIGHUP = 1, SIGKILL = 9, EINTR = 4, EAGAIN = 11;

        [StructLayout(LayoutKind.Sequential)]
        private struct WinSize { public ushort Rows, Cols, X, Y; }

        [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
        [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int ptsname_r(int fd, byte[] buf, nint len);
        [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong req, ref WinSize ws);
        [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buf, nint n);
        [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buf, nint n);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
        [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
        [DllImport("libc")] private static extern int posix_spawn_file_actions_init(IntPtr fa);
        [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(IntPtr fa);
        [DllImport("libc")] private static extern int posix_spawn_file_actions_addopen(IntPtr fa, int fd, byte[] path, int oflag, int mode);
        [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(IntPtr fa, int fd, int newfd);
        [DllImport("libc")] private static extern int posix_spawn_file_actions_addchdir_np(IntPtr fa, byte[] path);
        [DllImport("libc")] private static extern int posix_spawnattr_init(IntPtr attr);
        [DllImport("libc")] private static extern int posix_spawnattr_destroy(IntPtr attr);
        [DllImport("libc")] private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
        [DllImport("libc")] private static extern int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr set);
        [DllImport("libc")] private static extern int posix_spawnattr_setsigmask(IntPtr attr, IntPtr set);
        [DllImport("libc")] private static extern int sigfillset(IntPtr set);
        [DllImport("libc")] private static extern int sigemptyset(IntPtr set);
        [DllImport("libc")] private static extern int posix_spawnp(out int pid, byte[] file, IntPtr fa, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

        private static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

        private static IntPtr[] CStrings(IEnumerable<string> items)
        {
            var l = items.Select(s => Marshal.StringToCoTaskMemUTF8(s)).ToList();
            l.Add(IntPtr.Zero);
            return l.ToArray();
        }

        public static void Start(PtyHandle h, string cmd, List<string> args, string? cwd, Dictionary<string, string> env, int cols, int rows)
        {
            int master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
            if (master < 0) throw new Exception("ptyStart: posix_openpt не вдався (" + Marshal.GetLastWin32Error() + ")");
            var nameBuf = new byte[256];
            if (grantpt(master) != 0 || unlockpt(master) != 0 || ptsname_r(master, nameBuf, nameBuf.Length) != 0)
            {
                close(master);
                throw new Exception("ptyStart: не вдалося підготувати псевдотермінал");
            }
            var slave = Encoding.UTF8.GetString(nameBuf, 0, Array.IndexOf(nameBuf, (byte)0));
            Resize(master, cols, rows);

            // непрозорі структури glibc - із запасом за розміром
            IntPtr fa = Marshal.AllocHGlobal(1024), attr = Marshal.AllocHGlobal(1024);
            IntPtr sigAll = Marshal.AllocHGlobal(256), sigNone = Marshal.AllocHGlobal(256);
            var argv = CStrings(new[] { cmd }.Concat(args));
            var envp = CStrings(env.Select(kv => kv.Key + "=" + kv.Value));
            try
            {
                posix_spawn_file_actions_init(fa);
                posix_spawnattr_init(attr);
                if (cwd != null) posix_spawn_file_actions_addchdir_np(fa, Z(cwd));
                // після setsid (glibc робить його до файлових дій) відкритий
                // slave стає керівним терміналом нової сесії
                posix_spawn_file_actions_addopen(fa, 0, Z(slave), O_RDWR, 0);
                posix_spawn_file_actions_adddup2(fa, 0, 1);
                posix_spawn_file_actions_adddup2(fa, 0, 2);
                // .NET ігнорує SIGPIPE і блокує деякі сигнали - дитині все
                // стандартне, інакше "yes | head" у шелі не зупинявся б
                sigfillset(sigAll);
                sigemptyset(sigNone);
                posix_spawnattr_setsigdefault(attr, sigAll);
                posix_spawnattr_setsigmask(attr, sigNone);
                posix_spawnattr_setflags(attr, (short)(POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK));
                int err = posix_spawnp(out int pid, Z(cmd), fa, attr, argv, envp);
                if (err != 0)
                {
                    close(master);
                    throw new Exception($"Не вдалося запустити '{cmd}' (помилка {err})");
                }
                h.Pid = pid;
                h.Master = master;
            }
            finally
            {
                posix_spawn_file_actions_destroy(fa);
                posix_spawnattr_destroy(attr);
                Marshal.FreeHGlobal(fa); Marshal.FreeHGlobal(attr);
                Marshal.FreeHGlobal(sigAll); Marshal.FreeHGlobal(sigNone);
                foreach (var p in argv) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
                foreach (var p in envp) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }

            new Thread(() =>
            {
                var buf = new byte[16384];
                while (true)
                {
                    nint n = read(master, buf, buf.Length);
                    if (n > 0) { Append(h, buf, (int)n, false); continue; }
                    if (n < 0 && Marshal.GetLastWin32Error() is EINTR or EAGAIN) continue;
                    break; // EIO - усі процеси закрили slave
                }
                Append(h, buf, 0, true);
            }) { IsBackground = true, Name = "pty-read" }.Start();
        }

        public static void Write(PtyHandle h, byte[] bytes)
        {
            int off = 0;
            while (off < bytes.Length)
            {
                var chunk = off == 0 ? bytes : bytes[off..];
                nint n = write(h.Master, chunk, chunk.Length);
                if (n < 0)
                {
                    if (Marshal.GetLastWin32Error() is EINTR or EAGAIN) continue;
                    return;
                }
                off += (int)n;
            }
        }

        public static void Resize(int master, int cols, int rows)
        {
            var ws = new WinSize { Rows = (ushort)rows, Cols = (ushort)cols };
            ioctl(master, TIOCSWINSZ, ref ws);
        }

        public static void Poll(PtyHandle h)
        {
            if (h.Pid <= 0) return;
            int r = waitpid(h.Pid, out int status, WNOHANG);
            if (r != h.Pid) return;
            int sig = status & 0x7f;
            h.ExitCode = sig == 0 ? (status >> 8) & 0xff : 128 + sig;
            // master не закриваємо тут: потік читання ще дочитує хвіст виводу
            // (і фонові процеси шелу можуть тримати slave)
        }

        public static void Kill(PtyHandle h)
        {
            // як закриття вікна терміналу: SIGHUP усій групі сесії
            kill(-h.Pid, SIGHUP);
            for (int i = 0; i < 20 && h.ExitCode == null; i++)
            {
                Thread.Sleep(10);
                Poll(h);
            }
            if (h.ExitCode == null)
            {
                kill(-h.Pid, SIGKILL);
                kill(h.Pid, SIGKILL);
                for (int i = 0; i < 50 && h.ExitCode == null; i++)
                {
                    Thread.Sleep(10);
                    Poll(h);
                }
            }
            h.Closed = true;
            close(h.Master);
        }
    }

    // ---------------- Windows (ConPTY) ----------------
    private static class Win
    {
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int STARTF_USESTDHANDLES = 0x00000100;
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
        private const uint STILL_ACTIVE = 259;

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord { public short X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attrs, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pc);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr pc, Coord size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern void ClosePseudoConsole(IntPtr pc);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string? app, StringBuilder cmdLine, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref StartupInfoEx si, out ProcessInformation pi);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);

        // Правила лапок CommandLineToArgvW.
        private static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (var ch in a)
            {
                if (ch == '\\') { bs++; continue; }
                if (ch == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(ch);
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }

        public static void Start(PtyHandle h, string cmd, List<string> args, string? cwd, Dictionary<string, string> env, int cols, int rows)
        {
            if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) || !CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
                throw new Exception("ptyStart: CreatePipe не вдався");
            int hr = CreatePseudoConsole(new Coord { X = (short)cols, Y = (short)rows }, inRead, outWrite, 0, out var pc);
            // ConPTY тримає свої копії цих кінців
            CloseHandle(inRead);
            CloseHandle(outWrite);
            if (hr != 0)
            {
                CloseHandle(inWrite); CloseHandle(outRead);
                throw new Exception("ptyStart: псевдоконсоль потребує Windows 10 1809 або новішої (помилка " + hr + ")");
            }

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            IntPtr attrList = Marshal.AllocHGlobal(size);
            var envBlock = Marshal.StringToHGlobalUni(string.Concat(env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key + "=" + kv.Value + "\0")) + "\0");
            try
            {
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size) ||
                    !UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Exception("ptyStart: не вдалося налаштувати псевдоконсоль");
                var si = new StartupInfoEx();
                si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
                // без цього дитина успадкувала б перенаправлені stdin/stdout
                // самого nx (IDE запущено без консолі) замість псевдоконсолі
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.lpAttributeList = attrList;
                var cmdLine = new StringBuilder(string.Join(" ", new[] { cmd }.Concat(args).Select(Quote)));
                if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        envBlock, cwd, ref si, out var pi))
                {
                    int err = Marshal.GetLastWin32Error();
                    ClosePseudoConsole(pc);
                    CloseHandle(inWrite); CloseHandle(outRead);
                    throw new Exception($"Не вдалося запустити '{cmd}' (помилка {err})");
                }
                CloseHandle(pi.hThread);
                h.Process = pi.hProcess;
                h.Pid = pi.dwProcessId;
                h.PseudoConsole = pc;
                h.InputWrite = inWrite;
                h.OutputRead = outRead;
            }
            finally
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
                Marshal.FreeHGlobal(envBlock);
            }

            new Thread(() =>
            {
                var buf = new byte[16384];
                while (ReadFile(outRead, buf, buf.Length, out int n, IntPtr.Zero) && n > 0) Append(h, buf, n, false);
                Append(h, buf, 0, true);
            }) { IsBackground = true, Name = "pty-read" }.Start();
        }

        public static void Write(PtyHandle h, byte[] bytes) => WriteFile(h.InputWrite, bytes, bytes.Length, out _, IntPtr.Zero);

        public static void Resize(PtyHandle h, int cols, int rows) =>
            ResizePseudoConsole(h.PseudoConsole, new Coord { X = (short)cols, Y = (short)rows });

        public static void Poll(PtyHandle h)
        {
            if (h.Process == IntPtr.Zero || !GetExitCodeProcess(h.Process, out uint code) || code == STILL_ACTIVE) return;
            h.ExitCode = code;
            Close(h);
        }

        public static void Kill(PtyHandle h)
        {
            TerminateProcess(h.Process, 1);
            for (int i = 0; i < 50 && h.ExitCode == null; i++)
            {
                Thread.Sleep(10);
                Poll(h);
            }
            Close(h);
        }

        // Закрита псевдоконсоль закриває й вихідну трубу - потік читання
        // отримує кінець. ClosePseudoConsole чекає, поки вивід дочитають,
        // тому - у фоні.
        private static void Close(PtyHandle h)
        {
            if (h.Closed) return;
            h.Closed = true;
            var pc = h.PseudoConsole;
            var inW = h.InputWrite;
            new Thread(() => { ClosePseudoConsole(pc); CloseHandle(inW); }) { IsBackground = true }.Start();
        }
    }
}
