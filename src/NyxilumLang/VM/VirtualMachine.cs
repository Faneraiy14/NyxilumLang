using NyxilumLang.Compiler;
using NyxilumLang.Runtime;
using NyxilumDbLib = NyxilumDb.NyxilumDb;
#if WINDOWS
using System.Windows.Forms;
using System.Drawing;
#endif

namespace NyxilumLang.VM;

// Кидається інструкцією THROW, коли немає жодного активного try/catch —
// піднімається аж до Nx.cs і показується як звичайна Runtime Error.
public class NxThrowException : Exception
{
    public NxThrowException(string message) : base(message) { }
}

// Позначає виняток, чий текст (номер рядка, traceback) уже повністю
// сформований — верхній catch у Run()/RunFunction() НЕ повинен обгортати
// його ще раз своїм префіксом. Без цього маркера помилка воркера
// (RunFunction на ОКРЕМІЙ VM у spawn()), прокинута назовні через
// workerJoin(), обгорталась би ВДРУГЕ зовнішнім Run() головного потоку —
// подвійний "рядок N:" і задвоєний traceback навколо вже готового тексту.
internal sealed class NxFormattedException : Exception
{
    public NxFormattedException(string message, Exception inner) : base(message, inner) { }
}

public class VirtualMachine
{
    private readonly byte[] _code;
    private readonly List<object> _constants;
    private readonly Stack<object> _stack = new();
    // ВИПРАВЛЕНО: раніше всі змінні (параметри й локальні) лежали в ОДНОМУ
    // спільному словнику на всю програму, а номер слота призначався один раз
    // на етапі компіляції. Рекурсивний виклик функції використовував ті самі
    // слоти, що й батьківський виклик, — локальна змінна могла тихо
    // затертись вкладеним викликом. Тепер кожен виклик отримує власний,
    // окремий фрейм змінних; _frameStack зберігає фрейми викликачів.
    private Dictionary<int, object> _currentFrame = new();
    private readonly Stack<Dictionary<int, object>> _frameStack = new();
    private readonly Stack<int> _callStack = new();

    // Некерована рекурсія (напр. func f(n) { return f(n+1) } без умови
    // виходу) нічим не обмежувалась: _callStack/_frameStack ростуть на
    // керованій купі, а не на нативному стеку ОС, тож native StackOverflow
    // тут не спрацьовує - процес просто мовчки жер пам'ять (виміряно:
    // ~140 МБ/с) аж до вичерпання й OOM-краху чи kill-у ззовні, замість
    // зрозумілої, зловлюваної через try/catch помилки. gc_limit/
    // NX_GC_MAX_OBJECTS тут теж не рятує - він рахує NyxilumLang-виділення
    // (масиви/структури/мапи), не глибину викликів. 10_000 - з запасом
    // для реальних рекурсивних алгоритмів (обхід дерева, наївний Фібоначчі
    // тощо), але ловить втечу задовго до відчутного споживання пам'яті.
    private const int MaxCallDepth = 10_000;

    // Спільна перевірка для CALL/CALL_METHOD/CALL_VALUE - усі три незалежно
    // штовхають _callStack/_frameStack, тож перевірка в лише одному з них
    // (напр. CALL) не захищала б рекурсію через метод (self.method()) чи
    // через замикання/значення-функцію (var f = func(n) {...}; f(n+1)).
    private void CheckCallDepth()
    {
        if (_callStack.Count >= MaxCallDepth)
            throw new Exception($"Перевищено максимальну глибину викликів ({MaxCallDepth}) — схоже на нескінченну рекурсію без умови виходу.");
    }

    // Глобальні змінні (var на верхньому рівні файлу) — окреме сховище за
    // ІМЕНЕМ, а не за номером слота. Слоти _currentFrame нумеруються окремо
    // для кожної функції (Compiler.cs скидає _varCounter=0 при вході в
    // FunctionDeclaration), тому спільний слот для глобальних не спрацював
    // би: два різні виклики просто не бачили б одне значення.
    private readonly Dictionary<string, object> _globals;

    // Дозволяє викликачу (напр. REPL в Nx.cs) забрати значення
    // глобальних змінних після Run() і передати їх у КОНСТРУКТОР наступної
    // VM — так глобальна var, оголошена в одному рядку REPL, лишається
    // видимою й зі своїм значенням у наступних рядках.
    public IReadOnlyDictionary<string, object> Globals => _globals;

    // try/catch: кожен активний try запам'ятовує, куди стрибнути і до якої
    // глибини стеку/фреймів/викликів відкотитись, якщо всередині нього
    // станеться помилка (як явний throw, так і внутрішня помилка VM).
    private class ExceptionHandler
    {
        public int CatchAddr;
        public int VarSlot;
        public int StackDepth;
        public int CallStackDepth;
        public int FrameStackDepth;
    }
    private readonly Stack<ExceptionHandler> _handlers = new();

    // "Найпростіший варіант" JIT: справжня компіляція в машинний код тут не
    // має сенсу — значення NyxilumLang все одно боксовані object, тож
    // Reflection.Emit виграв би хіба що в диспетчеризації switch, а не в
    // боксингу/словникових пошуках, які й так домінують. Натомість
    // розпізнаємо 2 конкретні "гарячі" послідовності опкодів (лічильник
    // циклу і порівняння лічильника з межею) і виконуємо їх напряму через
    // C#-арифметику, минаючи стек і бокси, — з guard-перевіркою типів на
    // КОЖНІЙ ітерації, тож будь-яка зміна типу всередині циклу миттєво й
    // безпечно повертає виконання назад у звичайний інтерпретатор.
    private enum SuperOpKind { IncLocal, CmpJump }

    private readonly struct SuperOp
    {
        public required SuperOpKind Kind { get; init; }
        public required int Length { get; init; }
        public required int SlotA { get; init; }
        public int ConstIdx { get; init; }
        public bool RightIsConst { get; init; }
        public int SlotB { get; init; }
        public OpCode CompareOp { get; init; }
        public int JumpTarget { get; init; }
    }

    private const int HotThreshold = 512;
    private readonly Dictionary<int, int> _backEdgeHits = new();
    private SuperOp?[]? _super;
    private readonly bool _jitEnabled = Environment.GetEnvironmentVariable("NX_JIT") != "0";

    private void UnwindToHandler(object? errorValue)
    {
        var handler = _handlers.Pop();
        while (_stack.Count > handler.StackDepth) _stack.Pop();
        while (_callStack.Count > handler.CallStackDepth) _callStack.Pop();
        while (_frameStack.Count > handler.FrameStackDepth) _currentFrame = _frameStack.Pop();
        _currentFrame[handler.VarSlot] = errorValue ?? "";
        _ip = handler.CatchAddr;
    }
    private readonly Dictionary<string, int> _functionAddresses;
    private int _ip;
    public static readonly Dictionary<string, Func<object[], object?>> _nativeFunctions = new();

