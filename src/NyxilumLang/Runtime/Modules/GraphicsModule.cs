// 2D/3D графіка на Windows Forms — недоступна поза Windows, тому весь
// файл під #if WINDOWS. Реєстрація цих функцій (Register) теж викликається
// лише в межах #if WINDOWS у VirtualMachine.cs.
#if WINDOWS
using System.Windows.Forms;
using System.Drawing;
using System.Media;

namespace NyxilumLang.Runtime.Modules;

// Полотно для 2D-графіки: вікно (Form) + PictureBox, що показує Bitmap,
// у який NyxilumLang-скрипт малює. Ввід (клавіатура/миша) накопичується в
// полях канви й опитується нативними функціями (isKeyDown/getMouseX/...).
public class NxCanvas
{
    public Form Form = null!;
    public PictureBox Surface = null!;
    public Bitmap Buffer = null!;
    public HashSet<Keys> KeysDown = new();
    public bool MouseDown;
    public int MouseX, MouseY;
}

public static class GraphicsModule
{
    // Крок A плану NyxilumEngine (22.09.2026) - кеш завантажених
    // зображень ЗА ШЛЯХОМ, щоб повторний loadImage() того самого
    // файлу в ігровому циклі (типово - на КОЖНОМУ кадрі, той самий
    // спрайт) не читав диск щоразу - лише при ПЕРШОМУ виклику для
    // даного шляху.
    private static readonly Dictionary<string, Image> _imageCache = new();

    public static void Register(Dictionary<string, Func<object[], object?>> registry)
    {
        registry["createCanvas"] = args =>
        {
            string title = args[0]?.ToString() ?? "NyxilumLang";
            int width = Convert.ToInt32(args[1]);
            int height = Convert.ToInt32(args[2]);

            var canvas = new NxCanvas
            {
                Buffer = new Bitmap(width, height)
            };
            using (var g = Graphics.FromImage(canvas.Buffer)) g.Clear(Color.Black);

            canvas.Surface = new PictureBox
            {
                Width = width,
                Height = height,
                Image = canvas.Buffer,
                SizeMode = PictureBoxSizeMode.Normal
            };

            canvas.Form = new Form
            {
                Text = title,
                ClientSize = new Size(width, height),
                FormBorderStyle = FormBorderStyle.FixedSingle,
                MaximizeBox = false,
                StartPosition = FormStartPosition.CenterScreen
            };
            canvas.Form.Controls.Add(canvas.Surface);

            canvas.Form.KeyPreview = true;
            canvas.Form.KeyDown += (s, e) => canvas.KeysDown.Add(e.KeyCode);
            canvas.Form.KeyUp += (s, e) => canvas.KeysDown.Remove(e.KeyCode);
            canvas.Surface.MouseDown += (s, e) => canvas.MouseDown = true;
            canvas.Surface.MouseUp += (s, e) => canvas.MouseDown = false;
            canvas.Surface.MouseMove += (s, e) => { canvas.MouseX = e.X; canvas.MouseY = e.Y; };

            canvas.Form.Show();
            canvas.Form.Activate();
            return canvas;
        };

        registry["clearCanvas"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            using var g = Graphics.FromImage(canvas.Buffer);
            g.Clear(ColorFromArgs(args, 1));
            return null;
        };

        registry["drawRect"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            using var g = Graphics.FromImage(canvas.Buffer);
            using var brush = new SolidBrush(ColorFromArgs(args, 5));
            g.FillRectangle(brush, Convert.ToInt32(args[1]), Convert.ToInt32(args[2]), Convert.ToInt32(args[3]), Convert.ToInt32(args[4]));
            return null;
        };

        registry["drawCircle"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            int x = Convert.ToInt32(args[1]);
            int y = Convert.ToInt32(args[2]);
            int radius = Convert.ToInt32(args[3]);
            using var g = Graphics.FromImage(canvas.Buffer);
            using var brush = new SolidBrush(ColorFromArgs(args, 4));
            g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);
            return null;
        };

        registry["drawLine"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            using var g = Graphics.FromImage(canvas.Buffer);
            using var pen = new Pen(ColorFromArgs(args, 5), 2);
            g.DrawLine(pen, Convert.ToInt32(args[1]), Convert.ToInt32(args[2]), Convert.ToInt32(args[3]), Convert.ToInt32(args[4]));
            return null;
        };

