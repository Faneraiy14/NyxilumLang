
# NyxilumLang: Посібник користувача

NyxilumLang - це потужна, але проста мова програмування, створена для навчання та швидкої розробки.

## Основний синтаксис

### Змінні
Використовуйте `var` для оголошення змінних.
```nx
var x = 10
var name = "Nyxilum"
var isCool = true
var nothing = null       // isNull(nothing) -> true
```

### Рядки та інтерполяція
Конкатенація через `+` працює з будь-яким типом — число/bool/null самі
приводяться до рядка:
```nx
var вік = 20
print("Мені " + вік + " років")   // Мені 20 років
```

Для складніших рядків зручніша інтерполяція `${вираз}` — усередині може
бути будь-який вираз NyxilumLang (змінна, арифметика, виклик функції):
```nx
var імя = "Аня"
var вік = 20
print("Привіт, ${імя}! Тобі ${вік} ${вік + 1 - 1} років.")
print("Подвоєне: ${вік * 2}")
```
Це не окрема сутність рантайму — компілятор розгортає `"a${b}c"` у
звичайне `"a" + b + "c"` ще на етапі лексера, тому працює скрізь, де й
`+`, без жодних обмежень.

Щоб `${` не вважався початком інтерполяції, а лишився звичайним
текстом — заекрануй символ долара: `\${`.
```nx
print("Ціна: \${не інтерполяція}")   // Ціна: ${не інтерполяція}
```

### Функції
Функції оголошуються за допомогою `func`. Точка входу - функція `main`.
```nx
func add(a, b) {
    return a + b
}

func main() {
    var result = add(5, 10)
    print("Результат: " + result)
}
```

`func` можна оголосити й усередині іншої функції — видима лише в межах
"батька" (лексично, як у більшості мов): однакова назва в різних
"батьках" не конфліктує, кожен викликає свою. Це не замикання — вкладена
функція не бачить локальних змінних "батька"; для захоплення оточення
потрібна анонімна лямбда (`var f = func() {...}`, розділ нижче).

```nx
func outer() {
    func inner(x) {
        return x * 2
    }
    return inner(21)
}
print(outer()) // 42
```

### Управляючі конструкції
```nx
if (x > 0) {
    print("Додатне")
} else {
    print("Від'ємне або нуль")
}

for i in 0..5 {
    print(i)
}

for item in [10, 20, 30] {
    print(item)     // ітерація по елементах масиву (без ручного індексу)
}

while (x > 0) {
    print(x)
    x = x - 1
}
```

### Структури та Методи
```nx
struct Point {
    x: i32
    y: i32
}

func Point.move(dx, dy) {
    self.x = self.x + dx
    self.y = self.y + dy
}

func main() {
    var p = Point { x: 10, y: 20 }
    p.move(5, 5)
    print(p.x) // 15
}
```

Поля структури обов'язково з типом (`x: i32`, не просто `x`), метод —
`func Структура.метод(...)` окремо від оголошення `struct`, доступ до
поточного екземпляра — `self`, не `this`.

### Успадкування (extends, super)
```nx
struct Animal {
    name: string

    func speak() {
        print(self.name + " видає якийсь звук.")
    }

    // self.speak() тут знайде найбільш ПОХІДНЕ перевизначення —
    // поліморфізм: диспетчеризація завжди йде за РЕАЛЬНИМ типом self,
    // а не за тим, де саме оголошений метод, що його викликає.
    func introduce() {
        print("Це " + self.name + ".")
        self.speak()
    }
}

struct Dog extends Animal {
    func speak() {
        super.speak()          // виклик батьківської реалізації
        print(self.name + " гавкає: Гав!")
    }
}

func main() {
    var dog = Dog { name: "Рекс" }
    dog.introduce()
    // Це Рекс.
    // Рекс видає якийсь звук.
    // Рекс гавкає: Гав!
}
```

`extends Батько` дає структурі доступ до всіх методів предка (і предків
предка — ланцюжок необмежений), яких вона сама не перевизначає. Перевизначений
метод повністю замінює батьківський для екземплярів дочірньої структури;
`super.метод(...)` усередині перевизначення викликає САМЕ батьківську
реалізацію, минаючи перевизначення (інакше вийшла б нескінченна рекурсія).

