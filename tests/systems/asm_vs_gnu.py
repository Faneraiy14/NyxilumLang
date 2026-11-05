# S8 крок 6: кожна інструкція з лістингу власного асемблера (nxc --elf --listing: «рядок<TAB>hex»)
# має кодуватися так само, як у GNU as. Переходи/виклики на мітки й мітка(%rip) пропускаються:
# у нас вони завжди rel32/disp32, а as вкорочує переходи (вони перевіряються запуском програм).
# Запуск: python3 asm_vs_gnu.py лістинг... тека   -> код 0, якщо все збіглося
import sys, subprocess, re
*lsts, W = sys.argv[1:]
seen = {}
for lst in lsts:
    for line in open(lst, encoding='utf-8'):
        line = line.rstrip('\n')
        if '\t' not in line:
            continue
        src, hx = line.rsplit('\t', 1)
        if '*' not in src and re.search(r'(^j[a-z]+ |^call [A-Za-z_.]|\(%rip\))', src):
            continue
        seen.setdefault(src, hx)
items = list(seen.items())
with open(W + '/u.s', 'w') as f:
    for src, _ in items:
        f.write(src + '\n')
subprocess.run(['as', '--64', '-o', W + '/u.o', W + '/u.s'], check=True)
out = subprocess.run(['objdump', '-d', '--insn-width=16', W + '/u.o'], capture_output=True, text=True).stdout
ins = [m.group(1).replace(' ', '') for m in (re.match(r'\s+[0-9a-f]+:\t([0-9a-f ]+)\t', l) for l in out.splitlines()) if m]
bad = [f'{s}: наше {h}, as {g}' for (s, h), g in zip(items, ins) if h != g]
if len(ins) != len(items):
    bad.append(f'objdump дав {len(ins)} інструкцій замість {len(items)}')
print('\n'.join(bad[:10]) if bad else len(items))
sys.exit(1 if bad else 0)
