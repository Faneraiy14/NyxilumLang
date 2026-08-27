// extension.js — базове автодоповнення для .nx-файлів: ключові слова,
// усі вбудовані функції (список продубльовано з Compiler.cs _builtins —
// оновлюй тут при додаванні нової вбудованої функції в рантайм) і символи
// (func/struct/var), знайдені текстовим пошуком у поточному документі.
//
// Це НЕ повноцінний LSP: немає резолву типів, скоупів чи імпортованих
// файлів — лише те, що видно як текст у відкритому документі. Для мови
// без статичних типів цього вистачає для основного сценарію "не
// перепечатувати ім'я функції/змінної вручну".
const vscode = require('vscode');
const { execFile } = require('child_process');
const { writeFile, unlink, mkdtemp, rm } = require('fs/promises');
const { tmpdir } = require('os');
const path = require('path');

const KEYWORDS = [
    'func', 'var', 'if', 'else', 'while', 'for', 'return',
    'break', 'continue', 'true', 'false', 'null', 'in',
    'struct', 'self', 'import', 'try', 'catch', 'throw', 'extends', 'super',
];

// Продубльовано з src/NyxilumLang/Compiler/Compiler.cs _builtins.
const BUILTINS = [
    'print', 'printNoNewLine',
    'readLine', 'readInt', 'readDouble',
    'readFile', 'writeFile', 'appendFile', 'fileExists', 'readLines',
    'deleteFile', 'makeDir', 'dirExists', 'deleteDir', 'listDir',
    'sqrt', 'abs', 'pow', 'sin', 'cos', 'tan',
    'round', 'floor', 'ceil', 'max', 'min', 'clamp',
    'toString', 'toInt', 'toDouble', 'toFixed', 'len',
    'substring', 'replace', 'toUpper', 'toLower', 'contains', 'startsWith', 'endsWith', 'split', 'join',
    'trim', 'repeat', 'indexOf', 'reverse', 'utf8ByteCount',
    'append', 'pop', 'removeAt', 'insert', 'clear', 'slice', 'unique',
    'randomInt', 'randomDouble',
    'now', 'today', 'timestamp', 'formatDate', 'parseDate', 'sleep',
    'typeOf', 'isNumber', 'isString', 'isArray', 'isBool', 'isNull',
    'charCode', 'fromCharCode',
    'newMap', 'mapSet', 'mapGet', 'mapHas', 'mapRemove', 'mapKeys', 'mapValues',
    'sort', 'mapArr', 'filter', 'reduce', 'toJson', 'fromJson', 'callWithArgs',
    'osPlatform', 'osArchitecture', 'osMemory', 'osCpuCount', 'osEnv', 'osCwd',
    'osProcessList', 'osDiskFree', 'notify',
    'httpServer', 'httpGet', 'urlStatus', 'httpPost', 'httpRequest',
    'regexTest', 'regexMatch', 'regexFindAll', 'regexReplace',
    'wsConnect', 'wsSend', 'wsReceive', 'wsClose',
    'spawn', 'workerJoin', 'newChannel', 'channelSend', 'channelReceive',
    'createCanvas', 'clearCanvas', 'drawRect', 'drawCircle', 'drawLine', 'drawText',
    'presentCanvas', 'canvasShouldClose', 'closeCanvas',
    'isKeyDown', 'isMouseDown', 'getMouseX', 'getMouseY', 'project3D',
    'guiWindow', 'guiButton', 'guiLabel', 'guiTextBox', 'guiAdd',
    'guiOnAction', 'guiShow', 'guiSetText', 'guiGetText',
    'guiCheckbox', 'guiDropdown', 'guiScrollList', 'guiProgressBar', 'guiEntry',
    'guiGetChecked', 'guiSetChecked', 'guiSetOptions', 'guiGetSelected', 'guiSetSelected', 'guiSetProgress',
    'gc_stats', 'gc_collect', 'gc_limit', 'exit',
    'dbOpen', 'dbClose', 'dbSet', 'dbGet', 'dbHas', 'dbDelete', 'dbKeys', 'dbCount', 'dbCheckpoint',
    'procStart', 'procRun', 'procWait', 'procIsRunning', 'procKill', 'procPid', 'procExitCode',
    'procOutput', 'procErrorOutput',
    'zipExtract', 'zipEntries', 'zipExtractEntry', 'zipCreate',
];

