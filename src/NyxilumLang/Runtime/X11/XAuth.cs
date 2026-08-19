namespace NyxilumLang.Runtime.X11;

// Портовано з rawgui/src/xauth.js (той самий проєкт автора, вже перевірений
// на реальному X-сервері) - читання ~/.Xauthority чи $XAUTHORITY.
// Формат (велика ендіанність, послідовність записів до кінця файлу):
//   family(2B) | addrLen(2B) | addr(addrLen) | numberLen(2B) |
//   number(numberLen, ASCII номер дисплея) | nameLen(2B) |
//   name(nameLen, напр. "MIT-MAGIC-COOKIE-1") | dataLen(2B) | data(dataLen)
// Офіційного RFC нема - формат задокументований лише в коді самого Xlib
// (Xau.c), тому розбирається напряму з байтів.
public static class XAuth
{
    public sealed class Cookie
    {
        public string Name = "";
        public byte[] Data = System.Array.Empty<byte>();
    }

    private sealed class Entry
    {
        public string Number = "";
        public string Name = "";
        public byte[] Data = System.Array.Empty<byte>();
    }

    private static System.Collections.Generic.List<Entry> ReadEntries(byte[] buffer)
    {
        var entries = new System.Collections.Generic.List<Entry>();
        int offset = 0;
        ushort U16()
        {
            ushort v = (ushort)((buffer[offset] << 8) | buffer[offset + 1]); // велика ендіанність
            offset += 2;
            return v;
        }
        byte[] Bytes(int len)
        {
            var v = new byte[len];
            System.Array.Copy(buffer, offset, v, 0, len);
            offset += len;
            return v;
        }

        while (offset < buffer.Length)
        {
            U16(); // family - не використовується
            int addrLen = U16();
            Bytes(addrLen);
            int numberLen = U16();
            string number = System.Text.Encoding.ASCII.GetString(Bytes(numberLen));
            int nameLen = U16();
            string name = System.Text.Encoding.ASCII.GetString(Bytes(nameLen));
            int dataLen = U16();
            byte[] data = Bytes(dataLen);
            entries.Add(new Entry { Number = number, Name = name, Data = data });
        }
        return entries;
    }

    // displayNum - номер з $DISPLAY (":0" -> "0", ":0.1" -> "0" - частина
    // після крапки це номер екрана, не дисплея, для автентифікації неважлива).
    public static Cookie GetAuthCookie(string displayNum)
    {
        var authFile = System.Environment.GetEnvironmentVariable("XAUTHORITY")
            ?? System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".Xauthority");
        var buffer = System.IO.File.ReadAllBytes(authFile);
        var entries = ReadEntries(buffer);

        // Спершу шукаємо точний збіг номера дисплея з MIT-MAGIC-COOKIE-1 -
        // єдиний механізм автентифікації, який тут реалізовано (найпоширеніший,
        // XWayland теж його використовує). Порожній number - "будь-який
        // дисплей" (типово для персональних auth-файлів XWayland/mutter).
        var entry = entries.Find(e => e.Name == "MIT-MAGIC-COOKIE-1" && (e.Number == "" || e.Number == displayNum));
        if (entry == null)
            throw new System.Exception($"MIT-MAGIC-COOKIE-1 для дисплея {displayNum} не знайдено в {authFile}");
        return new Cookie { Name = entry.Name, Data = entry.Data };
    }
}
