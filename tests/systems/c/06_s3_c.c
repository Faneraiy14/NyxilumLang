#include <stdio.h>
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <string.h>
struct Inner { uint8_t a; double b; };
struct Outer { int16_t tag; struct Inner inner; int32_t arr[3]; bool flag; struct Inner tail[2]; uint8_t last; };
int64_t nx_sizeof_outer(void); int64_t nx_off(int64_t); void nx_fill(struct Outer *); double nx_sum(struct Outer *);
int main(void) {
    int fails = 0;
#define CHECK(c, m) do { if (!(c)) { printf("FAIL %s\n", m); fails++; } } while (0)
    CHECK(nx_sizeof_outer() == sizeof(struct Outer), "sizeof(Outer)");
    size_t offs[] = { offsetof(struct Outer, inner), offsetof(struct Outer, arr), offsetof(struct Outer, flag), offsetof(struct Outer, tail), offsetof(struct Outer, last) };
    for (int k = 0; k < 5; k++) CHECK(nx_off(k) == (int64_t)offs[k], "offsetof");
    struct Outer o; memset(&o, 0, sizeof o);
    nx_fill(&o);
    CHECK(o.tag == -3 && o.inner.a == 200 && o.inner.b == 0.5 && o.arr[2] == -77 && o.flag && o.tail[1].b == 6.25 && o.last == 9, "запис з NyxilumLang");
    o.arr[0] = 10; o.arr[1] = 20; o.tail[0].b = 0.25;
    CHECK(nx_sum(&o) == -3 + 200 + 0.5 + (10 + 20 - 77) + 0.25 + 6.25 + 9, "читання з C");
    printf(fails ? "розмітка: %d помилок\n" : "розмітка: OK%.0d\n", fails);
    return fails != 0;
}
