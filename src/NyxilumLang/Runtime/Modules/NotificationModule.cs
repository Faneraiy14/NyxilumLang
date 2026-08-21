using System.Diagnostics;

namespace NyxilumLang.Runtime.Modules;

// Системне push-сповіщення (той самий баблик, що й у Discord/пошти,
// тільки нативний для ОС) - викликає зовнішню утиліту через
// Process.Start, тож та сама категорія доступу, що й procRun/procStart
// (Sandbox.CheckProcess()). Без нової залежності на Windows Forms у
// net10.0 (не-Windows) збірці: усі три гілки нижче делегують у зовнішній
// виконуваний файл ОС, а не лінкують GUI-бібліотеку напряму.
public static class NotificationModule
{
    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["notify"] = args => {
            Sandbox.CheckProcess();
            string title = args.Length > 0 ? args[0]?.ToString() ?? "" : "";
            string message = args.Length > 1 ? args[1]?.ToString() ?? "" : "";

            ProcessStartInfo psi;
            if (OperatingSystem.IsLinux())
            {
                psi = new ProcessStartInfo("notify-send", new[] { title, message })
                {
                    UseShellExecute = false
                };
            }
            else if (OperatingSystem.IsMacOS())
            {
                var script = $"display notification {EscapeAppleScript(message)} with title {EscapeAppleScript(title)}";
                psi = new ProcessStartInfo("osascript", new[] { "-e", script })
                {
                    UseShellExecute = false
                };
            }
            else if (OperatingSystem.IsWindows())
            {
                // Без окремої залежності на BurntToast: класичний
                // System.Windows.Forms.NotifyIcon-балон через один
                // PowerShell-виклик, що сам вантажить WinForms - працює на
                // будь-якому Windows без встановлення додаткових модулів.
                var psScript = "Add-Type -AssemblyName System.Windows.Forms; " +
                    "$n = New-Object System.Windows.Forms.NotifyIcon; " +
                    "$n.Icon = [System.Drawing.SystemIcons]::Information; " +
                    "$n.Visible = $true; " +
                    $"$n.ShowBalloonTip(5000, {EscapePowerShell(title)}, {EscapePowerShell(message)}, [System.Windows.Forms.ToolTipIcon]::Info); " +
                    "Start-Sleep -Seconds 5; $n.Dispose()";
                psi = new ProcessStartInfo("powershell", new[] { "-NoProfile", "-Command", psScript })
                {
                    UseShellExecute = false
                };
            }
            else
            {
                throw new Exception("notify(): невідома ОС, сповіщення не підтримуються");
            }

            try
            {
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                throw new Exception("notify(): не вдалось показати сповіщення - " + ex.Message);
            }
            return null;
        };
    }

    private static string EscapeAppleScript(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string EscapePowerShell(string s) => "'" + s.Replace("'", "''") + "'";
}