    static VirtualMachine()
    {
        // Math
        _nativeFunctions["abs"] = args => Math.Abs(PopNumVal(args[0]));
        _nativeFunctions["sqrt"] = args => Math.Sqrt(PopNumVal(args[0]));
        _nativeFunctions["sin"] = args => Math.Sin(PopNumVal(args[0]));
        _nativeFunctions["cos"] = args => Math.Cos(PopNumVal(args[0]));
        _nativeFunctions["tan"] = args => Math.Tan(PopNumVal(args[0]));
        _nativeFunctions["round"] = args => Math.Round(PopNumVal(args[0]));
        _nativeFunctions["floor"] = args => Math.Floor(PopNumVal(args[0]));
        _nativeFunctions["ceil"] = args => Math.Ceiling(PopNumVal(args[0]));
        _nativeFunctions["pow"] = args => Math.Pow(PopNumVal(args[0]), PopNumVal(args[1]));
        // max/min: як len/indexOf/reverse — розрізняють за типом args[0],
        // не за кількістю аргументів. max(3,5) як і раніше; max([3,5,1])
        // рахує максимум по всьому масиву замість вимоги розкладати його
        // вручну через reduce().
        _nativeFunctions["max"] = args => args[0] is List<object> arrMax
            ? arrMax.Select(PopNumVal).Max()
            : Math.Max(PopNumVal(args[0]), PopNumVal(args[1]));
        _nativeFunctions["min"] = args => args[0] is List<object> arrMin
            ? arrMin.Select(PopNumVal).Min()
            : Math.Min(PopNumVal(args[0]), PopNumVal(args[1]));
        _nativeFunctions["clamp"] = args => Math.Max(PopNumVal(args[1]), Math.Min(PopNumVal(args[2]), PopNumVal(args[0])));
        _nativeFunctions["toFixed"] = args => PopNumVal(args[0]).ToString("F" + TruncToInt(args[1]), System.Globalization.CultureInfo.InvariantCulture);
        
        // Random
        // Random.Shared (не власний Random-інстанс): він thread-safe для
        // конкурентних викликів з кількох потоків одразу — звичайний "new
        // Random()", яким користуються кілька воркерів (spawn()) паралельно,
        // не гарантує коректність свого внутрішнього стану під конкуренцією.
        _nativeFunctions["randomInt"] = args => (double)Random.Shared.Next(TruncToInt(args[0]), TruncToInt(args[1]) + 1);
        _nativeFunctions["randomDouble"] = args => PopNumVal(args[0]) + (Random.Shared.NextDouble() * (PopNumVal(args[1]) - PopNumVal(args[0])));

        // Conversions & Types
        _nativeFunctions["toString"] = args => args[0]?.ToString() ?? "null";
        _nativeFunctions["toInt"] = args => TruncToInt(args[0]);
        _nativeFunctions["toDouble"] = args => Convert.ToDouble(args[0]);
        _nativeFunctions["typeOf"] = args => args[0] switch {
            List<object> => "array",
            NxMap => "map",
            NxFunctionRef => "function",
            NyxilumDbLib => "database",
            Dictionary<string, object> => "struct",
            string => "string",
            bool => "bool",
            double => "number",
            int => "number",
            null => "null",
            _ => args[0].GetType().Name
        };
        _nativeFunctions["isNumber"] = args => args[0] is int || args[0] is double || args[0] is float || args[0] is long;
        _nativeFunctions["isString"] = args => args[0] is string;
        _nativeFunctions["isArray"] = args => args[0] is List<object>;
        _nativeFunctions["isBool"] = args => args[0] is bool;
        _nativeFunctions["isNull"] = args => args[0] == null;

        // Символи
        _nativeFunctions["charCode"] = args => {
            string s = args[0]?.ToString() ?? "";
            if (s.Length == 0) throw new Exception("charCode: очікується непорожній рядок");
            return (double)s[0];
        };
        _nativeFunctions["fromCharCode"] = args => ((char)Convert.ToInt32(args[0])).ToString();

        // Мапи/словники (окремий тип від struct)
        _nativeFunctions["newMap"] = args => { NxGc.Instance.RecordAllocation(); return new NxMap(); };

        // GC-інструментарій: облік NyxilumLang-виділень (ARRAY_NEW/STRUCT_NEW/
        // newMap) і опційний ліміт, щоб некерований цикл виділень у
        // .nx-скрипті не поклав хост-процес. Це НЕ заміна CLR GC — той і
        // так коректно збирає циклічні посилання в наших боксованих object.
        _nativeFunctions["gc_stats"] = args => NxGc.Instance.Stats();
        _nativeFunctions["gc_collect"] = args => { NxGc.Instance.Collect(); return null; };
        _nativeFunctions["gc_limit"] = args => { NxGc.Instance.SetLimit((long)PopNumVal(args[0])); return null; };

        // NyxilumDb: embedded KV-база з WAL (окремий сестринський репозиторій,
        // https://github.com/Faneraiy14/NyxilumDb). Інстанс NyxilumDbLib
        // повертається/приймається як звичайне NyxilumLang-значення — так
        // само, як NxMap. Значення в v1 — лише рядки (UTF8-кодування
        // на Set, декодування на Get): у NyxilumLang немає власного типу
        // "байтовий масив", а більшість реальних застосунків мови й так
        // оперує рядками/JSON, тож розширювати модель значень заради
        // сирих байтів у першій версії не варто.
        _nativeFunctions["dbOpen"] = args => NyxilumDbLib.Open(args[0]?.ToString() ?? "");
        _nativeFunctions["dbClose"] = args => { ((NyxilumDbLib)args[0]).Dispose(); return null; };
        _nativeFunctions["dbSet"] = args => {
            ((NyxilumDbLib)args[0]).Set(args[1]?.ToString() ?? "", System.Text.Encoding.UTF8.GetBytes(args[2]?.ToString() ?? ""));
            return null;
        };
        _nativeFunctions["dbGet"] = args => {
            var bytes = ((NyxilumDbLib)args[0]).Get(args[1]?.ToString() ?? "");
            return bytes == null ? null : System.Text.Encoding.UTF8.GetString(bytes);
        };
        _nativeFunctions["dbHas"] = args => ((NyxilumDbLib)args[0]).ContainsKey(args[1]?.ToString() ?? "");
        _nativeFunctions["dbDelete"] = args => ((NyxilumDbLib)args[0]).Delete(args[1]?.ToString() ?? "");
        _nativeFunctions["dbKeys"] = args => {
            var prefix = args.Length > 1 ? args[1]?.ToString() : null;
            return ((NyxilumDbLib)args[0]).Keys(prefix).Select(k => (object)k).ToList();
        };
        _nativeFunctions["dbCount"] = args => (double)((NyxilumDbLib)args[0]).Count;
        _nativeFunctions["dbCheckpoint"] = args => { ((NyxilumDbLib)args[0]).Checkpoint(); return null; };
        _nativeFunctions["mapSet"] = args => {
            var map = (NxMap)args[0];
            map.Entries[args[1]] = args[2];
            return null;
        };
        _nativeFunctions["mapGet"] = args => {
            var map = (NxMap)args[0];
            return map.Entries.TryGetValue(args[1], out var v) ? v : null;
        };
        _nativeFunctions["mapHas"] = args => {
            var map = (NxMap)args[0];
            return map.Entries.ContainsKey(args[1]);
        };
        _nativeFunctions["mapRemove"] = args => {
            var map = (NxMap)args[0];
            return map.Entries.Remove(args[1]);
        };
        _nativeFunctions["mapKeys"] = args => {
            var map = (NxMap)args[0];
            return map.Entries.Keys.ToList();
        };
        _nativeFunctions["mapValues"] = args => {
            var map = (NxMap)args[0];
            return map.Entries.Values.ToList();
        };

        // Функції вищого порядку над масивами (потребують функцію-значення з Фази 4)
        _nativeFunctions["sort"] = args => {
            var list = new List<object>((List<object>)args[0]);
            var funcRef = (NxFunctionRef)args[1];
            var vm = Current!;
            list.Sort((a, b) => Math.Sign(Convert.ToDouble(vm.InvokeFunctionValue(funcRef, new object[] { a, b }))));
            return list;
        };
        _nativeFunctions["mapArr"] = args => {
            var list = (List<object>)args[0];
            var funcRef = (NxFunctionRef)args[1];
            var vm = Current!;
            var result = new List<object>();
            foreach (var item in list) result.Add(vm.InvokeFunctionValue(funcRef, new object[] { item })!);
            return result;
        };
        _nativeFunctions["filter"] = args => {
            var list = (List<object>)args[0];
            var funcRef = (NxFunctionRef)args[1];
            var vm = Current!;
            var result = new List<object>();
            foreach (var item in list)
                if (Convert.ToBoolean(vm.InvokeFunctionValue(funcRef, new object[] { item })))
                    result.Add(item);
            return result;
        };
        _nativeFunctions["reduce"] = args => {
            var list = (List<object>)args[0];
            var funcRef = (NxFunctionRef)args[1];
            object acc = args[2];
            var vm = Current!;
            foreach (var item in list)
                acc = vm.InvokeFunctionValue(funcRef, new object[] { acc, item })!;
            return acc;
        };

        // Виклик функції-значення з масивом аргументів (аналог "apply") -
        // потрібно, коли кількість аргументів невідома на етапі компіляції
        // (напр. узагальнена передача виклику до нативної функції).
        _nativeFunctions["callWithArgs"] = args => {
            var funcRef = (NxFunctionRef)args[0];
            var argList = (List<object>)args[1];
            return Current!.InvokeFunctionValue(funcRef, argList.ToArray());
        };

        // JSON
        _nativeFunctions["toJson"] = args => NxJson.Serialize(args[0]);
        _nativeFunctions["fromJson"] = args => NxJson.Deserialize(args[0]?.ToString() ?? "");

        // Strings & length
        _nativeFunctions["len"] = args => {
            if (args[0] is List<object> arr) return (double)arr.Count;
            if (args[0] is string s) return (double)s.Length;
            if (args[0] is Dictionary<string, object> d) return (double)d.Count;
            if (args[0] is NxMap m) return (double)m.Entries.Count;
            return 0.0;
        };
        _nativeFunctions["substring"] = args => {
            string s = args[0]?.ToString() ?? "";
            int start = TruncToInt(args[1]);
            int len = args.Length > 2 ? TruncToInt(args[2]) : s.Length - start;
            if (start < 0) start = 0;
            if (len < 0) len = s.Length - start;
            if (start + len > s.Length) len = s.Length - start;
            return s.Substring(start, len);
        };
        _nativeFunctions["replace"] = args => (args[0]?.ToString() ?? "").Replace(args[1]?.ToString() ?? "", args[2]?.ToString() ?? "");
        _nativeFunctions["toUpper"] = args => (args[0]?.ToString() ?? "").ToUpper();
        _nativeFunctions["toLower"] = args => (args[0]?.ToString() ?? "").ToLower();
        _nativeFunctions["contains"] = args => (args[0]?.ToString() ?? "").Contains(args[1]?.ToString() ?? "");
        _nativeFunctions["startsWith"] = args => (args[0]?.ToString() ?? "").StartsWith(args[1]?.ToString() ?? "");
        _nativeFunctions["endsWith"] = args => (args[0]?.ToString() ?? "").EndsWith(args[1]?.ToString() ?? "");
        _nativeFunctions["trim"] = args => (args[0]?.ToString() ?? "").Trim();
        _nativeFunctions["repeat"] = args => string.Concat(Enumerable.Repeat(args[0]?.ToString() ?? "", TruncToInt(args[1])));
        _nativeFunctions["indexOf"] = args => {
            if (args[0] is List<object> arr) return (double)arr.FindIndex(item => ValuesEqual(item, args[1]));
            return (double)(args[0]?.ToString() ?? "").IndexOf(args[1]?.ToString() ?? "");
        };
        _nativeFunctions["reverse"] = args => {
            if (args[0] is List<object> arr) { var copy = new List<object>(arr); copy.Reverse(); return copy; }
            var chars = (args[0]?.ToString() ?? "").ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        };
        _nativeFunctions["split"] = args => {
            string s = args[0]?.ToString() ?? "";
            string sep = args[1]?.ToString() ?? "";
            var parts = s.Split(new[] { sep }, StringSplitOptions.None);
            return parts.Select(p => (object)p).ToList();
        };
        _nativeFunctions["join"] = args => {
            var list = (List<object>)args[0];
            string sep = args[1]?.ToString() ?? "";
            return string.Join(sep, list);
        };

        // Arrays
        _nativeFunctions["append"] = args => {
            var list = (List<object>)args[0];
            list.Add(args[1]);
            return null;
        };
        _nativeFunctions["pop"] = args => {
            var list = (List<object>)args[0];
            if (list.Count == 0) return null;
            var val = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            return val;
        };
        _nativeFunctions["removeAt"] = args => {
            var list = (List<object>)args[0];
            int idx = TruncToInt(args[1]);
            list.RemoveAt(idx);
            return null;
        };
        _nativeFunctions["insert"] = args => {
            var list = (List<object>)args[0];
            int idx = TruncToInt(args[1]);
            list.Insert(idx, args[2]);
            return null;
        };
        _nativeFunctions["clear"] = args => {
            var list = (List<object>)args[0];
            list.Clear();
            return null;
        };
        _nativeFunctions["slice"] = args => {
            var list = (List<object>)args[0];
            int start = Math.Clamp(TruncToInt(args[1]), 0, list.Count);
            int end = args.Length > 2 ? Math.Clamp(TruncToInt(args[2]), start, list.Count) : list.Count;
            return list.GetRange(start, end - start);
        };
        _nativeFunctions["unique"] = args => {
            var list = (List<object>)args[0];
            var result = new List<object>();
            foreach (var item in list) if (!result.Any(x => ValuesEqual(x, item))) result.Add(item);
            return result;
        };

        // File I/O — усі шляхи проходять через Sandbox.CheckPath: за
        // замовчуванням це no-op, але під NX_SANDBOX=1 (напр. NyxilumMcp,
        // що виконує потенційно згенерований ШІ код) обмежує доступ
        // поточною робочою директорією.
        _nativeFunctions["readFile"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            return File.ReadAllText(path);
        };
        _nativeFunctions["writeFile"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            File.WriteAllText(path, args[1]?.ToString() ?? "");
            return true;
        };
        _nativeFunctions["appendFile"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            File.AppendAllText(path, args[1]?.ToString() ?? "");
            return true;
        };
        _nativeFunctions["fileExists"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            return File.Exists(path);
        };
        _nativeFunctions["readLines"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            return File.ReadAllLines(path).Select(l => (object)l).ToList();
        };
        // Без цих п'яти видалення/теки не було ЖОДНОГО способу прибрати чи
        // переглянути файли з .nx-коду (лише читати/писати/дізнатись, чи є
        // файл) - лаунчеру, наприклад, треба вміти видалити мод чи цілу
        // теку збірки при видаленні, і перелічити вміст теки mods/.
        _nativeFunctions["deleteFile"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        };
        _nativeFunctions["makeDir"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            Directory.CreateDirectory(path); // як mkdir -p - не падає, якщо вже існує чи батьківських тек нема
            return true;
        };
        _nativeFunctions["dirExists"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            return Directory.Exists(path);
        };
        _nativeFunctions["deleteDir"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            if (!Directory.Exists(path)) return false;
            Directory.Delete(path, recursive: true);
            return true;
        };
        _nativeFunctions["listDir"] = args => {
            var path = args[0]?.ToString() ?? "";
            NyxilumLang.Runtime.Sandbox.CheckPath(path);
            return Directory.GetFileSystemEntries(path).Select(p => (object)Path.GetFileName(p)).ToList();
        };
        _nativeFunctions["printNoNewLine"] = args => {
            Console.Write(args[0]);
            return null;
        };

        // System / Time
        _nativeFunctions["now"] = args => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _nativeFunctions["today"] = args => DateTime.Now.ToString("yyyy-MM-dd");
        _nativeFunctions["timestamp"] = args => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // now()/today() віддавали лише два фіксовані формати - жодного способу
        // ані відформатувати timestamp() у власний вигляд (напр. "dd.MM HH:mm"),
        // ані розпарсити рядок дати назад у число. formatDate/parseDate
        // закривають обидва напрямки через .NET custom date format strings.
        _nativeFunctions["formatDate"] = args => {
            long ts = (long)PopNumVal(args[0]);
            string format = args.Length > 1 && args[1] != null ? args[1].ToString()! : "yyyy-MM-dd HH:mm:ss";
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString(format);
            }
            catch (FormatException ex)
            {
                throw new Exception($"formatDate: неправильний формат \"{format}\": {ex.Message}");
            }
        };
        _nativeFunctions["parseDate"] = args => {
            string s = args[0]?.ToString() ?? "";
            string format = args[1]?.ToString() ?? "";
            if (!DateTime.TryParseExact(s, format, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
            {
                throw new Exception($"parseDate: не вдалось розпарсити \"{s}\" за форматом \"{format}\"");
            }
            return new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed)).ToUnixTimeSeconds();
        };
        _nativeFunctions["sleep"] = args => {
            System.Threading.Thread.Sleep(Convert.ToInt32(args[0]));
            return null;
        };