Чого свідомо НЕМАЄ (щоб не роздувати мову): модифікаторів доступу
(`private`/`public` — усі поля й методи публічні), множинного успадкування,
абстрактних структур/інтерфейсів. Поля предка й нащадка — просто ключі
одного словника, окремого оголошення "успадкованих полів" не існує:
`Dog { name: "Рекс" }` уже отримує все, чого очікують і власні методи Dog,
і успадковані від Animal.

### Обробка помилок (try/catch/throw)
```nx
func riskyDivide(a, b) {
    if b == 0 {
        throw "Ділення на нуль!"
    }
    return a / b
}

func main() {
    try {
        var r = riskyDivide(10, 0)
    } catch (e) {
        print("Спіймано: " + e)
    }
}
```
`try`/`catch` перехоплює як явний `throw`, так і внутрішні помилки VM (вихід за межі масиву, помилки нативних функцій тощо) — програма не падає, а виконання продовжується з catch-блоку.

### Мапи/словники
```nx
var ages = newMap()
mapSet(ages, "Святослав", 14)
print(mapGet(ages, "Святослав"))       // 14
print(mapHas(ages, "Богдан"))          // false
mapRemove(ages, "Святослав")
var keys = mapKeys(ages)
```
Мапа — окремий тип від `struct` (`typeOf()` повертає `"map"`), ключем може бути число, рядок або bool.

### Функції як значення та замикання
```nx
func square(x) { return x * x }

func apply(fn, x) { return fn(x) }

func main() {
    print(apply(square, 5))              // 25 - іменована функція як значення

    var double_ = func(x) { return x * 2 }
    print(double_(21))                   // 42 - анонімна функція (лямбда)

    var makeAdder = func(n) {
        return func(x) { return x + n }  // замикання - захоплює n
    }
    var add5 = makeAdder(5)
    print(add5(10))                      // 15
}
```
Замикання захоплюють значення зовнішніх змінних **копією в момент створення** лямбди (не живим посиланням) — зміна зовнішньої змінної після створення лямбди на неї не впливає.

### Модулі (import)
```nx
// math_helpers.nx
func square(x) { return x * x }
```
```nx
// main.nx
import "math_helpers.nx"

func main() {
    print(square(5))   // 25
}
```
Шлях в `import` — відносно файлу, що імпортує. Циклічні та повторні імпорти безпечні (кожен файл обробляється один раз).

Вибірковий import — тягне лише перелічені функції/структури/глобальні змінні, а не весь файл:
```nx
import "math_helpers.nx" { square }

func main() {
    print(square(5))   // 25 - cube() з того ж файлу лишається неімпортованим
}
```
Методи названої структури підтягуються автоматично разом з нею. Якщо переліченого імені нема у файлі — помилка одразу при запуску (ще до виконання коду). Функції з іменем на `_` (напр. `_helper`) вважаються приватними хелперами модуля й тягнуться завжди, навіть якщо їх не перелічено — так публічна функція з того ж файлу може викликати свій внутрішній хелпер незалежно від того, що саме імпортує викликач.

### Стандартна бібліотека (`lib/`)
У теці `lib/` в корені репозиторію лежать готові `.nx`-модулі — підключаються звичайним `import` за відносним шляхом (`../lib/...` з файлу в `tests/`, або `lib/...`, якщо скрипт лежить поруч із самою `lib/`):

- **`lib/datetime.nx`** — арифметика дат з правильними високосними роками (алгоритм Говарда Гіннанта, чиста NyxilumLang): `daysFromCivil(y,m,d)`/`civilFromDays(z)` (дата <-> днів від епохи), `isLeapYear(y)`, `dayOfWeek(y,m,d)` (0=неділя), `dayName(weekday)`, `addDays(y,m,d,n)`, `diffDays(y1,m1,d1,y2,m2,d2)`, `formatDate(y,m,d)`, `parseDate(s)`, `todayCivil()`.
- **`lib/strings.nx`** — `capitalize(s)`, `titleCase(s)`, `isBlank(s)`, `isEmpty(s)`, `padLeft(s, len, ch)`, `padRight(s, len, ch)`, `countOccurrences(s, sub)`.
- **`lib/collections.nx`** — `range(n)`, `rangeFrom(start, end)`, `sum(arr)`, `first(arr)`, `last(arr)`, `flatten(arr)` (один рівень), `zip(arr1, arr2)`, `chunk(arr, size)`, `count(arr, fn)`.
- **`lib/testing.nx`** — `assertTrue(cond, msg)`, `assertFalse(cond, msg)`, `assertEqual(actual, expected, msg)`, `assertThrows(fn, msg)`. Провал — звичайний `throw`, тож або лови його `try/catch` сам, або лишай непійманим, щоб процес впав з ненульовим кодом (зручно для CI).
  ```nx
  import "lib/testing.nx" { assertEqual }

  func main() {
      assertEqual(2 + 2, 4, "2+2 має дорівнювати 4")
      print("тест пройдено")
  }
  ```
