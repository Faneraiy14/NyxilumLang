#!/bin/bash
# ============================================================
# run_all.sh — прогін усіх тестів NyxilumLang
#
# Запуск:  bash tests/run_all.sh
#
# Тест вважається успішним, якщо він вивів очікуваний результат і не
# впав з непередбаченою помилкою. Тести, які НАВМИСНЕ перевіряють
# помилку (напр. необроблений throw), перелічені в EXPECT_ERROR —
# для них помилка це і є успіх, а її ВІДСУТНІСТЬ — провал.
# ============================================================

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE="${NX_EXE:-$TESTS_DIR/../src/NyxilumLang/bin/Debug/net10.0-windows/Nx.exe}"

# Відкривають вікна, слухають порт назавжди або чекають на ввід —
# у автоматичному прогоні пропускаємо. bench_loop.nx — навмисно повільний
# неформальний бенчмарк (десятки секунд), не тест коректності.
# test_sandbox_symlink.nx / test_db_sandbox_escape.nx — окремі блоки нижче
# (потребують NX_SANDBOX=1 і готують/прибирають теки/symlink навколо
# запуску, тут пройшли б без сенсу чи забруднили б диск поза sandbox).
SKIP="test_graphics2d.nx test_graphics3d.nx calculator.nx test_http_server.nx test_websocket_server.nx bench_loop.nx test_sandbox_symlink.nx test_db_sandbox_escape.nx ws_client_check.nx ws_timeout_recovery_check.nx test_ast_dump.nx"

# Тести, де помилка — очікуваний результат.
EXPECT_ERROR="test_throw_uncaught.nx test_nested_scope_error.nx test_selective_import_missing.nx test_lib_testing_fail.nx test_parser_stack_limits.nx"

TIMEOUT_SEC=25

cd "$TESTS_DIR" || exit 1

if [ ! -f "$EXE" ]; then
    echo "Не знайдено Nx.exe: $EXE"
    echo "Спочатку зберіть: dotnet build src/NyxilumLang"
    exit 1
fi

pass=0
fail=0
skip=0
failed=()

in_list() {
    case " $2 " in *" $1 "*) return 0;; *) return 1;; esac
}

for f in *.nx; do
    [ -e "$f" ] || continue

    if in_list "$f" "$SKIP"; then
        echo "⏭️  $f — пропущено (графіка/інтерактив)"
        skip=$((skip+1))
        continue
    fi

    out=$(timeout "$TIMEOUT_SEC" "$EXE" "$f" 2>&1)
    code=$?

    if echo "$out" | grep -qiE "Runtime Error|Parse Error|Unhandled exception"; then
        has_error=1
    else
        has_error=0
    fi

    if [ $code -eq 124 ]; then
        echo "⏱️  $f — таймаут (${TIMEOUT_SEC}с)"
        fail=$((fail+1)); failed+=("$f — таймаут")
        continue
    fi

    if in_list "$f" "$EXPECT_ERROR"; then
        if [ $has_error -eq 1 ]; then
            echo "✅ $f — помилка очікувана й отримана"
            pass=$((pass+1))
        else
            echo "❌ $f — мала бути помилка, але її немає"
            fail=$((fail+1)); failed+=("$f — очікувалась помилка")
        fi
        continue
    fi

    if [ $has_error -eq 1 ]; then
        echo "❌ $f"
        echo "$out" | grep -iE "Runtime Error|Parse Error|Unhandled exception" | head -2 | sed 's/^/       /'
        fail=$((fail+1)); failed+=("$f")
    else
        echo "✅ $f"
        pass=$((pass+1))
    fi
done

# Модульні тести лежать в окремій підпапці й запускаються через main.nx
if [ -f "modules/main.nx" ]; then
    out=$(cd modules && timeout "$TIMEOUT_SEC" "$EXE" main.nx 2>&1)
    if echo "$out" | grep -qiE "Runtime Error|Parse Error|Unhandled exception"; then
        echo "❌ modules/main.nx"
        fail=$((fail+1)); failed+=("modules/main.nx")
    else
        echo "✅ modules/main.nx"
        pass=$((pass+1))
    fi
fi

# JIT (superinstruction-оптимізація гарячих циклів, VirtualMachine.cs):
# найважливіша перевірка — вивід З NX_JIT увімкненим (типово) і з
# NX_JIT=0 має бути ПОБАЙТОВО однаковим. Розбіжність означає, що
# superinstruction десь порушує семантику, а не просто прискорює.
if [ -f "test_jit_superops.nx" ]; then
    jit_on=$(timeout "$TIMEOUT_SEC" "$EXE" test_jit_superops.nx 2>&1)
    jit_off=$(timeout "$TIMEOUT_SEC" env NX_JIT=0 "$EXE" test_jit_superops.nx 2>&1)
    if [ "$jit_on" = "$jit_off" ]; then
        echo "✅ test_jit_superops.nx — NX_JIT=1 і NX_JIT=0 дають однаковий вивід"
        pass=$((pass+1))
    else
        echo "❌ test_jit_superops.nx — вивід NX_JIT=1 і NX_JIT=0 РІЗНИТЬСЯ"
        fail=$((fail+1)); failed+=("test_jit_superops.nx — розбіжність JIT on/off")
    fi