        // Раніше єдиний спосіб завершити процес з певним кодом — необроблений
        // throw (завжди код 1). Для CI/скриптів, яким треба саме "код 0 при
        // успіху, код 2 при провалених перевірках" тощо, exit(n) дає це напряму,
        // без штучного throw. Environment.Exit не повертається — рядок після
        // виклику exit() у .nx-скрипті просто ніколи не виконається.
        _nativeFunctions["exit"] = args => {
            Environment.Exit(TruncToInt(args[0]));
            return null;
        };

        // GUI (Windows Forms — недоступно поза Windows, тому весь блок
        // під #if WINDOWS; на Linux/Mac ці функції просто не реєструються,
        // а не падають на кожному запуску незалежно від того, чи скрипт
        // взагалі їх використовує).
#if WINDOWS
        _nativeFunctions["guiWindow"] = args => {
            NyxilumLang.Runtime.Sandbox.CheckGui();
            var form = new Form {
                Text = args[0].ToString(),
                Width = Convert.ToInt32(args[1]),
                Height = Convert.ToInt32(args[2]),
                StartPosition = FormStartPosition.CenterScreen
            };
            return form;
        };
        _nativeFunctions["guiButton"] = args => {
            var btn = new Button {
                Text = args[0].ToString(),
                Left = Convert.ToInt32(args[1]),
                Top = Convert.ToInt32(args[2]),
                Width = Convert.ToInt32(args[3]),
                Height = Convert.ToInt32(args[4])
            };
            return btn;
        };
        _nativeFunctions["guiLabel"] = args => {
            var label = new Label {
                Text = args[0].ToString(),
                Left = Convert.ToInt32(args[1]),
                Top = Convert.ToInt32(args[2]),
                Width = Convert.ToInt32(args[3]),
                Height = Convert.ToInt32(args[4]),
                Font = new Font("Arial", 12)
            };
            return label;
        };
        _nativeFunctions["guiTextBox"] = args => {
            var tb = new TextBox {
                Left = Convert.ToInt32(args[0]),
                Top = Convert.ToInt32(args[1]),
                Width = Convert.ToInt32(args[2]),
                Height = Convert.ToInt32(args[3]),
                Font = new Font("Arial", 14),
                ReadOnly = true,
                TextAlign = HorizontalAlignment.Right
            };
            return tb;
        };
        _nativeFunctions["guiAdd"] = args => {
            var parent = (Control)args[0];
            var child = (Control)args[1];
            GuiThreadGuard.Ensure(parent);
            parent.Controls.Add(child);
            return null;
        };
        _nativeFunctions["guiSetText"] = args => {
            var control = (Control)args[0];
            GuiThreadGuard.Ensure(control);
            control.Text = args[1]?.ToString() ?? "";
            return null;
        };
        _nativeFunctions["guiGetText"] = args => {
            var control = (Control)args[0];
            GuiThreadGuard.Ensure(control);
            return control.Text;
        };
        _nativeFunctions["guiOnAction"] = args => {
            // Другий аргумент — функція-ЗНАЧЕННЯ (як cmp у sort(arr,cmp)),
            // не рядок з іменем. Раніше клік просто нічого не робив —
            // обробник підписувався, але його тіло було порожнім, бо не
            // мав доступу до VM, щоб реально викликати NyxilumLang-функцію.
            var control = (Control)args[0];
            var funcRef = (NxFunctionRef)args[1];
            var vm = Current!;
            GuiThreadGuard.Ensure(control);

            if (control is Button btn) {
                btn.Click += (s, e) => vm.InvokeFunctionValue(funcRef, Array.Empty<object>());
            }
            return null;
        };
        _nativeFunctions["guiShow"] = args => {
            var form = (Form)args[0];
            GuiThreadGuard.Ensure(form);
            Application.Run(form);
            return null;
        };
#endif

