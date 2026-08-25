# Встановлення NyxilumLang

Два способи. Перший — для будь-кого, .NET не потрібен. Другий — якщо хочеш
працювати з вихідним кодом самої мови. Обидва працюють однаково на Windows,
Linux і Mac.

## Спосіб 1 — готовий реліз (рекомендовано)

**Крок 1.** Відкрий сторінку [релізів NyxilumNode](https://github.com/Faneraiy14/NyxilumNode/releases/latest)
і скачай архів для своєї платформи: `Nx-win-x64.zip`, `Nx-linux-x64.tar.gz`,
`Nx-osx-x64.tar.gz` або `Nx-osx-arm64.tar.gz`.

**Крок 2.** Розпакуй архів у будь-яку папку.

- **Windows:** правою кнопкою на архіві → «Видобути все».
- **Linux/Mac:** `tar xzf Nx-<платформа>.tar.gz`.

Усередині — бінарник (`Nx.exe` на Windows, `Nx` на Linux/Mac),
скрипт встановлення й README.

**Крок 3.** Запусти скрипт встановлення з тієї ж папки.

**Windows** — клацни правою на `install-nx.ps1` → **«Запустити за допомогою
PowerShell»**. Якщо PowerShell покаже попередження про виконання скриптів —
це нормальна поведінка для файлів, скачаних з інтернету, не помилка:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

і запусти `install-nx.ps1` ще раз.

**Linux/Mac** — у терміналі з тієї ж папки:

```bash
bash install-nx.sh
```

**Крок 4.** Закрий термінал і відкрий **новий** — PATH оновлюється лише для
нових вікон (на Linux/Mac достатньо `source ~/.bashrc` чи `source ~/.zshrc`).

**Крок 5.** Перевір:

```bash
nx --version
```

Має відповісти `Nx v1.0.0`. Якщо відповіло —
готово, `nx myprogram.nx` працює з будь-якої папки.

### Якщо Windows показала «Windows захистила ваш ПК»

`Nx.exe` не підписаний платним сертифікатом, тому SmartScreen може
насторожитись при першому запуску. Натисни дрібне посилання **«Докладніше»**
зверху вікна, потім **«Виконати все одно»**.

## Спосіб 2 — з вихідного коду

Потрібен [.NET SDK 10](https://dotnet.microsoft.com/download) — сам SDK
крос-платформний, встановлюється так само на Windows, Linux і Mac.

```bash
git clone https://github.com/Faneraiy14/NyxilumLang.git
cd NyxilumLang
dotnet build src/NyxilumLang -f net10.0 -p:EnableWindowsTargeting=true
```

(csproj мультитаргетить `net10.0-windows;net10.0` — на Linux/Mac без
`-f net10.0 -p:EnableWindowsTargeting=true` збірка впаде з NETSDK1100.)

Далі — той самий скрипт встановлення, що й у Способі 1, лежить прямо в
корені репозиторію й сам знаходить щойно зібраний бінарник:

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File install-nx.ps1
```

```bash
# Linux/Mac
bash install-nx.sh
```

Далі те саме — нове вікно термінала, `nx --version`.

Перезбереш проєкт (`dotnet build`) — команда `nx` підхопить нову версію
сама, без повторного запуску скрипта встановлення.

GUI (`guiWindow` тощо) і графіка (`createCanvas` тощо) працюють лише на
Windows — усередині Windows Forms, якого поза Windows не існує. Решта мови
(компілятор, VM, майже вся стандартна бібліотека) працює однаково на всіх
трьох платформах.

## Перша програма

Створи файл `hello.nx`:

```nx
func main() {
    print("Привіт, NyxilumLang!")
}
```

Запусти:

```bash
nx hello.nx
```

## Бібліотеки

```bash
nx install owner/repo
```

Тягне публічний GitHub-репозиторій із `main.nx` у корені. Подробиці —
у розділі [«Менеджер пакетів»](README.md#менеджер-пакетів) головного README.

## Якщо щось не працює

| Проблема | Причина |
|---|---|
| `nx` не розпізнається як команда | Термінал відкрито до встановлення — закрий і відкрий новий |
| `dotnet build` каже, що SDK не знайдено | .NET SDK не встановлений або треба перезапустити термінал після встановлення |
| SmartScreen блокує запуск (Windows) | Нормально для непідписаних `.exe` — «Докладніше» → «Виконати все одно» |
| Скрипт відмовляється запускатись у PowerShell | `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`, потім спробуй ще раз |
| `bash install-nx.sh` каже "не знайдено" | Бінарник не поруч зі скриптом і не зібраний — див. Спосіб 1 крок 2 або Спосіб 2 |
