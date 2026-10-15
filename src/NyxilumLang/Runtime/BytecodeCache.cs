using System.Text;
using NyxilumLang.Compiler;

namespace NyxilumLang.Runtime;

// Кеш скомпільованого байткоду: другий і наступні запуски того самого .nx
// (і Nx IDE - ~11 тис. рядків у ~20 модулях) не проходять лексер, парсер,
// import і компілятор, якщо жоден файл не змінився.
//
// Файл кешу - ~/.local/share/nyx/cache/<sha256(шлях + версія nx)>.nxc
// (Windows: %LOCALAPPDATA%\nyx\cache). Усередині: версія формату й nx,
// список усіх прочитаних файлів (головний + усі import) з розміром і часом
// зміни, далі байткод. Хоч один файл змінився / інша версія nx / будь-яка
// помилка читання - звичайна компіляція (кеш ніколи не ламає запуск).
// NX_NO_CACHE=1 - вимкнути.
public static class BytecodeCache
{
    private const int FormatVersion = 1;

    public static bool Enabled => Environment.GetEnvironmentVariable("NX_NO_CACHE") != "1";

    private static string NxVersion => typeof(BytecodeCache).Assembly.GetName().Version?.ToString() ?? "?";

    private static string CacheDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
        return Path.Combine(baseDir, "nyx", "cache");
    }

    // Ім'я файла кешу - FNV-1a 64 від шляху й версії. Не SHA256: та при
    // першому виклику вантажить криптобібліотеку системи (~35 мс на старті),
    // а тут потрібна лише стабільна коротка назва, не криптографія.
    private static string CachePath(string entry)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(Path.GetFullPath(entry) + "\n" + NxVersion))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return Path.Combine(CacheDir(), h.ToString("x16") + ".nxc");
    }

    // Байткод з кешу або null (нема / застарів / зіпсований).
    public static Bytecode? TryLoad(string entry)
    {
        try
        {
            var path = CachePath(entry);
            if (!File.Exists(path)) return null;
            // увесь файл одним читанням (а не дрібними порціями з диска)
            using var r = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)), Encoding.UTF8);
            if (r.ReadString() != "NXBC" || r.ReadInt32() != FormatVersion || r.ReadString() != NxVersion) return null;
            int deps = r.ReadInt32();
            for (int i = 0; i < deps; i++)
            {
                var file = r.ReadString();
                long size = r.ReadInt64();
                long ticks = r.ReadInt64();
                var fi = new FileInfo(file);
                if (!fi.Exists || fi.Length != size || fi.LastWriteTimeUtc.Ticks != ticks) return null;
            }
            return ReadBytecode(r);
        }
        catch
        {
            return null;
        }
    }

    // Зберегти після компіляції. deps - усі прочитані файли (з головним).
    public static void Save(string entry, IEnumerable<string> deps, Bytecode bc)
    {
        try
        {
            var dir = CacheDir();
            Directory.CreateDirectory(dir);
            var path = CachePath(entry);
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                w.Write("NXBC");
                w.Write(FormatVersion);
                w.Write(NxVersion);
                var list = deps.Select(Path.GetFullPath).Distinct().ToList();
                w.Write(list.Count);
                foreach (var f in list)
                {
                    var fi = new FileInfo(f);
                    w.Write(f);
                    w.Write(fi.Length);
                    w.Write(fi.LastWriteTimeUtc.Ticks);
                }
                WriteBytecode(w, bc);
            }
            File.Move(tmp, path, true);
        }
        catch
        {
            // нема прав / диск повний / тип константи, якого кеш не знає -
            // нічого страшного, наступного разу просто скомпілюється знову
        }
    }

    private static void WriteBytecode(BinaryWriter w, Bytecode bc)
    {
        var code = bc.ToArray();
        w.Write(code.Length);
        w.Write(code);
        w.Write(bc.Constants.Count);
        foreach (var c in bc.Constants) WriteConst(w, c);
        w.Write(bc.FunctionAddresses.Count);
        foreach (var kv in bc.FunctionAddresses) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(bc.LineMap.Count);
        foreach (var (off, line) in bc.LineMap) { w.Write(off); w.Write(line); }
        w.Write(bc.VarDecls.Count);
        foreach (var (off, slot, name) in bc.VarDecls) { w.Write(off); w.Write(slot); w.Write(name); }
    }

    private static Bytecode ReadBytecode(BinaryReader r)
    {
        var bc = new Bytecode();
        int n = r.ReadInt32();
        bc.Code.AddRange(r.ReadBytes(n));
        if (bc.Code.Count != n) throw new EndOfStreamException();
        int consts = r.ReadInt32();
        for (int i = 0; i < consts; i++) bc.Constants.Add(ReadConst(r)!);
        int funcs = r.ReadInt32();
        for (int i = 0; i < funcs; i++) bc.FunctionAddresses[r.ReadString()] = r.ReadInt32();
        int lines = r.ReadInt32();
        for (int i = 0; i < lines; i++) bc.LineMap.Add((r.ReadInt32(), r.ReadInt32()));
        int vars = r.ReadInt32();
        for (int i = 0; i < vars; i++) bc.VarDecls.Add((r.ReadInt32(), r.ReadInt32(), r.ReadString()));
        return bc;
    }

    // Константи компілятора: null, рядок, число, bool, посилання на функцію,
    // список (слоти захоплених змінних). Інший тип - виняток (кеш не пишеться).
    private static void WriteConst(BinaryWriter w, object? c)
    {
        switch (c)
        {
            case null: w.Write((byte)0); break;
            case string s: w.Write((byte)1); w.Write(s); break;
            case double d: w.Write((byte)2); w.Write(d); break;
            case bool b: w.Write((byte)3); w.Write(b); break;
            case NxFunctionRef f when f.Captured == null:
                w.Write((byte)4);
                w.Write(f.Name ?? ""); w.Write(f.Name != null);
                w.Write(f.NativeName ?? ""); w.Write(f.NativeName != null);
                w.Write(f.Address);
                break;
            case List<object> list:
                w.Write((byte)5);
                w.Write(list.Count);
                foreach (var x in list) WriteConst(w, x);
                break;
            default:
                throw new NotSupportedException("константа " + c.GetType().Name);
        }
    }

    private static object? ReadConst(BinaryReader r)
    {
        switch (r.ReadByte())
        {
            case 0: return null;
            case 1: return r.ReadString();
            case 2: return r.ReadDouble();
            case 3: return r.ReadBoolean();
            case 4:
                {
                    var name = r.ReadString(); bool hasName = r.ReadBoolean();
                    var native = r.ReadString(); bool hasNative = r.ReadBoolean();
                    return new NxFunctionRef { Name = hasName ? name : null, NativeName = hasNative ? native : null, Address = r.ReadInt32() };
                }
            case 5:
                {
                    int n = r.ReadInt32();
                    var list = new List<object>(n);
                    for (int i = 0; i < n; i++) list.Add(ReadConst(r)!);
                    return list;
                }
            default: throw new InvalidDataException();
        }
    }
}
