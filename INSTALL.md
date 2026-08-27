# Installing NyxilumLang

*[Українською](INSTALL.uk.md)*

Two ways. The first needs no .NET at all. The second is for working with the
language's own source code. Both work the same way on Windows, Linux, and Mac.

## Method 1 — prebuilt release (recommended)

**Step 1.** Open the [NyxilumNode releases page](https://github.com/Faneraiy14/NyxilumNode/releases/latest)
and download the archive for your platform: `Nx-win-x64.zip`, `Nx-linux-x64.tar.gz`,
`Nx-osx-x64.tar.gz`, or `Nx-osx-arm64.tar.gz`.

**Step 2.** Extract the archive into any folder.

- **Windows:** right-click the archive → "Extract All".
- **Linux/Mac:** `tar xzf Nx-<platform>.tar.gz`.

Inside — the binary (`Nx.exe` on Windows, `Nx` on Linux/Mac), an install
script, and a README.

**Step 3.** Run the install script from that same folder.

**Windows** — right-click `install-nx.ps1` → **"Run with PowerShell"**. If
PowerShell shows a warning about running scripts, that's normal behavior for
files downloaded from the internet, not an error:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

then run `install-nx.ps1` again.

**Linux/Mac** — in a terminal, from the same folder:

```bash
bash install-nx.sh
```

**Step 4.** Close the terminal and open a **new** one — PATH only refreshes
for new windows (on Linux/Mac, `source ~/.bashrc` or `source ~/.zshrc` is
enough).

**Step 5.** Verify:

```bash
nx --version
```

Should reply `Nx v1.0.0`. If it did — you're done,
`nx myprogram.nx` works from any folder.

### If Windows shows "Windows protected your PC"

`Nx.exe` isn't signed with a paid certificate, so SmartScreen may flag it on
first run. Click the small **"More info"** link at the top of the window,
then **"Run anyway"**.

## Method 2 — from source

Requires the [.NET SDK 10](https://dotnet.microsoft.com/download) — the SDK
itself is cross-platform and installs the same way on Windows, Linux, and Mac.

```bash
git clone https://github.com/Faneraiy14/NyxilumLang.git
cd NyxilumLang
dotnet build src/NyxilumLang -f net10.0 -p:EnableWindowsTargeting=true
```

(The csproj multi-targets `net10.0-windows;net10.0` — on Linux/Mac, without
`-f net10.0 -p:EnableWindowsTargeting=true` the build fails with NETSDK1100.)

From here — the same install script as in Method 1 sits right in the
repository root and finds the freshly built binary on its own:

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File install-nx.ps1
```

```bash
# Linux/Mac
bash install-nx.sh
```

Then the same as before — a new terminal window, `nx --version`.

Rebuild the project (`dotnet build`) and the `nx` command picks up the new
version automatically, no need to rerun the install script.

GUI (`guiWindow`, etc.) and graphics (`createCanvas`, etc.) only work on
Windows — they run on Windows Forms, which doesn't exist outside Windows.
The rest of the language (compiler, VM, almost the entire standard library)
works the same on all three platforms.

## Your first program

Create a file `hello.nx`:

```nx
func main() {
    print("Hello, NyxilumLang!")
}
```

Run it:

```bash
nx hello.nx
```

## Libraries

```bash
nx install owner/repo
```

Pulls a public GitHub repo that has `main.nx` at its root. Details in the
["Package Manager"](README.md#package-manager) section of the main README.

## If something's not working

| Problem | Cause |
|---|---|
| `nx` isn't recognized as a command | The terminal was opened before installing — close it and open a new one |
| `dotnet build` says the SDK wasn't found | .NET SDK isn't installed, or the terminal needs to be restarted after installing it |
| SmartScreen blocks the run (Windows) | Normal for unsigned `.exe` files — "More info" → "Run anyway" |
| The script refuses to run in PowerShell | `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`, then try again |
| `bash install-nx.sh` says "not found" | The binary isn't next to the script and wasn't built — see Method 1 step 2 or Method 2 |