fi

# REPL: глобальна var і func, оголошені на одному рядку, мають лишатись
# видимими й зі своїм значенням на наступних рядках (VirtualMachine.Globals
# + Compiler.Compile(knownGlobals) в Nx.cs). print() при цьому не
# повинен повторюватись на кожному наступному рядку.
repl_out=$(printf 'var x = 5\nfunc double(n) { return n * 2 }\nprint(x)\nprint(double(x))\nexit()\n' | timeout "$TIMEOUT_SEC" "$EXE" 2>&1)
repl_clean=$(echo "$repl_out" | sed 's/> //g' | sed '/^Nx REPL\|^Type /d' | grep -v '^[[:space:]]*$')
if [ "$repl_clean" = "$(printf '5\n10')" ]; then
    echo "✅ REPL — var і func зберігаються між рядками, print не дублюється"
    pass=$((pass+1))
else
    echo "❌ REPL — стан між рядками не зберігається або вивід продублювався"
    echo "$repl_out" | sed 's/^/       /'
    fail=$((fail+1)); failed+=("REPL — персистентність між рядками")
fi

# Пісочниця (Sandbox.CheckPath): symlink усередині робочої директорії,
# що веде за її межі, раніше читався безперешкодно (Path.GetFullPath
# лише нормалізує ТЕКСТ шляху, symlink на диску не резолвить). Готуємо
# секретний файл ЗА межами tests/, symlink на нього ВСЕРЕДИНІ tests/,
# і перевіряємо, що NX_SANDBOX=1 коректно блокує читання крізь нього.
secret_file=$(mktemp)
echo "секрет-поза-sandbox" > "$secret_file"
ln -sf "$secret_file" escape_link.txt
sandbox_out=$(timeout "$TIMEOUT_SEC" env NX_SANDBOX=1 "$EXE" test_sandbox_symlink.nx 2>&1)
rm -f escape_link.txt "$secret_file"
if echo "$sandbox_out" | grep -q "Спіймано: OK" && ! echo "$sandbox_out" | grep -q "секрет-поза-sandbox"; then
    echo "✅ test_sandbox_symlink.nx — symlink-обхід пісочниці заблоковано"
    pass=$((pass+1))
else
    echo "❌ test_sandbox_symlink.nx — symlink дозволив вийти за межі пісочниці"
    echo "$sandbox_out" | sed 's/^/       /'
    fail=$((fail+1)); failed+=("test_sandbox_symlink.nx — обхід пісочниці через symlink")
fi

# dbOpen() раніше не проходив через Sandbox.CheckPath (на відміну від
# readFile/writeFile тощо) - NX_SANDBOX=1 не заважав відкрити/створити базу
# ЗА межами робочої директорії. Готуємо теку-ціль поза tests/, перевіряємо,
# що NX_SANDBOX=1 блокує dbOpen("../db_sandbox_escape_target/...") і що
# теку так і не створено.
db_escape_target="$TESTS_DIR/../db_sandbox_escape_target"
rm -rf "$db_escape_target"
db_sandbox_out=$(timeout "$TIMEOUT_SEC" env NX_SANDBOX=1 "$EXE" test_db_sandbox_escape.nx 2>&1)
db_escape_created=0
[ -e "$db_escape_target" ] && db_escape_created=1
rm -rf "$db_escape_target"
if echo "$db_sandbox_out" | grep -q "Спіймано: OK" && [ "$db_escape_created" -eq 0 ]; then
    echo "✅ test_db_sandbox_escape.nx — dbOpen() за межі робочої директорії заблоковано"
    pass=$((pass+1))
else
    echo "❌ test_db_sandbox_escape.nx — dbOpen() дозволив вийти за межі пісочниці"
    echo "$db_sandbox_out" | sed 's/^/       /'
    fail=$((fail+1)); failed+=("test_db_sandbox_escape.nx — dbOpen() обходить пісочницю")
fi

# PackageManager: ключ залежності з nx.json ("nx install") чи аргумент
# "nx uninstall"/"nx update" ішов прямо в Path.Combine(projectDir,
# nx_modules, name) без перевірки - ключ на кшталт ".." дозволяв
# Directory.Delete(targetDir, true) (InstallOne, ПЕРЕД розпаковкою) знести
# щось ЗА межами nx_modules/, аж до цілого projectDir. Живцем перевірено.
# Перевірка (ValidatePackageName) спрацьовує ще ДО мережевих запитів, тож
# тест не потребує інтернету.
pkg_work="$TESTS_DIR/../pkg_traversal_work"
pkg_canary="$TESTS_DIR/../pkg_traversal_canary.txt"
rm -rf "$pkg_work" "$pkg_canary"
mkdir -p "$pkg_work"
echo "canary" > "$pkg_canary"
cat > "$pkg_work/nx.json" <<'EOF'
{"dependencies": {"..": "octocat/Hello-World"}}
EOF
pkg_out=$(cd "$pkg_work" && timeout "$TIMEOUT_SEC" "$EXE" install 2>&1)
pkg_canary_survived=0
[ -f "$pkg_canary" ] && pkg_canary_survived=1
rm -rf "$pkg_work" "$pkg_canary"
if echo "$pkg_out" | grep -q "Неприпустиме ім'я пакета" && [ "$pkg_canary_survived" -eq 1 ]; then
    echo "✅ pkg_traversal — 'nx install' з '..' у nx.json відхилено, файли поза nx_modules/ не зачеплено"
    pass=$((pass+1))
