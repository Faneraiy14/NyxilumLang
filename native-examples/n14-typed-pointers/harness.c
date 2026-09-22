/* Фаза N14 - freestanding C-гарнес (-m32 -ffreestanding -nostdlib, без
 * libc), той самий підхід, що n12/n13. Пам'ять під буфери виділяє сам
 * гарнес (звичайні статичні масиви .bss) - переданий адреси в
 * NyxilumLang-функції як звичайні C-покажчики (cdecl передає покажчик
 * як просте 4-байтове ціле - точнісінько те, що ptr<T>-параметр і так
 * очікує).
 */

typedef int i32;
typedef signed char i8;
typedef unsigned char u8;
typedef short i16;

extern i32 readI32(i32 *p, i32 i);
extern void writeI32(i32 *p, i32 i, i32 v);
extern u8  readU8(u8 *p, i32 i);
extern void writeU8(u8 *p, i32 i, u8 v);
extern i32 sumViaPointerArith(i32 *p);
extern i32 sumBytesViaPointerArith(u8 *p);
extern i32 isNullPtr(i32 *p);
extern i32 ptrEquality(i32 *a, i32 *b);
extern i16 readI16(i16 *p, i32 i);
extern void writeI16(i16 *p, i32 i, i16 v);

static void _exit_(int code) {
    __asm__ volatile ( "int $0x80" : : "a"(1), "b"(code) );
    __builtin_unreachable();
}

static i32 buf32[8];
static u8  buf8[8];
static i16 buf16[8];

void _start(void) {
    unsigned fail = 0;

    /* 1. Базовий запис/читання ptr<int32>. */
    writeI32(buf32, 2, 424242);
    if (readI32(buf32, 2) != 424242) fail |= (1u << 0);

    /* 2. Базовий запис/читання ptr<uint8>. */
    writeU8(buf8, 3, 200);
    if (readU8(buf8, 3) != 200) fail |= (1u << 1);

    /* 3. Арифметика вказівників МАСШТАБУЄ на sizeof(int32)=4: "p+1"
     * усередині NyxilumLang-коду має вказувати на buf32[1], НЕ на
     * адресу+1 байт - якщо компілятор помилково НЕ масштабував би
     * (стара String-семантика "адреса+1 байт"), p[0]+q[0] прочитав би
     * зовсім не те значення (обрізаний/зсунутий байт замість цілого
     * buf32[1]). */
    buf32[0] = 10;
    buf32[1] = 20;
    if (sumViaPointerArith(buf32) != 30) fail |= (1u << 2);

    /* 4. Те саме для ptr<uint8> (sizeof=1) - тут масштаб і "сирий
     * зсув у байтах" ЗБІГАЮТЬСЯ (на відміну від тесту 3), доводячи, що
     * масштаб РЕАЛЬНО залежить від T, а не завжди "1" чи завжди "4". */
    buf8[0] = 5;
    buf8[1] = 7;
    if (sumBytesViaPointerArith(buf8) != 12) fail |= (1u << 3);

    /* 5. Порівняння з null (0). */
    if (isNullPtr((i32 *)0) != 1) fail |= (1u << 4);
    if (isNullPtr(buf32) != 0) fail |= (1u << 5);

    /* 6. Порівняння ідентичності двох вказівників (та сама адреса -
     * рівні; buf32 проти buf32+1 елемент - НЕ рівні). */
    if (ptrEquality(buf32, buf32) != 1) fail |= (1u << 6);
    if (ptrEquality(buf32, buf32 + 1) != 0) fail |= (1u << 7);

    /* 7. ptr<int16> - третя ширина (sizeof=2), включно з негативним
     * значенням (перевіряє signed-розширення при читанні). */
    writeI16(buf16, 1, -12345);
    if (readI16(buf16, 1) != -12345) fail |= (1u << 8);

    _exit_((int)fail);
}