        registry["drawText"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            string text = args[1]?.ToString() ?? "";
            int x = Convert.ToInt32(args[2]);
            int y = Convert.ToInt32(args[3]);
            int size = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var color = args.Length > 7 ? ColorFromArgs(args, 5) : Color.White;
            using var g = Graphics.FromImage(canvas.Buffer);
            using var font = new Font("Arial", size);
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, x, y);
            return null;
        };

        // loadImage/drawImage/playSound (Крок A плану NyxilumEngine,
        // 22.09.2026) - передумова рушія: 2D-графіка вже вміла лише
        // суцільні фігури (drawRect/drawCircle/...), жодних зображень/
        // звуку не було зовсім. loadImage кешує за ШЛЯХОМ (_imageCache
        // вище) - повторний виклик того самого шляху НЕ читає диск.
        registry["loadImage"] = args =>
        {
            string path = args[0]?.ToString() ?? "";
            if (_imageCache.TryGetValue(path, out var cached)) return cached;
            var img = Image.FromFile(path);
            _imageCache[path] = img;
            return img;
        };

        registry["drawImage"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            var img = (Image)args[1];
            int x = Convert.ToInt32(args[2]);
            int y = Convert.ToInt32(args[3]);
            using var g = Graphics.FromImage(canvas.Buffer);
            if (args.Length > 5)
            {
                int w = Convert.ToInt32(args[4]);
                int h = Convert.ToInt32(args[5]);
                g.DrawImage(img, x, y, w, h);
            }
            else
            {
                g.DrawImage(img, x, y);
            }
            return null;
        };

        // playSound - лише WAV (обмеження самого SoundPlayer, частини
        // .NET - жодної нової залежності, той самий "нуль зовнішніх
        // залежностей" принцип, що вже тримає весь нативний компілятор)
        // - чесно задокументовано, не приховано. Play() (не PlaySync())
        // - асинхронно, щоб НЕ блокувати ігровий цикл на час звучання -
        // САМЕ тому НЕ `using`: Play() відтворює у ФОНОВОМУ потоці, і
        // Dispose() одразу після повернення з цієї функції перервав би
        // відтворення на середині (реальний, а не гіпотетичний баг,
        // спійманий ДО живого тесту - `using var` + асинхронний Play()
        // - класична пастка). SoundPlayer лишається жити до збирання
        // сміттям - прийнятний компроміс для простої першої версії.
        registry["playSound"] = args =>
        {
            string path = args[0]?.ToString() ?? "";
            var player = new SoundPlayer(path);
            player.Play();
            return null;
        };

        // presentCanvas/closeCanvas — на відміну від draw*/clear* (ті лише
        // малюють у власний Bitmap canvas.Buffer, не чіпаючи Control), тут
        // реальні виклики на Form/PictureBox (Invalidate/DoEvents/Close) —
        // саме ті, що вимагають виконання з того самого потоку, що створив
        // вікно. GuiThreadGuard ловить виклик з воркера (spawn) одразу.
        registry["presentCanvas"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            if (!canvas.Form.IsDisposed)
            {
                GuiThreadGuard.Ensure(canvas.Form);
                canvas.Surface.Invalidate();
                Application.DoEvents();
            }
            return null;
        };

        registry["canvasShouldClose"] = args => ((NxCanvas)args[0]).Form.IsDisposed;

        registry["closeCanvas"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            if (!canvas.Form.IsDisposed)
            {
                GuiThreadGuard.Ensure(canvas.Form);
                canvas.Form.Close();
            }
            return null;
        };

        registry["isKeyDown"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            string keyName = args[1]?.ToString() ?? "";
            return Enum.TryParse<Keys>(keyName, true, out var key) && canvas.KeysDown.Contains(key);
        };

        // 3D: проста перспективна проекція точки (x,y,z) у 2D-координати
        // екрана. Обертання/математику 3D сам NyxilumLang-скрипт може робити
        // через вже наявні sin/cos - тут потрібен лише місток "3D -> екран".
        registry["project3D"] = args =>
        {
            var canvas = (NxCanvas)args[0];
            double x = Convert.ToDouble(args[1]);
            double y = Convert.ToDouble(args[2]);
            double z = Convert.ToDouble(args[3]);
            double camDistance = args.Length > 4 ? Convert.ToDouble(args[4]) : 5.0;

            double denom = camDistance + z;
            if (denom < 0.001) denom = 0.001;
            double scale = camDistance / denom;

            double screenX = canvas.Buffer.Width / 2.0 + x * scale * (canvas.Buffer.Width / 4.0);
            double screenY = canvas.Buffer.Height / 2.0 - y * scale * (canvas.Buffer.Height / 4.0);

            return new List<object> { screenX, screenY, scale };
        };

        registry["isMouseDown"] = args => ((NxCanvas)args[0]).MouseDown;
        registry["getMouseX"] = args => (double)((NxCanvas)args[0]).MouseX;
        registry["getMouseY"] = args => (double)((NxCanvas)args[0]).MouseY;
    }

    private static Color ColorFromArgs(object[] args, int startIdx)
    {
        int r = Math.Clamp(Convert.ToInt32(args[startIdx]), 0, 255);
        int g = Math.Clamp(Convert.ToInt32(args[startIdx + 1]), 0, 255);
        int b = Math.Clamp(Convert.ToInt32(args[startIdx + 2]), 0, 255);
        return Color.FromArgb(r, g, b);
    }
}
#endif
