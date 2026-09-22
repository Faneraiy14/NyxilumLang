/* Фаза N15 - freestanding C-гарнес. Викликає ЗВИЧАЙНУ (не-naked)
 * функцію callNakedSetMarker(), яка сама викликає naked-функцію
 * nakedSetMarker() - доводить, що виклик у ОБИДВА боки (normal ->
 * naked, і повернення з naked назад у normal) реально працює: якщо
 * б esp/повернення після naked-виклику були неправильними, звичайна
 * функція callNakedSetMarker() сама впала б чи повернула сміття.
 */

typedef int i32;

extern i32 callNakedSetMarker(void);

static void _exit_(int code) {
    __asm__ volatile ( "int $0x80" : : "a"(1), "b"(code) );
    __builtin_unreachable();
}

void _start(void) {
    i32 result = callNakedSetMarker();
    /* 777 - те, що naked-функція мала записати в marker через
     * ГОЛИЙ asm(), а звичайна функція - прочитати назад ЗВИЧАЙНИМ
     * кодом (return marker;) - обидва боки виклику (у naked і назад)
     * мусять бути справними, щоб це значення дійшло сюди. */
    _exit_(result == 777 ? 0 : 1);
}
