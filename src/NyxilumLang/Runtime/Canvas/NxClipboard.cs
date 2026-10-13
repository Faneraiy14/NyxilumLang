using System.Diagnostics;

namespace NyxilumLang.Runtime.Canvas;

// Системний буфер обміну для clipboardGet/clipboardSet.
// Windows - Windows Forms Clipboard (потребує STA-потоку - окремий потік).
// Linux - готові утиліти: wl-copy/wl-paste (Wayland), xclip чи xsel (X11).
// Власна реалізація X11-selection (власник буфера мусить ЖИТИ й відповідати
// на запити інших програм) - окремий великий шматок; утиліти роблять саме це.
// Немає жодної - буфер лише всередині процесу (копіювання/вставка в межах
// самої програми все одно працює).
public static class NxClipboard
{
    private static string _internal = "";

    public static string Get()
    {
#if WINDOWS
        string result = _internal;
        var t = new Thread(() => { try { if (System.Windows.Forms.Clipboard.ContainsText()) result = System.Windows.Forms.Clipboard.GetText(); } catch { } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        return result;
#else
        foreach (var (exe, args) in PasteTools())
        {
            var text = Run(exe, args, null);
            if (text != null) return text;
        }
        return _internal;
#endif
    }

    public static void Set(string text)
    {
        _internal = text;
#if WINDOWS
        var t = new Thread(() => { try { System.Windows.Forms.Clipboard.SetText(text.Length == 0 ? " " : text); } catch { } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
#else
        foreach (var (exe, args) in CopyTools())
            if (Run(exe, args, text) != null) return;
#endif
    }

#if !WINDOWS
    private static bool Wayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static IEnumerable<(string, string[])> PasteTools()
    {
        if (Wayland) yield return ("wl-paste", new[] { "--no-newline" });
        yield return ("xclip", new[] { "-selection", "clipboard", "-o" });
        yield return ("xsel", new[] { "--clipboard", "--output" });
    }

    private static IEnumerable<(string, string[])> CopyTools()
    {
        if (Wayland) yield return ("wl-copy", Array.Empty<string>());
        yield return ("xclip", new[] { "-selection", "clipboard" });
        yield return ("xsel", new[] { "--clipboard", "--input" });
    }

    // null - утиліти немає або вона впала.
    private static string? Run(string exe, string[] args, string? input)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardInput = input != null, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (input != null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
            // wl-copy/xclip лишаються у фоні власниками буфера - stdout не чекаємо.
            if (input != null) return p.WaitForExit(1500) && p.ExitCode != 0 ? null : "";
            var outText = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1500);
            return p.ExitCode == 0 ? outText : null;
        }
        catch { return null; }
    }
#endif
}