else
    echo "❌ pkg_traversal — 'nx install' з '..' у nx.json НЕ відхилено належним чином"
    echo "$pkg_out" | sed 's/^/       /'
    fail=$((fail+1)); failed+=("pkg_traversal — обхід через ключ залежності '..'")
fi

# WebSocket-сервер (httpServer(port, handler, wsHandler)): піднімаємо
# test_websocket_server.nx у фоні, підключаємось справжнім WS-клієнтом
# (той самий wsConnect, що й на клієнтському боці) і перевіряємо, що
# echo реально доходить туди й назад - не лише що handshake не падає.
"$EXE" test_websocket_server.nx > /tmp/nx_ws_server.log 2>&1 &
ws_server_pid=$!
sleep 1
ws_client_out=$(timeout 8 "$EXE" ws_client_check.nx 2>&1)
if echo "$ws_client_out" | grep -q "REPLY:echo: перевірка"; then
    echo "✅ test_websocket_server.nx — WS-сервер: клієнт підключився й отримав echo"
    pass=$((pass+1))
else
    echo "❌ test_websocket_server.nx — WS-клієнт не отримав очікуваний echo"
    echo "$ws_client_out" | sed 's/^/       /'
    echo "--- лог сервера ---"
    cat /tmp/nx_ws_server.log | sed 's/^/       /'
    fail=$((fail+1)); failed+=("test_websocket_server.nx — WS round-trip не спрацював")
fi

# Регресія: wsReceive-тайм-аут раніше абортував сокет (CancellationToken
# на ReceiveAsync -> стан Aborted), тож усе ПІСЛЯ першого тайм-ауту падало.
# Той самий сервер вище ще живий - підключаємось повторно, навмисно ловимо
# тайм-аут першим, і перевіряємо, що з'єднання лишається робочим.
ws_recovery_out=$(timeout 8 "$EXE" ws_timeout_recovery_check.nx 2>&1)
kill "$ws_server_pid" 2>/dev/null
wait "$ws_server_pid" 2>/dev/null
if echo "$ws_recovery_out" | grep -q "REPLY:echo: після тайм-ауту" && ! echo "$ws_recovery_out" | grep -qi "НЕСПОДІВАНО\|invalid state\|Aborted"; then
    echo "✅ ws_timeout_recovery_check.nx — wsReceive-тайм-аут не ламає з'єднання"
    pass=$((pass+1))
else
    echo "❌ ws_timeout_recovery_check.nx — з'єднання не пережило тайм-аут"
    echo "$ws_recovery_out" | sed 's/^/       /'
    fail=$((fail+1)); failed+=("ws_timeout_recovery_check.nx — тайм-аут ламає сокет")
fi
rm -f /tmp/nx_ws_server.log

# "nx ast файл.nx" (AstJsonDumper.cs): AST у канонічній JSON-схемі, яку
# читає anylint (github.com/Faneraiy14/anylint) через NyxilumProvider.
# Перевіряємо форму, а не повний дамп - схема стабільна, конкретне дерево
# може вирости новими вузлами й це не має ламати цей тест.
ast_out=$(timeout "$TIMEOUT_SEC" "$EXE" ast test_ast_dump.nx 2>&1)
if echo "$ast_out" | grep -q '"type":"FunctionDecl","line":7,"attributes":{"name":"f"}' \
    && echo "$ast_out" | grep -q '"type":"Return"' \
    && echo "$ast_out" | grep -q '"type":"CatchClause".*"children":\[{"type":"Block","line":13,"attributes":{},"children":\[\]}\]'; then
    echo "✅ test_ast_dump.nx — 'nx ast' видає канонічний JSON (FunctionDecl/Return/порожній CatchClause)"
    pass=$((pass+1))
else
    echo "❌ test_ast_dump.nx — 'nx ast' видав щось несподіване"
    echo "$ast_out" | sed 's/^/       /'
    fail=$((fail+1)); failed+=("test_ast_dump.nx — форма AST-дампу зламана")
fi

echo
echo "======================================"
echo "Успішно: $pass | Провалено: $fail | Пропущено: $skip"

if [ ${#failed[@]} -gt 0 ]; then
    echo
    echo "Проблемні:"
    printf '  %s\n' "${failed[@]}"
    exit 1
fi

echo "Усі тести пройдено."
