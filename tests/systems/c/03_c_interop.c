#include <stdio.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
struct Rec { int8_t a; int32_t b; double c; uint16_t d; const char *p; };
double nx_mix(int32_t, double, uint8_t, double, int64_t, struct Rec *);
void nx_fill(struct Rec *);
int64_t nx_sum(int64_t *, int64_t);
uint64_t nx_udiv(uint64_t, uint64_t);
int32_t nx_sdiv(int32_t, int32_t);
int main(void) {
    int fails = 0;
#define CHECK(c, m) do { if (!(c)) { printf("FAIL %s\n", m); fails++; } } while (0)
    struct Rec r; memset(&r, 0x77, sizeof r);
    nx_fill(&r);
    CHECK(r.a == -5 && r.b == 1000000 && r.c == 2.5 && r.d == 65000, "поля структури за розміткою C");
    CHECK(strcmp(r.p, "з NyxilumLang") == 0, "рядок-вказівник");
    CHECK(nx_mix(-3, 0.5, 250, 0.25, 1LL << 40, &r) == -3 + 0.5 + 250 + 0.25 + (double)(1LL << 40) + 2.5, "змішані цілі й дробові аргументи");
    int64_t v[5] = {1, -2, 3000000000LL, 4, 5};
    CHECK(nx_sum(v, 5) == 3000000008LL, "масив з C");
    CHECK(nx_udiv(UINT64_MAX, 3) == UINT64_MAX / 3, "беззнакове ділення 64");
    CHECK(nx_sdiv(-7, 2) == -3 && nx_sdiv(INT32_MIN, 1) == INT32_MIN, "знакове ділення 32");
    int64_t nx_rec_size(void);
    CHECK(nx_rec_size() == (int64_t)sizeof r, "sizeof(Rec) як у C");
    printf(fails ? "C-зшивка: %d помилок\n" : "C-зшивка: OK%.0d\n", fails);
    return fails != 0;
}