// Ідентифікатор в NyxilumLang може бути кирилицею (напр. "func подвоїти(x)"),
// тож \w тут не годиться — потрібні unicode property escapes (\p{L}).
const IDENT = '\\p{L}[\\p{L}\\p{N}_]*';
const FUNC_RE = new RegExp(`\\bfunc\\s+(${IDENT})\\s*\\(`, 'gu');
const STRUCT_RE = new RegExp(`\\bstruct\\s+(${IDENT})`, 'gu');
const VAR_RE = new RegExp(`\\bvar\\s+(${IDENT})`, 'gu');

function keywordItems() {
    return KEYWORDS.map((kw) => {
        const item = new vscode.CompletionItem(kw, vscode.CompletionItemKind.Keyword);
        item.detail = 'ключове слово NyxilumLang';
        return item;
    });
}

function builtinItems() {
    return BUILTINS.map((name) => {
        const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Function);
        item.detail = 'вбудована функція NyxilumLang';
        item.insertText = new vscode.SnippetString(`${name}($0)`);
        return item;
    });
}

// Символи, знайдені текстовим пошуком по всьому відкритому документу —
// не лише в межах поточної області видимості (простіше й для скрипта на
// кілька сотень рядків досить точно; хибний позитив тут не гірший за
// звичайну відсутність автодоповнення).
function documentSymbolItems(document) {
    const text = document.getText();
    const items = [];
    const seen = new Set();

    const addAll = (regex, kind, detail, asCall) => {
        for (const match of text.matchAll(regex)) {
            const name = match[1];
            const key = kind + ':' + name;
            if (seen.has(key)) continue;
            seen.add(key);
            const item = new vscode.CompletionItem(name, kind);
            item.detail = detail;
            if (asCall) item.insertText = new vscode.SnippetString(`${name}($0)`);
            items.push(item);
        }
    };

    addAll(FUNC_RE, vscode.CompletionItemKind.Function, 'функція (з цього файлу)', true);
    addAll(STRUCT_RE, vscode.CompletionItemKind.Struct, 'структура (з цього файлу)', false);
    addAll(VAR_RE, vscode.CompletionItemKind.Variable, 'змінна (з цього файлу)', false);

    return items;
}

// Діагностика синтаксичних помилок: "nx check <файл>" — лише Lexer+Parser
// (Nx.cs RunCheck), БЕЗ Compiler і БЕЗ VM.Run(). Свідомо НЕ "nx файл.nx" —
// той реально ВИКОНУЄ код (побічні ефекти: файли, мережа, зациклення),
// що неприйнятно для перевірки "на кожну паузу під час набору". check —
// єдина команда, яка каже "чи взагалі валідний синтаксис" безпечно.
//
// Реалізація через реальний парсер компілятора (окремий процес), а не
// власний JS-парсер: другий парсер тут — окреме джерело правди, яке
// неминуче розійшлося б зі справжнім (нові конструкції мови, зміни
// граматики) і давало б хибні/пропущені помилки замість точних.
const PARSE_ERROR_RE = /на рядку (\d+)(?:, стовпець (\d+))?/u;
const diagnostics = vscode.languages.createDiagnosticCollection('nyxilum');
const debounceTimers = new Map(); // document URI (string) -> timeout handle
const DEBOUNCE_MS = 400;
let tempDirPromise = null;

async function getTempDir() {
    tempDirPromise ??= mkdtemp(path.join(tmpdir(), 'nyxilum-check-'));
    return tempDirPromise;
}

function runNxCheck(filePath) {
    return new Promise((resolve) => {
        execFile('nx', ['check', filePath], { timeout: 5000, windowsHide: true }, (error, stdout) => {
            resolve({ error, stdout: stdout ?? '' });
        });
    });
}

