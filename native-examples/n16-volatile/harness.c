/* Фаза N16 - freestanding C-гарнес - функціональна перевірка (сам
 * доказ "no-op" - побайтовий diff двох .s, дивись README.md/скрипт
 * компіляції) - тут лише "чи взагалі volatile-змінна працює як
 * звичайна".
 */

typedef int i32;

extern i32 readStatus(void);
extern void writeStatus(i32 v);
extern i32 localVolatileRoundtrip(i32 x);

static void _exit_(int code) {
    __asm__ volatile ( "int $0x80" : : "a"(1), "b"(code) );
    __builtin_unreachable();
}

void _start(void) {
    unsigned fail = 0;

    writeStatus(0xBEEF);
    if (readStatus() != 0xBEEF) fail |= (1u << 0);

    if (localVolatileRoundtrip(-999) != -999) fail |= (1u << 1);

    _exit_((int)fail);
}
