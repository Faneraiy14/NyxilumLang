using System.IO.Compression;

namespace NyxilumLang.Runtime.Modules;

// zip - без цього неможливо написати лаунчер (версії/бібліотеки Minecraft і
// самі моди розповсюджуються саме як .zip/.jar), а окремо ставити пакет
// заради розпаковування одного архіву — зайва залежність там, де .NET уже
// має System.IO.Compression у собі.
public static class ArchiveModule
{
    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        // zipExtract(zipPath, destDir) -> кількість розпакованих файлів.
        // overwriteFiles: true - повторний виклик (напр. повторна установка
        // тієї самої версії гри) не падає на "файл уже існує".
        registry["zipExtract"] = args => {
            var zipPath = args[0].ToString()!;
            var destDir = args[1].ToString()!;
            Sandbox.CheckPath(zipPath);
            Sandbox.CheckPath(destDir);
            Directory.CreateDirectory(destDir);

            using var archive = ZipFile.OpenRead(zipPath);
            int count = 0;
            foreach (var entry in archive.Entries)
            {
                // Записи-теки в zip мають порожнє Name (лише FullName з "/"
                // на кінці) - ExtractToFile на них кинув би помилку, тому
                // лише створюємо структуру тек і йдемо далі.
                var destPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!destPath.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar) && destPath != Path.GetFullPath(destDir))
                    throw new Exception($"zipExtract: підозрілий шлях у архіві ('{entry.FullName}' веде за межі '{destDir}') - можлива Zip Slip атака");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
                count++;
            }
            return (double)count;
        };

        // zipEntries(zipPath) -> масив імен файлів усередині архіву (з
        // підтеками через "/", як у самому zip) - без розпаковування,
        // щоб .nx-скрипт міг вирішити, ЩО саме йому треба (напр. лише
        // *.so/*.dll з natives-jar).
        registry["zipEntries"] = args => {
            var zipPath = args[0].ToString()!;
            Sandbox.CheckPath(zipPath);
            using var archive = ZipFile.OpenRead(zipPath);
            var names = new List<object>();
            foreach (var entry in archive.Entries)
                if (!string.IsNullOrEmpty(entry.Name)) names.Add(entry.FullName);
            return names;
        };

        // zipExtractEntry(zipPath, entryName, destPath) -> true/false (є така
        // точка в архіві?) - витягнути лише ОДИН файл, напр. конкретну
        // .dll/.so з natives-jar, без розпаковування решти архіву.
        registry["zipExtractEntry"] = args => {
            var zipPath = args[0].ToString()!;
            var entryName = args[1].ToString()!;
            var destPath = args[2].ToString()!;
            Sandbox.CheckPath(zipPath);
            Sandbox.CheckPath(destPath);

            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry(entryName);
            if (entry == null) return false;
            var dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            entry.ExtractToFile(destPath, overwrite: true);
            return true;
        };

        // zipCreate(zipPath, sourceDir) -> запаковує ЦІЛУ теку в новий zip
        // (напр. експорт світу/збірки). Якщо zipPath уже існує - видаляє
        // спершу: ZipFile.CreateFromDirectory інакше кидає помилку.
        registry["zipCreate"] = args => {
            var zipPath = args[0].ToString()!;
            var sourceDir = args[1].ToString()!;
            Sandbox.CheckPath(zipPath);
            Sandbox.CheckPath(sourceDir);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return null;
        };
    }
}
