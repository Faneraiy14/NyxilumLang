using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NyxilumLang.Runtime.Modules;

public static class OsModule
{
    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["osPlatform"] = args => RuntimeInformation.OSDescription;
        registry["osArchitecture"] = args => RuntimeInformation.OSArchitecture.ToString();
        registry["osMemory"] = args => Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024); // MB
        registry["osCpuCount"] = args => Environment.ProcessorCount;
        registry["osEnv"] = args => {
            if (Sandbox.Enabled)
                throw new Exception("Пісочниця: читання змінних середовища заборонено (NX_SANDBOX=1)");
            return Environment.GetEnvironmentVariable(args[0].ToString()!) ?? "";
        };
        registry["osCwd"] = args => Directory.GetCurrentDirectory();

        // Список процесів усієї ОС (не лише дочірніх, як procRun/procStart) -
        // та сама категорія розкриття інформації про хост, що й запуск
        // процесів, тож той самий Sandbox.CheckProcess(), а не окрема
        // перевірка. %CPU через Process.TotalProcessorTime не миттєвий -
        // потрібні дві точки в часі, щоб порахувати різницю (як це робить
        // сам top/htop) - звідси короткий Sleep всередині.
        registry["osProcessList"] = args => {
            Sandbox.CheckProcess();
            var procs = Process.GetProcesses();
            var before = new Dictionary<int, TimeSpan>();
            foreach (var p in procs)
            {
                try { before[p.Id] = p.TotalProcessorTime; } catch { /* процес міг завершитись саме зараз */ }
            }
            var sampleMs = 200;
            Thread.Sleep(sampleMs);

            // Dictionary<string, object>, НЕ NxMap - dot-доступ (p.pid) працює
            // через STRUCT_GET лише на цьому типі (той самий підхід, що й
            // gc_stats()/procRun() вище й у ProcessModule.cs).
            var result = new List<object>();
            var cpuCount = Environment.ProcessorCount;
            foreach (var p in procs)
            {
                try
                {
                    double cpuPercent = 0.0;
                    if (before.TryGetValue(p.Id, out var prevCpu))
                    {
                        var deltaCpuMs = (p.TotalProcessorTime - prevCpu).TotalMilliseconds;
                        cpuPercent = Math.Round(deltaCpuMs / sampleMs / cpuCount * 100.0, 1);
                    }
                    result.Add(new Dictionary<string, object> {
                        ["pid"] = (double)p.Id,
                        ["name"] = p.ProcessName,
                        ["memMB"] = (double)(p.WorkingSet64 / (1024 * 1024)),
                        ["cpuPercent"] = cpuPercent
                    });
                }
                catch
                {
                    // процес завершився між першим і другим замірами - пропускаємо,
                    // а не валимо весь виклик через один зниклий рядок
                }
                finally
                {
                    p.Dispose();
                }
            }
            return result;
        };

        // Вільне/загальне місце на диску, що містить шлях path (не лише
        // корінь) - DriveInfo сам знаходить потрібний том. Той самий
        // CheckPath(), що й інші файлові функції: це інформація про
        // конкретний шлях у файловій системі.
        registry["osDiskFree"] = args => {
            var path = args.Length > 0 ? args[0].ToString()! : Directory.GetCurrentDirectory();
            Sandbox.CheckPath(path);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? path);
            return new Dictionary<string, object> {
                ["freeGB"] = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024 / 1024, 2),
                ["totalGB"] = Math.Round(drive.TotalSize / 1024.0 / 1024 / 1024, 2)
            };
        };
    }
}