        // Runtime Registration (Auto-load if available)
        NyxilumLang.Runtime.Modules.HttpModule.Register(_nativeFunctions);
        NyxilumLang.Runtime.Modules.OsModule.Register(_nativeFunctions);
        NyxilumLang.Runtime.Modules.RegexModule.Register(_nativeFunctions);
        NyxilumLang.Runtime.Modules.WebSocketModule.Register(_nativeFunctions);
        NyxilumLang.Runtime.Modules.ConcurrencyModule.Register(_nativeFunctions);
        // System.Diagnostics.Process не залежить від Windows Forms - працює
        // однаково на всіх платформах, тому поза #if WINDOWS (на відміну
        // від guiWindow/GraphicsModule нижче).
        NyxilumLang.Runtime.Modules.ProcessModule.Register(_nativeFunctions);
        NyxilumLang.Runtime.Modules.ArchiveModule.Register(_nativeFunctions);
        NyxilumLang.Runtime.Modules.NotificationModule.Register(_nativeFunctions);
#if WINDOWS
        NyxilumLang.Runtime.Modules.GraphicsModule.Register(_nativeFunctions);
#else
        // Linux/Mac: guiWindow/guiButton/... (той самий набір імен, що й
        // Windows Forms-версія вище) - тут поверх власного X11-протоколу
        // (Runtime/X11/), портованого з сестринського проєкту rawgui
        // (github.com/Faneraiy14/rawgui, уже перевірений на реальному
        // X-сервері). 2D/3D-канвас (createCanvas тощо, GraphicsModule) поки
        // лишається лише для Windows - GUI-вікна для лаунчера важливіші.
        NyxilumLang.Runtime.X11.X11Gui.Register(_nativeFunctions);
#endif
    }

    private static double PopNumVal(object val) => Convert.ToDouble(val);

    // Convert.ToInt32() округлює до найближчого ЦІЛОГО за банківським правилом
    // (round half to even): 5.9->6, 5.5->6, АЛЕ 4.5->4, 2.5->2. Для toInt(),
    // індексів масиву, довжин substring тощо очікується звичайне ВІДКИДАННЯ
    // дробової частини (як (int) у C/C#, parseInt у JS): 5.9->5, -5.9->-5.
    // Різниця непомітна на цілих числах і виринає лише на дробових аргументах.
    private static int TruncToInt(object val) => (int)Convert.ToDouble(val);

    private readonly List<(int Offset, int Line)> _lineMap;

    public VirtualMachine(Bytecode bytecode, IReadOnlyDictionary<string, object>? initialGlobals = null)
    {
        _code = bytecode.ToArray();
        _constants = bytecode.Constants;
        _functionAddresses = bytecode.FunctionAddresses;
        _lineMap = bytecode.LineMap;
        _globals = initialGlobals != null ? new Dictionary<string, object>(initialGlobals) : new();
    }

    // Використовується spawn() (ConcurrencyModule.cs): нова VM для окремого
    // потоку-воркера з ТИМ САМИМ байткодом, що й у "template" (_code/
    // _constants/_functionAddresses/_lineMap — усі readonly й незмінні
    // після компіляції, тому безпечно спільні між потоками), але повністю
    // окремим виконуваним станом (стек/фрейми/глобальні) — жодного
    // спільного мутабельного стану з батьківською VM чи іншими воркерами.
    internal VirtualMachine(VirtualMachine template, IReadOnlyDictionary<string, object>? initialGlobals)
    {
        _code = template._code;
        _constants = template._constants;
        _functionAddresses = template._functionAddresses;
        _lineMap = template._lineMap;
        _globals = initialGlobals != null ? new Dictionary<string, object>(initialGlobals) : new();
    }

    // Дозволяє нативним функціям (sort/mapArr/filter/reduce тощо) знайти
    // поточну VM і викликати NyxilumLang-функцію-значення як колбек.
    // [ThreadStatic]: з появою spawn() (ConcurrencyModule.cs) кілька VM
    // виконуються одночасно на різних потоках — звичайний static тут
    // означав би, що воркер на мить перезаписує Current головного потоку
    // (чи навпаки), і колбек виконується не в тій VM. Кожен потік бачить
    // власне значення.
    [ThreadStatic]
    public static VirtualMachine? Current;

    // Останній запис у _lineMap з Offset <= заданого ip. _lineMap
    // відсортована за зростанням Offset (компілюється послідовно), тому
    // проходимо з кінця й беремо перший підходящий — просте О(n), для
    // помилки виконання (не гарячий шлях) цього достатньо.
    private int LineAt(int ip)
    {
        for (int i = _lineMap.Count - 1; i >= 0; i--)
        {
            if (_lineMap[i].Offset <= ip) return _lineMap[i].Line;
        }
        return -1;
    }

    private int CurrentLine() => LineAt(_ip);

    // Функція, чиє тіло містить заданий ip — та з _functionAddresses з
    // НАЙБІЛЬШОЮ адресою, що все ще <= ip (компілятор кладе тіла функцій
    // послідовно одне за одним, тож "останній старт перед ip" і є
    // функцією, всередині якої ми зараз). null-адреса (ip до першої
    // функції, тобто код верхнього рівня файлу) — не помилка, а
    // "<основний код>".
    private string FunctionNameAt(int ip)
    {
        string? best = null;
        int bestAddr = -1;
        foreach (var (name, addr) in _functionAddresses)
        {
            if (addr <= ip && addr > bestAddr)
            {
                bestAddr = addr;
                best = name;
            }
        }
        return best ?? "<основний код>";
    }

    // Traceback у стилі Python: найглибший виклик (де стались помилка)
    // — останній рядок. _callStack зберігає адреси ПОВЕРНЕННЯ (позиція
    // одразу після CALL) — вони лежать усередині функції-ВИКЛИКАЧА, тому
    // FunctionNameAt/LineAt на кожному записі дають "хто й де викликав
    // наступний рівень вкладеності". Порожній рядок, якщо виклик один
    // (немає сенсу показувати traceback з одного кадру — досить "рядок N:").
    private string BuildTraceback()
    {
        if (_callStack.Count == 0) return "";

        var frames = new List<(string Name, int Line)> { (FunctionNameAt(_ip), LineAt(_ip)) };
        foreach (var returnIp in _callStack)
            frames.Add((FunctionNameAt(returnIp), LineAt(returnIp)));

        // Line == -1 — синтетичний маркер глибини (RunFunction/
        // InvokeFunctionValue штовхають поточний _ip у _callStack ПЕРЕД
        // переходом у функцію; для щойно створеної VM воркера це _ip=0,
        // до будь-якого реального рядка) — не справжній сайт виклику.
        frames.RemoveAll(f => f.Line <= 0);

        // Дедублікація суміжних однакових кадрів: авто-виклик main() з
        // синтетичного коду верхнього рівня (компілятор сам додає CALL
        // main наприкінці, якщо скрипт — самі функції без top-level коду)
        // не має власної адреси-межі "де закінчується main і починається
        // верхній рівень" — FunctionNameAt тоді приписує обидва тому самому
        // "main", і без дедублікації кадр показувався б двічі поспіль.
        var deduped = new List<(string Name, int Line)>();
        foreach (var f in frames)
            if (deduped.Count == 0 || deduped[^1] != f) deduped.Add(f);
        frames = deduped;

        if (frames.Count <= 1) return "";

        var sb = new System.Text.StringBuilder("Traceback (найглибший виклик — останній):\n");
        for (int i = frames.Count - 1; i >= 0; i--)
            sb.Append($"  рядок {frames[i].Line}, у {frames[i].Name}()\n");
        return sb.ToString();
    }

    public void Run()
    {
        Current = this;
        _currentFrame[0] = this; // Store VM in globals for callbacks if needed
        _currentFrame[1] = _nativeFunctions; // Allow access to native functions if needed

        while (_ip < _code.Length)
        {
            try
            {
                if (!Step()) return;
            }
            // _handlers.Count == 0 тут означає "жоден NyxilumLang try/catch це
            // не зловить" — той самий виняток однаково вилетить з Run()
            // необробленим, тож маємо єдиний шанс додати номер рядка й
            // traceback, перш ніж повідомлення дійде до Nx.cs як "Runtime
            // Error". Traceback НЕ додається до значення try/catch зловленої
            // помилки (див. UnwindToHandler нижче) — лише сюди, до того, що
            // реально впаде необробленим і покажеться користувачу.
            catch (Exception ex) when (_handlers.Count == 0)
            {
                if (ex is NxFormattedException) throw;
                int line = CurrentLine();
                string prefix = line > 0 ? $"рядок {line}: " : "";
                throw new NxFormattedException(BuildTraceback() + prefix + ex.Message, ex);
            }
        }
    }

    // Виконує рекурсивний виклик NyxilumLang-функції-значення (з нативної
    // функції, напр. sort/mapArr/filter/reduce) і повертає її результат.
    // Використовує ту саму Step()-логіку, що й основний цикл Run(), просто
    // зупиняється, щойно стек викликів повертається до глибини, з якої
    // почався цей вкладений виклик (тобто після RETURN колбека).
    public object? InvokeFunctionValue(NxFunctionRef funcRef, object[] args)
    {
        if (funcRef.NativeName != null)
        {
            return _nativeFunctions[funcRef.NativeName](args);
        }

        int savedIp = _ip;
        int targetCallDepth = _callStack.Count;

        _callStack.Push(_ip);
        _frameStack.Push(_currentFrame);
        _currentFrame = new Dictionary<int, object>();
        if (funcRef.Captured != null)
            foreach (var kv in funcRef.Captured) _currentFrame[kv.Key] = kv.Value;
        foreach (var a in args) _stack.Push(a);
        _ip = funcRef.Address;

        while (_callStack.Count > targetCallDepth)
        {
            if (!Step()) break;
        }

        var result = _stack.Count > 0 ? _stack.Pop() : null;
        _ip = savedIp;
        return result;
    }

    // Використовується spawn() (ConcurrencyModule.cs): на відміну від
    // InvokeFunctionValue (виклик УСЕРЕДИНІ вже запущеного Run() на цьому
    // самому потоці), тут funcRef — ЄДИНА програма для щойно створеної VM
    // на новому потоці, тому сама виставляє Current (thread-local — див.
    // коментар біля поля) і сама обгортає необроблені помилки номером
    // рядка, як це інакше робив би Run().
    public object? RunFunction(NxFunctionRef funcRef, object[] args)
    {
        Current = this;

        if (funcRef.NativeName != null)
            return _nativeFunctions[funcRef.NativeName](args);

        int targetCallDepth = _callStack.Count;
        _callStack.Push(_ip);
        _frameStack.Push(_currentFrame);
        _currentFrame = new Dictionary<int, object>();
        if (funcRef.Captured != null)
            foreach (var kv in funcRef.Captured) _currentFrame[kv.Key] = kv.Value;
        foreach (var a in args) _stack.Push(a);
        _ip = funcRef.Address;

        while (_callStack.Count > targetCallDepth)
        {
            try
            {
                if (!Step()) break;
            }
            catch (Exception ex) when (_handlers.Count == 0)
            {
                if (ex is NxFormattedException) throw;
                int line = CurrentLine();
                string prefix = line > 0 ? $"рядок {line}: " : "";
                throw new NxFormattedException(BuildTraceback() + prefix + ex.Message, ex);
            }
        }

        return _stack.Count > 0 ? _stack.Pop() : null;
    }

    private bool Step()
    {
            var installed = _super?[_ip];
            if (installed.HasValue && TryExecSuper(installed.Value)) return true;

            try
            {
            var op = (OpCode)_code[_ip++];
            switch (op)
            {
                case OpCode.LOAD_CONST: _stack.Push(_constants[ReadInt16()]); break;
                case OpCode.LOAD_VAR:
                    int idx = ReadInt16();
                    _stack.Push(_currentFrame.TryGetValue(idx, out var v) ? v : 0);
                    break;
                case OpCode.STORE_VAR: _currentFrame[ReadInt16()] = _stack.Pop(); break;
                case OpCode.GET_GLOBAL:
                    {
                        var name = (string)_constants[ReadInt16()];
                        _stack.Push(_globals.TryGetValue(name, out var gv) ? gv : 0);
                    }
                    break;
                case OpCode.SET_GLOBAL:
                    {
                        var name = (string)_constants[ReadInt16()];
                        _globals[name] = _stack.Pop();
                    }
                    break;
                case OpCode.CALL:
                    int addr = ReadInt16();
                    CheckCallDepth();
                    _callStack.Push(_ip);
                    _frameStack.Push(_currentFrame);
                    _currentFrame = new Dictionary<int, object>();
                    _ip = addr;
                    break;
                case OpCode.RETURN:
                    if (_callStack.Count > 0)
                    {
                        // Компілятор гарантує, що кожен RETURN кладе власне
                        // значення (див. Compiler.cs), тому стек тут порожнім
                        // бути не має. Якщо все ж порожній — це помилка VM, і
                        // повертати 0 було б тихим приховуванням: раніше саме
                        // так "Stack empty" перетворювалось на значення функції.
                        var returnVal = _stack.Count > 0 ? _stack.Pop() : null;
                        // Якщо return стався всередині try (без TRY_END), приберемо
                        // обробники, що належали фрейму, який зараз завершується —
                        // інакше вони лишились би "висіти" і зловили б чужу помилку.
                        while (_handlers.Count > 0 && _handlers.Peek().CallStackDepth >= _callStack.Count) _handlers.Pop();
                        _ip = _callStack.Pop();
                        _currentFrame = _frameStack.Pop();
                        _stack.Push(returnVal);
                    }
                    break;
                case OpCode.ADD:
                    {
                        var b = _stack.Pop();
                        var a = _stack.Pop();
                        // null у конкатенації друкується як "null" (так само, як
                        // це робить print), а не валить програму NullReference:
                        // "текст" + f(), де f нічого не повернула, — надто
                        // звичайна ситуація, щоб бути аварійною.
                        if (a is string || b is string) _stack.Push((a?.ToString() ?? "null") + (b?.ToString() ?? "null"));
                        else _stack.Push(Convert.ToDouble(a) + Convert.ToDouble(b));
                    }
                    break;
                case OpCode.SUB: { var b = PopNum(); var a = PopNum(); _stack.Push(a - b); } break;
                case OpCode.MUL: { var b = PopNum(); var a = PopNum(); _stack.Push(a * b); } break;
                case OpCode.DIV: { var b = PopNum(); var a = PopNum(); _stack.Push(a / b); } break;
                case OpCode.MOD: { var b = PopNum(); var a = PopNum(); _stack.Push(a % b); } break;
                case OpCode.EQ: { var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(ValuesEqual(a, b)); } break;
                case OpCode.NEQ: { var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(!ValuesEqual(a, b)); } break;
                case OpCode.LT: { var b = PopNum(); var a = PopNum(); _stack.Push(a < b); } break;
                case OpCode.LTE: { var b = PopNum(); var a = PopNum(); _stack.Push(a <= b); } break;
                case OpCode.GT: { var b = PopNum(); var a = PopNum(); _stack.Push(a > b); } break;
                case OpCode.GTE: { var b = PopNum(); var a = PopNum(); _stack.Push(a >= b); } break;
                case OpCode.AND: { var b = Convert.ToBoolean(_stack.Pop()); var a = Convert.ToBoolean(_stack.Pop()); _stack.Push(a && b); } break;
                case OpCode.OR: { var b = Convert.ToBoolean(_stack.Pop()); var a = Convert.ToBoolean(_stack.Pop()); _stack.Push(a || b); } break;
                case OpCode.NOT: _stack.Push(!Convert.ToBoolean(_stack.Pop())); break;
                case OpCode.JUMP:
                    {
                        int jumpInstrAddr = _ip - 1;
                        int target = ReadInt16();
                        // Зворотний перехід (target ще раніше по коду, ніж сам
                        // JUMP) — це саме те, у що компілюються while/for
                        // (Compiler.cs: JUMP на startPos/forStart). Рахуємо
                        // скільки разів пройшли через кожен такий заголовок
                        // циклу і після HotThreshold ітерацій пробуємо
                        // встановити superinstruction-и для нього.
                        if (_jitEnabled && target < jumpInstrAddr)
                        {
                            _backEdgeHits.TryGetValue(target, out var hits);
                            hits++;
                            _backEdgeHits[target] = hits;
                            if (hits == HotThreshold) InstallSuperOps(target, jumpInstrAddr);
                        }
                        _ip = target;
                    }
                    break;
                case OpCode.JUMP_IF_FALSE:
                    int addr2 = ReadInt16();
                    if (!Convert.ToBoolean(_stack.Pop())) _ip = addr2;
                    break;
                case OpCode.PRINT: Console.WriteLine(_stack.Pop()); break;
                
                // ВВІД
                case OpCode.READ_LINE: _stack.Push(Console.ReadLine() ?? ""); break;
                case OpCode.READ_INT:
                    while (true)
                    {
                        var input = Console.ReadLine();
                        // null = ввід закінчився (Ctrl+Z, конвеєр, перенаправлення
                        // файлу). Раніше цикл у такому разі друкував запит
                        // нескінченно, бо чекав на ввід, якого вже не буде.
                        if (input == null) throw new Exception("Ввід закінчився: readInt() не отримав числа");
                        // Штовхаємо double, а не int: усі інші числа в мові -
                        // double, а порівняння int з double через object.Equals
                        // давало false навіть для однакових значень.
                        if (int.TryParse(input, out var result)) { _stack.Push((double)result); break; }
                        Console.Write("Введіть число: ");
                    }
                    break;
                case OpCode.READ_DOUBLE:
                    while (true)
                    {
                        var input = Console.ReadLine();
                        if (input == null) throw new Exception("Ввід закінчився: readDouble() не отримав числа");
                        if (double.TryParse(input, out var result)) { _stack.Push(result); break; }
                        Console.Write("Введіть число: ");
                    }
                    break;
                
                // ФАЙЛИ
                case OpCode.READ_FILE:
                    var path = _stack.Pop()?.ToString() ?? "";
                    try { _stack.Push(File.ReadAllText(path)); }
                    catch { _stack.Push(""); }
                    break;
                case OpCode.WRITE_FILE:
                    var content = _stack.Pop()?.ToString() ?? "";
                    var filePath = _stack.Pop()?.ToString() ?? "";
                    try { File.WriteAllText(filePath, content); _stack.Push(true); }
                    catch { _stack.Push(false); }
                    break;
                case OpCode.APPEND_FILE:
                    content = _stack.Pop()?.ToString() ?? "";
                    filePath = _stack.Pop()?.ToString() ?? "";
                    try { File.AppendAllText(filePath, content); _stack.Push(true); }
                    catch { _stack.Push(false); }
                    break;
                case OpCode.FILE_EXISTS:
                    filePath = _stack.Pop()?.ToString() ?? "";
                    _stack.Push(File.Exists(filePath));
                    break;
                
                // МАТЕМАТИКА
                case OpCode.SQRT: _stack.Push(Math.Sqrt(PopNum())); break;
                case OpCode.ABS: _stack.Push(Math.Abs(PopNum())); break;
                case OpCode.POW: { var b = PopNum(); var a = PopNum(); _stack.Push(Math.Pow(a, b)); } break;
                case OpCode.SIN: _stack.Push(Math.Sin(PopNum())); break;
                case OpCode.COS: _stack.Push(Math.Cos(PopNum())); break;
                case OpCode.TAN: _stack.Push(Math.Tan(PopNum())); break;
                case OpCode.ROUND: _stack.Push(Math.Round(PopNum())); break;
                case OpCode.FLOOR: _stack.Push(Math.Floor(PopNum())); break;
                case OpCode.CEIL: _stack.Push(Math.Ceiling(PopNum())); break;
                case OpCode.MAX: { var b = PopNum(); var a = PopNum(); _stack.Push(Math.Max(a, b)); } break;
                case OpCode.MIN: { var b = PopNum(); var a = PopNum(); _stack.Push(Math.Min(a, b)); } break;
                case OpCode.TO_STRING: _stack.Push(_stack.Pop()?.ToString() ?? "null"); break;
                case OpCode.TO_INT: _stack.Push(TruncToInt(_stack.Pop())); break;
                case OpCode.TO_DOUBLE: _stack.Push(Convert.ToDouble(_stack.Pop())); break;
                case OpCode.LEN:
                    {
                        var val = _stack.Pop();
                        if (val is List<object> arr) _stack.Push((double)arr.Count);
                        else if (val is string s) _stack.Push((double)s.Length);
                        else if (val is Dictionary<string, object> d) _stack.Push((double)d.Count);
                        else _stack.Push(0.0);
                    }
                    break;

                case OpCode.ARRAY_NEW:
                    {
                        int size = ReadInt16();
                        var arr = new List<object>(size);
                        for (int i = 0; i < size; i++) arr.Add(0);
                        for (int i = size - 1; i >= 0; i--) arr[i] = _stack.Pop();
                        NxGc.Instance.RecordAllocation();
                        _stack.Push(arr);
                    }
                    break;
                case OpCode.ARRAY_GET:
                    {
                        int index = TruncToInt(_stack.Pop());
                        var arrObj = _stack.Pop();
                        if (arrObj is not List<object> arr)
                            throw new Exception("Спроба звернутися до елемента масиву, але значення зліва - null або не масив.");
                        if (index < 0 || index >= arr.Count)
                            throw new Exception($"Індекс {index} поза межами масиву (довжина {arr.Count}).");
                        _stack.Push(arr[index]);
                    }
                    break;
                case OpCode.ARRAY_SET:
                    {
                        var val = _stack.Pop();
                        int index = TruncToInt(_stack.Pop());
                        var arrObj = _stack.Pop();
                        if (arrObj is not List<object> arr)
                            throw new Exception("Спроба присвоїти елемент масиву, але значення зліва - null або не масив.");
                        if (index < 0 || index >= arr.Count)
                            throw new Exception($"Індекс {index} поза межами масиву (довжина {arr.Count}).");
                        arr[index] = val;
                    }
                    break;
                case OpCode.STRUCT_NEW:
                    {
                        int fieldCount = ReadInt16();
                        var fields = new Dictionary<string, object>();
                        for (int i = 0; i < fieldCount; i++)
                        {
                            var val = _stack.Pop();
                            var name = (string)_stack.Pop();
                            fields[name] = val;
                        }
                        NxGc.Instance.RecordAllocation();
                        _stack.Push(fields);
                    }
                    break;
                case OpCode.STRUCT_GET:
                    {
                        var fieldName = (string)_stack.Pop();
                        var fieldsObj = _stack.Pop();
                        if (fieldsObj is not Dictionary<string, object> fields)
                            throw new Exception($"Спроба звернутися до поля '{fieldName}', але значення зліва - null або не структура.");
                        if (!fields.TryGetValue(fieldName, out var val))
                            throw new Exception($"У структурі немає поля '{fieldName}'.");
                        _stack.Push(val);
                    }
                    break;
                case OpCode.STRUCT_SET:
                    {
                        var val = _stack.Pop();
                        var fieldName = (string)_stack.Pop();
                        var fieldsObj = _stack.Pop();
                        if (fieldsObj is not Dictionary<string, object> fields)
                            throw new Exception($"Спроба присвоїти поле '{fieldName}', але значення зліва - null або не структура.");
                        fields[fieldName] = val;
                    }
                    break;
                case OpCode.CALL_NATIVE:
                    {
                        int nameIdx = ReadInt16();
                        int argCount = ReadInt16();
                        var name = (string)_constants[nameIdx];
                        var args = new object[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            args[i] = _stack.Count > 0 ? _stack.Pop() : null!;
                        // Компілятор реєструє gui*/canvas*-функції як "оголошені"
                        // на всіх платформах (щоб .nx-файл однаково компілювався
                        // скрізь), але сама реалізація доступна лише на Windows
                        // (Windows Forms). Без цієї перевірки виклик на Linux/Mac
                        // впав би з сирим .NET KeyNotFoundException.
                        if (!_nativeFunctions.TryGetValue(name, out var native))
                            throw new Exception($"Функція '{name}' недоступна на цій платформі (потребує Windows — GUI/графіка на Windows Forms)");
                        var result = native(args);
                        // ВИПРАВЛЕНО: раніше `result ?? 0` тихо перетворював БУДЬ-ЯКЕ
                        // легітимне null-значення, повернуте нативною функцією
                        // (напр. mapGet на відсутньому ключі), на 0. Це було
                        // непомітно, поки null не став справжнім значенням мови
                        // (Фаза 8+); тепер null з нативної функції має лишатись null.
                        _stack.Push(result!);
                    }
                    break;
                case OpCode.CALL_METHOD:
                    {
                        int nameIdx = ReadInt16();
                        int argCount = ReadInt16();
                        var methodName = (string)_constants[nameIdx];
                        
                        // Object is at stack depth argCount
                        var obj = _stack.ElementAt(argCount);
                        if (obj is Dictionary<string, object> dict && dict.TryGetValue("__type", out var typeVal))
                        {
                            var typeName = typeVal.ToString();
                            var fullName = $"{typeName}.{methodName}";
                            if (_functionAddresses.TryGetValue(fullName, out int methodAddr))
                            {
                                CheckCallDepth();
                                _callStack.Push(_ip);
                                _frameStack.Push(_currentFrame);
                                _currentFrame = new Dictionary<int, object>();
                                _ip = methodAddr;
                            }
                            else
                            {
                                throw new Exception($"Метод '{methodName}' не знайдено у структурі '{typeName}'");
                            }
                        }
                        else
                        {
                            throw new Exception($"Спроба виклику методу '{methodName}' на об'єкті, що не є структурою");
                        }
                    }
                    break;
                
                case OpCode.MAKE_CLOSURE:
                    {
                        int closureAddr = ReadInt16();
                        int slotsConstIdx = ReadInt16();
                        var slotsList = (List<object>)_constants[slotsConstIdx];
                        var captured = new Dictionary<int, object>();
                        foreach (var slotObj in slotsList)
                        {
                            int slot = Convert.ToInt32(slotObj);
                            if (_currentFrame.TryGetValue(slot, out var val)) captured[slot] = val;
                        }
                        _stack.Push(new NxFunctionRef { Address = closureAddr, Captured = captured });
                    }
                    break;
                case OpCode.CALL_VALUE:
                    {
                        int argCount = ReadInt16();
                        var callArgs = new object[argCount];
                        for (int i = argCount - 1; i >= 0; i--) callArgs[i] = _stack.Count > 0 ? _stack.Pop() : null!;
                        var funcRef = (NxFunctionRef)_stack.Pop();

                        if (funcRef.NativeName != null)
                        {
                            // Посилання на нативну функцію - немає байткод-адреси,
                            // викликаємо напряму, як CALL_NATIVE.
                            var nativeResult = _nativeFunctions[funcRef.NativeName](callArgs);
                            _stack.Push(nativeResult!);
                            break;
                        }

                        CheckCallDepth();
                        _callStack.Push(_ip);
                        _frameStack.Push(_currentFrame);
                        _currentFrame = new Dictionary<int, object>();
                        if (funcRef.Captured != null)
                            foreach (var kv in funcRef.Captured) _currentFrame[kv.Key] = kv.Value;
                        foreach (var a in callArgs) _stack.Push(a);
                        _ip = funcRef.Address;
                    }
                    break;

                case OpCode.TRY_BEGIN:
                    {
                        int catchAddr = ReadInt16();
                        int varSlot = ReadInt16();
                        _handlers.Push(new ExceptionHandler
                        {
                            CatchAddr = catchAddr,
                            VarSlot = varSlot,
                            StackDepth = _stack.Count,
                            CallStackDepth = _callStack.Count,
                            FrameStackDepth = _frameStack.Count
                        });
                    }
                    break;
                case OpCode.TRY_END:
                    if (_handlers.Count > 0) _handlers.Pop();
                    break;
                case OpCode.THROW:
                    {
                        var thrown = _stack.Count > 0 ? _stack.Pop() : "помилка";
                        if (_handlers.Count > 0) UnwindToHandler(thrown);
                        else throw new NxThrowException(thrown?.ToString() ?? "помилка");
                    }
                    break;

                case OpCode.POP: if (_stack.Count > 0) _stack.Pop(); break;

                case OpCode.HALT: return false;
            }
            }
            catch (NxThrowException)
            {
                // Явний throw без активного обробника — це вже вирішено в THROW
                // (там або UnwindToHandler, або кидається саме цей виняток нагору,
                // тому тут його просто пропускаємо далі, до Nx.cs).
                throw;
            }
            catch (Exception ex) when (_handlers.Count > 0)
            {
                // Внутрішня помилка VM (ділення на нуль, вихід за межі масиву,
                // помилка нативної функції тощо) під час активного try —
                // перехоплюємо її так само, як явний throw. Номер рядка тут
                // так само корисний, як і в необробленому Runtime Error —
                // catch(e) у самому NyxilumLang-скрипті теж хоче знати ДЕ.
                int line = CurrentLine();
                string prefix = line > 0 ? $"рядок {line}: " : "";
                UnwindToHandler(prefix + ex.Message);
            }
        return true;
    }

    // Виконує вже встановлену superinstruction напряму через C#-арифметику,
    // минаючи стек/бокси. Повертає false БЕЗ жодного побічного ефекту
    // (нічого не займає з _stack, не рухає _ip), якщо guard типів не
    // пройшов, — тоді Step() просто продовжує звичайний switch з того ж
    // самого _ip, наче superinstruction тут і не було.
    private bool TryExecSuper(SuperOp s)
    {
        switch (s.Kind)
        {
            case SuperOpKind.IncLocal:
            {
                if (!_currentFrame.TryGetValue(s.SlotA, out var av) || av is not double da) return false;
                if (_constants[s.ConstIdx] is not double dc) return false;
                _currentFrame[s.SlotA] = da + dc;
                _ip += s.Length;
                return true;
            }
            case SuperOpKind.CmpJump:
            {
                if (!_currentFrame.TryGetValue(s.SlotA, out var av) || av is not double da) return false;
                double db;
                if (s.RightIsConst)
                {
                    if (_constants[s.ConstIdx] is not double dc) return false;
                    db = dc;
                }
                else
                {
                    if (!_currentFrame.TryGetValue(s.SlotB, out var bv) || bv is not double dbv) return false;
                    db = dbv;
                }
                bool cond = s.CompareOp switch
                {
                    OpCode.LT => da < db,
                    OpCode.LTE => da <= db,
                    OpCode.GT => da > db,
                    OpCode.GTE => da >= db,
                    _ => false
                };
                _ip = cond ? _ip + s.Length : s.JumpTarget;
                return true;
            }
            default:
                return false;
        }
    }

    // Розмір інструкції в байтах (1 байт опкод + операнди), потрібен щоб
    // коректно пройти байткод циклу інструкція-за-інструкцією замість
    // наївного побайтового сканування — інакше байт операнда іншої
    // інструкції міг би хибно "збігтись" зі значенням TRY_BEGIN/THROW.
    private static int InstrLength(OpCode op) => op switch
    {
        OpCode.CALL_NATIVE or OpCode.CALL_METHOD or OpCode.MAKE_CLOSURE or OpCode.TRY_BEGIN => 5,
        OpCode.LOAD_CONST or OpCode.LOAD_VAR or OpCode.STORE_VAR or OpCode.GET_GLOBAL or OpCode.SET_GLOBAL
            or OpCode.CALL or OpCode.JUMP or OpCode.JUMP_IF_FALSE or OpCode.ARRAY_NEW or OpCode.STRUCT_NEW
            or OpCode.CALL_VALUE => 3,
        _ => 1
    };

    // Гарячий зворотний перехід (backward JUMP) знайдено вдруге за
    // HotThreshold ітерацій — пробуємо розпізнати 2 безпечні патерни:
    // CMP_JMP у заголовку циклу (loopStart) і INC_LOCAL одразу перед самим
    // JUMP (jumpInstrAddr). Якщо десь у тілі циклу є try/catch/throw —
    // ліпше взагалі нічого не встановлювати: superinstruction обходить
    // звичайний switch, а разом з ним і всю логіку _handlers/UnwindToHandler.
    private void InstallSuperOps(int loopStart, int jumpInstrAddr)
    {
        _super ??= new SuperOp?[_code.Length];

        for (int i = loopStart; i < jumpInstrAddr;)
        {
            var op = (OpCode)_code[i];
            if (op is OpCode.TRY_BEGIN or OpCode.TRY_END or OpCode.THROW) return;
            i += InstrLength(op);
        }

        TryInstallCmpJump(loopStart, jumpInstrAddr);
        TryInstallIncLocal(loopStart, jumpInstrAddr);
    }

    private void TryInstallCmpJump(int loopStart, int loopEndExclusive)
    {
        int i = loopStart;
        if (!TryReadOperand(ref i, loopEndExclusive, out var leftOp, out var leftArg)) return;
        if (leftOp != OpCode.LOAD_VAR) return;
        if (!TryReadOperand(ref i, loopEndExclusive, out var rightOp, out var rightArg)) return;
        if (rightOp != OpCode.LOAD_VAR && rightOp != OpCode.LOAD_CONST) return;
        if (i >= loopEndExclusive) return;
        var cmpOp = (OpCode)_code[i];
        if (cmpOp is not (OpCode.LT or OpCode.LTE or OpCode.GT or OpCode.GTE)) return;
        i += 1;
        if (!TryReadOperand(ref i, loopEndExclusive, out var jmpOp, out var jmpArg)) return;
        if (jmpOp != OpCode.JUMP_IF_FALSE) return;

        _super![loopStart] = new SuperOp
        {
            Kind = SuperOpKind.CmpJump,
            Length = i - loopStart,
            SlotA = leftArg,
            RightIsConst = rightOp == OpCode.LOAD_CONST,
            SlotB = rightArg,
            ConstIdx = rightArg,
            CompareOp = cmpOp,
            JumpTarget = jmpArg
        };
    }

    private void TryInstallIncLocal(int loopStart, int jumpInstrAddr)
    {
        const int patternLen = 10; // LOAD_VAR(3) + LOAD_CONST(3) + ADD(1) + STORE_VAR(3)
        int start = jumpInstrAddr - patternLen;
        if (start < loopStart) return;

        int i = start;
        if (!TryReadOperand(ref i, jumpInstrAddr, out var op1, out var slot1)) return;
        if (op1 != OpCode.LOAD_VAR) return;
        if (!TryReadOperand(ref i, jumpInstrAddr, out var op2, out var constIdx)) return;
        if (op2 != OpCode.LOAD_CONST) return;
        if (i >= jumpInstrAddr || (OpCode)_code[i] != OpCode.ADD) return;
        i += 1;
        if (!TryReadOperand(ref i, jumpInstrAddr, out var op3, out var slot2)) return;
        if (op3 != OpCode.STORE_VAR) return;
        if (slot1 != slot2) return;
        if (i != jumpInstrAddr) return;

        _super![start] = new SuperOp
        {
            Kind = SuperOpKind.IncLocal,
            Length = patternLen,
            SlotA = slot1,
            ConstIdx = constIdx
        };
    }

    // Читає одну інструкцію з операндом (LOAD_VAR/LOAD_CONST/STORE_VAR/
    // JUMP_IF_FALSE тощо — усі рівно 3 байти: опкод + Int16), не виходячи
    // за межі [i, limit).
    private bool TryReadOperand(ref int i, int limit, out OpCode op, out int arg)
    {
        op = default; arg = 0;
        if (i + 3 > limit) return false;
        op = (OpCode)_code[i];
        arg = _code[i + 1] | (_code[i + 2] << 8);
        i += 3;
        return true;
    }

    private int ReadInt16()
    {
        int val = _code[_ip] | (_code[_ip + 1] << 8);
        _ip += 2;
        return val;
    }

    private double PopNum() => Convert.ToDouble(_stack.Pop());

    // Порівняння на рівність. Раніше тут був object.Equals, який порівнює ще й
    // ТИПИ "коробок": int 2 та double 2.0 вважались різними, тому x == 2 давало
    // false для числа з readInt(). При цьому < і > працювали правильно, бо
    // зводять обидва боки до double через PopNum - через цю різницю баг і був
    // непомітним: підказки "більше/менше" діяли, а перевірка на рівність ні.
    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;

        if (IsNumber(a) && IsNumber(b))
            return Convert.ToDouble(a) == Convert.ToDouble(b);

        return Equals(a, b);
    }

    private static bool IsNumber(object v) =>
        v is double || v is float || v is decimal ||
        v is int || v is long || v is short || v is sbyte ||
        v is uint || v is ulong || v is ushort || v is byte;
}
