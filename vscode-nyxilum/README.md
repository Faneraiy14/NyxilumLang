# Nyxilum for VS Code

*[Українською](README.uk.md)*

Syntax highlighting for `.nx` files: keywords (`func`, `var`, `struct`,
`if`/`else`, `while`, `for`/`in`, `try`/`catch`/`throw`, `import`, `break`/`continue`),
types (`i32`, `f64`, `string`, `bool`), strings, numbers, comments (`//` and `/* */`),
function calls, and struct names before `{`.

Autocomplete: keywords, all built-in functions, and symbols
(`func`/`struct`/`var`) from the currently open document.

Syntax-error diagnostics while you type: underlines the location and shows
the parser's message (unclosed bracket, unknown symbol, etc.) with a small
delay after you pause typing. Requires `nx` in PATH (installed together with
the language — see [INSTALL.md](../INSTALL.md)); it calls
`nx check <temp_file>` — Lexer+Parser only, no code execution
(unlike running the file directly, this is safe to do on every pause while
typing, even while the text isn't finished yet).

## Local testing (without publishing)

1. Open the `vscode-nyxilum` folder in VS Code.
2. Press `F5` — a new window ("Extension Development Host") opens with the
   extension active. Open any `.nx` file from `tests/` in it.

## Packaging into a `.vsix` (to install it yourself without the Marketplace)

```bash
npm install -g @vscode/vsce
vsce package
code --install-extension nyxilum-0.4.0.vsix
```

Publishing to the official VS Code Marketplace is a separate step (requires
a Publisher account at https://marketplace.visualstudio.com) — not done
automatically here.