- **`lib/http_client.nx`** — обгортка над `httpGet`/`httpPost`/`httpRequest` з автоматичною JSON-серіалізацією (у дусі Python-бібліотеки `requests`): `getJson(url)`, `postJson(url, data)`, `requestJson(url, method, data)` (`data` — мапа/масив або `null`), `requestStatus(url, method, data)` — лише код статусу.
  ```nx
  import "lib/http_client.nx" { postJson }

  func main() {
      var data = newMap()
      mapSet(data, "name", "Святослав")
      var response = postJson("https://httpbin.org/post", data)
      print(toJson(response))
  }
  ```
- **`lib/telegram.nx`** — обгортка над [Telegram Bot API](https://core.telegram.org/bots/api) (звичайний HTTPS+JSON, без WebSocket - тому повністю реалізований на самій NyxilumLang): `tgGetMe(token)`, `tgSendMessage(token, chatId, text)`, `tgGetUpdates(token, offset)`, `tgMessageText(update)`, `tgChatId(update)`, і блокуючий `tgPollLoop(token, handler)` для готового бота одним викликом. Токен читай через `osEnv("TELEGRAM_BOT_TOKEN")`, ніколи не хардкодь у скрипті. Повний робочий приклад ехо-бота: `programs/telegram_echo_bot.nx`.
  ```nx
  import "lib/telegram.nx" { tgPollLoop, tgMessageText, tgChatId, tgSendMessage }

  func main() {
      var token = osEnv("TELEGRAM_BOT_TOKEN")
      tgPollLoop(token, func(update) {
          var text = tgMessageText(update)
          if !isNull(text) {
              tgSendMessage(token, tgChatId(update), "Ехо: " + text)
          }
      })
  }
  ```
- **`lib/discord.nx`** — мінімальний [Discord Gateway](https://discord.com/developers/docs/topics/gateway)-клієнт: `dSendMessage(token, channelId, text)` (REST), і блокуючий `dPollLoop(token, intents, handler)` — сам робить handshake (Hello -> Identify -> heartbeat за розкладом сервера) через нативний `wsConnect`/`wsSend`/`wsReceive`, `handler(eventName, data)` викликається на кожну подію (`"MESSAGE_CREATE"`, `"READY"` тощо). На розрив з'єднання сервером (напр. невірний токен - код 4004) виходить з циклу з чітким повідомленням, а не зависає чи падає. Токен - через `osEnv("DISCORD_BOT_TOKEN")`. Повний приклад: `programs/discord_echo_bot.nx`.
  ```nx
  import "lib/discord.nx" { dPollLoop, dSendMessage }

  func main() {
      var token = osEnv("DISCORD_BOT_TOKEN")
      dPollLoop(token, 37377, func(eventName, data) {
          if eventName == "MESSAGE_CREATE" {
              dSendMessage(token, mapGet(data, "channel_id"), "Ехо: " + mapGet(data, "content"))
          }
      })
  }
  ```
  Не забудь увімкнути "Message Content Intent" у Discord Developer Portal - без нього `content` завжди порожній.

### Функції вищого порядку та JSON
```nx
var nums = [5, 2, 8, 1]
var sorted = sort(nums, func(a, b) { return a - b })
var squares = mapArr(nums, func(x) { return x * x })
var evens = filter(nums, func(x) { return x % 2 == 0 })
var sum = reduce(nums, func(acc, x) { return acc + x }, 0)

var data = newMap()
mapSet(data, "name", "Святослав")
print(toJson(data))              // {"name":"Святослав"}
var back = fromJson("[1,2,3]")   // масив
```

### Конкурентність

`spawn(fn, ...args)` запускає функцію-значення на новій, повністю
ізольованій VM в окремому потоці — свій стек, свої глобальні змінні,
той самий байткод. Жодного спільного мутабельного стану з викликачем
чи іншими воркерами: усе, що потрапляє в `spawn()` (аргументи, значення,
захоплені лямбдою через замикання), глибоко копіюється на межі — мутація
масиву/мапи/структури в головному потоці ПІСЛЯ spawn ніяк не вплине на
вже запущеного воркера, і навпаки.

```nx
func important(n) {
    return n * n
}

func main() {
    var w = spawn(important, 7)
    print(workerJoin(w))   // 49 — чекає завершення й повертає результат
}
```

`workerJoin(worker)` блокує виклик, доки воркер не завершиться, і
повертає його результат. Якщо воркер завершився необробленою помилкою —
`workerJoin` прокидає її далі, її можна зловити звичайним `try/catch`.

`workerJoin(worker, timeoutMs)` — з обмеженням часу очікування: якщо
воркер не встиг за цей час, кидає помилку (не повертає `null` — воркер
міг сам легітимно повернути `null` як результат, тож `null` не годиться
як ознака тайм-ауту). Сам воркер при цьому НЕ зупиняється — просто
перестаємо його чекати; `workerJoin` можна викликати повторно пізніше,
щоб забрати результат, коли він таки завершиться:

```nx
var w = spawn(slowTask)
try {
    print(workerJoin(w, 100))
} catch (e) {
    print("ще не готово, спробуємо пізніше")
}
// ... інша робота ...
print(workerJoin(w))   // без тайм-ауту — дочекатись остаточно
```

Кілька воркерів одночасно:
```nx
var workers = []
var i = 0
while i < 10 {
    append(workers, spawn(important, i))
    i = i + 1
}
var results = []
i = 0
while i < len(workers) {
    append(results, workerJoin(workers[i]))
    i = i + 1
}
```

Канали — для спілкування МІЖ воркерами (а не лише "запустив і забрав
результат наприкінці"):
```nx
var ch = newChannel()

func producer(c) {
    var i = 0
    while i < 5 {
        channelSend(c, i)
        i = i + 1
    }
}

func main() {
    var w = spawn(producer, ch)
    var i = 0
    while i < 5 {
        print(channelReceive(ch))   // блокує до наступного повідомлення
        i = i + 1
    }
    workerJoin(w)
}
```
`channelReceive(ch, timeoutMs)` — з тайм-аутом: повертає `null`, якщо
нічого не прийшло за відведений час, замість вічного блокування.

Значення, що проходять через `channelSend`, теж глибоко копіюються —
той самий "shared nothing" принцип, що й у `spawn()`.

**GUI/графіка з воркера.** `guiSetText`/`guiAdd`/`guiShow`/`presentCanvas`/
`closeCanvas` та подібні (усе, що чіпає вже створене вікно) можна
викликати лише з головного потоку — Windows Forms цього вимагає. Виклик
з воркера кидає чітку помилку, а не крашить чи псує стан вікна. Щоб
воркер вплинув на GUI — хай поверне результат через `workerJoin` чи
надішле каналом, а сам GUI онови з головного потоку:

```nx
func heavyCalculation() {
    // ... довгі обчислення без GUI ...
    return 42
}

func main() {
    var label = guiLabel("Рахую...", 10, 10, 200, 30)
    var w = spawn(heavyCalculation)
    var result = workerJoin(w)     // головний потік чекає й забирає результат
    guiSetText(label, toString(result))  // GUI оновлюється тут, з головного потоку
}
```

### Графіка (2D і 3D)
```nx
var canvas = createCanvas("Гра", 400, 300)
var frame = 0
while frame < 60 {
    clearCanvas(canvas, 20, 20, 30)
    drawRect(canvas, 50, 100, 40, 40, 200, 60, 60)
    drawCircle(canvas, 300, 150, 25, 60, 200, 60)
    drawText(canvas, "Кадр " + toString(frame), 10, 10, 14, 255, 255, 255)
    presentCanvas(canvas)
    sleep(16)
    frame = frame + 1
}
closeCanvas(canvas)
```
3D робиться поверх того ж полотна: обертання точок через `sin`/`cos` (уже вбудовані), а `project3D(canvas, x, y, z, camDistance)` перетворює 3D-координату в 2D-точку екрана — і далі малюєш ребра через `drawLine`. Ввід: `isKeyDown("W")`, `isMouseDown(canvas)`, `getMouseX/Y(canvas)`, `canvasShouldClose(canvas)`.

### Вбудовані функції
- `print(val)` - вивід у консоль
- `readLine()`, `readInt()`, `readDouble()` - читання з консолі
- `sqrt(x)`, `sin(x)`, `cos(x)`, `tan(x)`, `pow(x,y)`, `abs(x)`, `round/floor/ceil(x)`, `clamp(x,min,max)` - математика
- `max/min(a,b)` або `max/min(arr)` - максимум/мінімум двох чисел або всього масиву
- `toFixed(x,n)` - число як рядок з n знаками після коми, напр. `toFixed(3.14159, 2)` -> `"3.14"`
- `readFile(path)`, `writeFile(path, content)`, `appendFile(path, content)`, `fileExists(path)`, `readLines(path)` - робота з файлами
- `deleteFile(path)`, `makeDir(path)`, `dirExists(path)`, `deleteDir(path)` (рекурсивно), `listDir(path)` (масив імен файлів/тек усередині) - файли й теки
- `zipCreate(zipPath, sourceDir)` - запакувати теку в архів; `zipExtract(zipPath, destDir)` - розпакувати ввесь архів, повертає кількість файлів; `zipEntries(zipPath)` - список імен файлів усередині БЕЗ розпаковування; `zipExtractEntry(zipPath, entryName, destPath)` - витягнути лише один файл, повертає `true`/`false` (є така точка в архіві?)
- `toString(v)`, `toInt(v)`, `toDouble(v)`, `typeOf(v)`, `isNumber/isString/isArray/isBool(v)` - перетворення та перевірка типів
- `charCode(s)` - код першого символу рядка (наприклад, `charCode("A")` -> 65); `fromCharCode(code)` - символ за кодом
- `len(v)`, `substring(s,start,len)`, `replace/toUpper/toLower/contains/startsWith/endsWith(s,...)`, `split(s,sep)`, `join(arr,sep)` - рядки
- `trim(s)` - прибирає пробіли з обох боків; `repeat(s,n)` - повторює рядок n разів
- `indexOf(v,item)` - позиція елемента/підрядка (-1, якщо нема); працює і для рядків, і для масивів
- `reverse(v)` - розвертає рядок або масив (оригінальний масив не змінює)
- `append(arr,v)`, `pop(arr)`, `insert(arr,i,v)`, `removeAt(arr,i)`, `clear(arr)` - масиви
- `slice(arr,start,end?)` - підмасив від start до end (або до кінця); `unique(arr)` - прибирає дублікати, лишає порядок
- `newMap()`, `mapSet(m,k,v)`, `mapGet(m,k)`, `mapHas(m,k)`, `mapRemove(m,k)`, `mapKeys(m)`, `mapValues(m)` - мапи/словники
- `sort(arr,cmp)`, `mapArr(arr,fn)`, `filter(arr,fn)`, `reduce(arr,fn,init)` - функції вищого порядку над масивами
- `toJson(v)`, `fromJson(str)` - серіалізація в JSON і назад
- `sleep(ms)` - пауза виконання
- `exit(код)` - негайно завершує процес із заданим кодом виходу (рядки коду після exit() не виконуються) - для CI/скриптів, де потрібен конкретний код (0 успіх, інший - провал), без штучного throw (той завжди дає код 1)
- `gc_stats()` - структура `{allocated, limit, bytesEstimate}` з обліком NyxilumLang-виділень (масиви/структури/мапи) поточного запуску; `gc_collect()` - форсує збирання сміття .NET і оновлює оцінку пам'яті; `gc_limit(n)` - встановлює ліміт кількості виділень, перевищення кидає помилку (можна зловити через `try/catch`, або задати ззовні через `NX_GC_MAX_OBJECTS`)
- `dbOpen(path)` - відкриває (чи створює) персистентну KV-базу [NyxilumDb](https://github.com/Faneraiy14/NyxilumDb) в теці за шляхом `path`; `dbSet(db,k,v)`, `dbGet(db,k)` (рядок або `null`), `dbHas(db,k)`, `dbDelete(db,k)` - значення в v1 лише рядки; `dbKeys(db,prefix?)` - масив ключів (опційно за префіксом); `dbCount(db)`; `dbCheckpoint(db)` - примусова компакція; `dbClose(db)` - закриває базу (компактує WAL, якщо він непорожній)
- `createCanvas(title,w,h)`, `clearCanvas`, `drawRect`, `drawCircle`, `drawLine`, `drawText`, `presentCanvas`, `canvasShouldClose`, `closeCanvas` - 2D графіка
- `project3D(canvas,x,y,z,camDistance)` - проекція 3D-точки в 2D для рендеру 3D-сцен
- `guiWindow(title,w,h)`, `guiLabel(text,x,y,w,h)`, `guiButton(text,x,y,w,h)`, `guiTextBox(x,y,w,h)` - вікна на Windows Forms
- `guiAdd(parent,child)` - додати елемент у вікно; `guiSetText(control,text)`/`guiGetText(control)` - змінити/прочитати текст
- `guiOnAction(button,fn)` - викликати NyxilumLang-функцію `fn` при кліку (звичайну функцію-значення, як у `sort(arr,cmp)` — без дужок, не рядок з іменем)
- `guiShow(win)` - показати вікно (блокує, доки вікно не закриють)
- `isKeyDown(key)`, `isMouseDown(canvas)`, `getMouseX/Y(canvas)` - ввід для вікна
- `randomInt(min,max)`, `randomDouble(min,max)`, `now()`, `today()`, `timestamp()` - утиліти
- `formatDate(timestamp, format?)` - Unix-timestamp (секунди) у рядок довільного формату (.NET custom date format, напр. `"dd.MM.yyyy HH:mm"`); без `format` - той самий вигляд, що й `now()`; `parseDate(str, format)` - обернена операція, рядок за форматом назад у Unix-timestamp; кидає помилку, якщо рядок не відповідає формату
- `osPlatform()`, `osArchitecture()`, `osMemory()`, `osCpuCount()`, `osEnv(name)`, `osCwd()` - інформація про систему
- `procStart(cmd, args?, options?)` - запускає зовнішній процес ОС і одразу повертає хендл, НЕ чекаючи завершення (`args` - масив рядків, `options` - мапа з `cwd`/`env`); `procRun(cmd, args?, options?)` - те саме, але БЛОКУЄ до завершення й повертає мапу `{exitCode, stdout, stderr}`; `procWait(h)` - чекає завершення вже запущеного `procStart`, повертає код виходу; `procIsRunning(h)`, `procKill(h)`, `procPid(h)`, `procExitCode(h)` (`null`, доки процес ще працює); `procOutput(h)`/`procErrorOutput(h)` - усе, що процес вивів у stdout/stderr ДОСІ (можна перечитувати повторно, поки процес ще працює). У пісочниці (`NX_SANDBOX=1`) заборонено завжди
- `httpGet(url)`, `urlStatus(url)` - HTTP-запити
- `httpPost(url, body)` - POST-запит з тілом `body` (Content-Type `application/json`), повертає тіло відповіді рядком
- `httpRequest(url, method, body?, headers?)` - запит довільним методом (`"PUT"`, `"DELETE"`, `"PATCH"` тощо), повертає мапу `{status, body}`; `headers` - мапа (`newMap`/`mapSet`) для заголовків на кшталт `Authorization`
- `wsConnect(url)` - відкриває WebSocket-з'єднання (`wss://`/`ws://`); `wsSend(ws, text)` - надсилає текстове повідомлення; `wsReceive(ws, timeoutMs?)` - блокує до наступного повідомлення або тайм-ауту (тоді повертає `null`); кидає помилку (лови через `try/catch`), якщо з'єднання розірване сервером; `wsClose(ws)` - закриває з'єднання
- `httpServer(port, handler)` - HTTP-сервер; `handler(path, method)` викликається на кожен запит і повертає рядок тіла відповіді. Блокує назавжди (Ctrl+C для зупинки)
- `regexTest(s, pattern)` - чи збігається рядок з regex-шаблоном (bool); `regexMatch(s, pattern)` - перший збіг або `null`; `regexFindAll(s, pattern)` - масив усіх збігів; `regexReplace(s, pattern, replacement)` - заміна всіх збігів
- `guiWindow(title, w, h)`, `guiButton(text, x, y, w, h)`, `guiShow(win)` - GUI (експериментально)

## Як запустити
Після встановлення (див. INSTALL.md) команда `nx` доступна на будь-якій
платформі (Windows/Linux/Mac) — передайте шлях до файлу:
`nx program.nx`

Інші команди:
- `nx format program.nx` - вивести відформатований код
- `nx lint program.nx` - перевірити код на типові помилки (невикористані змінні, задовгі рядки, порожні блоки)