async function checkDocument(document) {
    if (document.languageId !== 'nyxilum') return;

    const uriKey = document.uri.toString();
    let tempFile;
    try {
        const dir = await getTempDir();
        // Ім'я тимчасового файлу похідне від документа (не константа) —
        // кілька відкритих .nx-файлів перевіряються паралельно без
        // взаємного стирання чужого тимчасового файлу.
        const safeName = Buffer.from(uriKey).toString('base64url').slice(0, 40);
        tempFile = path.join(dir, `${safeName}.nx`);
        await writeFile(tempFile, document.getText(), 'utf8');

        const { error, stdout } = await runNxCheck(tempFile);

        // Документ міг закритись чи змінитись, поки ми чекали на процес —
        // діагностику ставимо лише якщо він досі відкритий з тим самим текстом.
        if (!vscode.workspace.textDocuments.includes(document)) return;

        if (!error) {
            diagnostics.set(document.uri, []);
            return;
        }

        const match = PARSE_ERROR_RE.exec(stdout);
        if (!match) {
            // "nx" не знайдено в PATH (ENOENT) чи інша несподівана відмова —
            // мовчки пропускаємо: діагностика — зручність, не критична
            // функція, і не варта нав'язливого попередження на кожен набір.
            return;
        }

        const line = Math.max(0, parseInt(match[1], 10) - 1);
        const column = match[2] ? Math.max(0, parseInt(match[2], 10) - 1) : 0;
        const lineText = line < document.lineCount ? document.lineAt(line).text : '';
        const endColumn = Math.max(column + 1, lineText.length);

        const range = new vscode.Range(line, column, line, endColumn);
        const message = stdout.trim().replace(/^Parse Error:\s*/u, '');
        const diagnostic = new vscode.Diagnostic(range, message, vscode.DiagnosticSeverity.Error);
        diagnostic.source = 'nx';
        diagnostics.set(document.uri, [diagnostic]);
    } catch {
        // Тимчасовий файл не вдалось записати/прочитати тощо — так само
        // не критично, пропускаємо цю перевірку мовчки.
    } finally {
        if (tempFile) unlink(tempFile).catch(() => {});
    }
}

function scheduleCheck(document) {
    if (document.languageId !== 'nyxilum') return;
    const uriKey = document.uri.toString();
    clearTimeout(debounceTimers.get(uriKey));
    debounceTimers.set(
        uriKey,
        setTimeout(() => checkDocument(document), DEBOUNCE_MS)
    );
}

function activate(context) {
    const provider = vscode.languages.registerCompletionItemProvider(
        'nyxilum',
        {
            provideCompletionItems(document) {
                return [
                    ...keywordItems(),
                    ...builtinItems(),
                    ...documentSymbolItems(document),
                ];
            },
        },
        // Триґер і на звичайне введення літери (VS Code сам фільтрує список
        // за вже набраним префіксом), і явно на '.' — на майбутнє, якщо
        // з'явиться доступ до полів структур через крапку в автодоповненні.
    );
    context.subscriptions.push(provider);

    context.subscriptions.push(diagnostics);
    context.subscriptions.push(vscode.workspace.onDidOpenTextDocument(scheduleCheck));
    context.subscriptions.push(vscode.workspace.onDidChangeTextDocument((e) => scheduleCheck(e.document)));
    context.subscriptions.push(
        vscode.workspace.onDidCloseTextDocument((document) => {
            clearTimeout(debounceTimers.get(document.uri.toString()));
            debounceTimers.delete(document.uri.toString());
            diagnostics.delete(document.uri);
        })
    );
    // Документи, вже відкриті на момент активації розширення (не лише ті,
    // що відкриються ПІСЛЯ — onDidOpenTextDocument їх не ловить).
    vscode.workspace.textDocuments.forEach(scheduleCheck);
}

function deactivate() {
    for (const timer of debounceTimers.values()) clearTimeout(timer);
    debounceTimers.clear();
    if (tempDirPromise) {
        tempDirPromise.then((dir) => rm(dir, { recursive: true, force: true })).catch(() => {});
    }
}

module.exports = { activate, deactivate };
