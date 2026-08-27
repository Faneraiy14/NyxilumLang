# ============================================================
# install-nx.ps1 — глобальна команда "nx"
#
# Після встановлення .nx-файли можна запускати з будь-якої папки:
#     nx myprogram.nx
#
# Запуск (з клону репозиторію, потрібен .NET SDK):
#     powershell -ExecutionPolicy Bypass -File install-nx.ps1
#
# На чужому ПК без .NET і без клону репозиторію — качай самодостатній
# Nx.exe зі сторінки Releases репозитрію й запускай install-nx.ps1
# поруч із ним: скрипт знайде .exe в тій же папці, дотнет не знадобиться.
# ============================================================

$ErrorActionPreference = "Stop"

# Подвійний клік -> "Запустити за допомогою PowerShell" відкриває НОВЕ вікно,
# яке саме закривається одразу після завершення скрипта - і успіх, і крах
# виглядають однаково як "мигнуло й зникло", жодного повідомлення прочитати
# не встигаєш. try/catch + Read-Host в кінці тримають вікно відкритим у
# ОБОХ випадках, поки хтось сам не натисне Enter.
try {

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Спершу шукаємо вже готовий .exe поруч зі скриптом (так буде на чужому ПК,
# що скачав реліз) або в build-теці (так буде в клоні репозиторію розробника).
$candidates = @(
    (Join-Path $scriptDir "Nx.exe"),
    (Join-Path $scriptDir "src\NyxilumLang\bin\Release\net10.0-windows\win-x64\publish\Nx.exe"),
    (Join-Path $scriptDir "src\NyxilumLang\bin\Debug\net10.0-windows\Nx.exe")
)
$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $exe) {
    Write-Host "Nx.exe не знайдено поруч зі скриптом і не зібрано локально." -ForegroundColor Red
    Write-Host "Або поклади Nx.exe (з Releases) у цю ж папку, або зберіть проєкт:"
    Write-Host "    dotnet build src\NyxilumLang"
    throw "Nx.exe не знайдено"
}
Write-Host "Знайдено: $exe"

$binDir = Join-Path $HOME "bin"
if (-not (Test-Path $binDir)) {
    New-Item -ItemType Directory -Path $binDir | Out-Null
    Write-Host "Створено папку $binDir"
}

# .cmd, а не .ps1 — так команда працює і в cmd, і в PowerShell.
# %* передає всі аргументи далі (ім'я файлу тощо).
$cmdPath = Join-Path $binDir "nx.cmd"
$content = "@echo off`r`n`"$exe`" %*"
Set-Content -Path $cmdPath -Value $content -Encoding ascii
Write-Host "Створено $cmdPath"

# PATH користувача, не системний — прав адміністратора не потрібно.
$userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($userPath -notlike "*$binDir*") {
    [Environment]::SetEnvironmentVariable("PATH", "$userPath;$binDir", "User")
    Write-Host "Додано $binDir до PATH"
} else {
    Write-Host "$binDir вже є в PATH"
}

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
Write-Host "Відкрий НОВЕ вікно термінала (PATH оновлюється лише в нових) і спробуй:"
Write-Host ""
Write-Host "    nx --version" -ForegroundColor Cyan
Write-Host ""
Write-Host "Якщо перезбереш проєкт — команда підхопить нову збірку сама,"
Write-Host "бо посилається на ту саму папку."

# ---- VS Code розширення (опційно) ----
# Той самий .vsix можна поставити й окремо в будь-який момент (папка
# vscode-nyxilum в репозиторії) — тут лише зручний бонус, коли розширення
# лежить поруч у релізі, а на комп'ютері вже є VS Code. Нічого критичного:
# якщо VS Code нема чи щось піде не так, встановлення nx це не зупиняє.
$vsix = Get-ChildItem -Path $scriptDir -Filter "*.vsix" -ErrorAction SilentlyContinue | Select-Object -First 1
$codeCmd = Get-Command code -ErrorAction SilentlyContinue

if ($vsix -and $codeCmd) {
    Write-Host ""
    Write-Host "Знайдено VS Code — ставлю розширення підсвічування NyxilumLang..."
    try {
        & code --install-extension $vsix.FullName | Out-Null
        Write-Host "Розширення NyxilumLang для VS Code встановлено." -ForegroundColor Green
    } catch {
        Write-Host "Не вдалось встановити розширення автоматично: $_" -ForegroundColor Yellow
        Write-Host "Постав вручну: code --install-extension `"$($vsix.FullName)`""
    }
} elseif ($vsix -and -not $codeCmd) {
    Write-Host ""
    Write-Host "VS Code не знайдено в PATH — пропускаю автоматичне встановлення розширення."
    Write-Host "Якщо поставиш VS Code пізніше: code --install-extension `"$($vsix.FullName)`""
}

} catch {
    Write-Host ""
    Write-Host "Сталася помилка: $_" -ForegroundColor Red
} finally {
    Write-Host ""
    Read-Host "Натисни Enter, щоб закрити це вікно"
}
