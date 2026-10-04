"""Interpolate the Zeus delivery base reward between level 55 and level 140.

The user's anchors, both at tier 1:
    level  55 ->  30,000 experience and 2 talent points
    level 140 -> 120,000 experience and 8 talent points
"""
LOW_LEVEL, HIGH_LEVEL = 55, 140
LOW_EXP, HIGH_EXP = 30_000, 120_000
LOW_TALENT, HIGH_TALENT = 2, 8

SPAN = HIGH_LEVEL - LOW_LEVEL
EXP_STEP = (HIGH_EXP - LOW_EXP) / SPAN
TALENT_STEP = (HIGH_TALENT - LOW_TALENT) / SPAN

print(f"levels {LOW_LEVEL}..{HIGH_LEVEL}  span={SPAN}")
print(f"experience per level = {EXP_STEP:.6f}")
print(f"talent points per level = {TALENT_STEP:.6f}")
print()


def exact_exp(level):
    return LOW_EXP + (level - LOW_LEVEL) * EXP_STEP


def exact_talent(level):
    return LOW_TALENT + (level - LOW_LEVEL) * TALENT_STEP


def rounded_exp(level):
    return round(exact_exp(level))


def stepped_talent(level):
    return LOW_TALENT + (level - LOW_LEVEL) * (HIGH_TALENT - LOW_TALENT) // SPAN


print("level : exact experience -> rounded | exact talent -> stepped")
for level in range(LOW_LEVEL, HIGH_LEVEL + 1):
    print(f"{level:>5} : {exact_exp(level):>12.3f} -> {rounded_exp(level):>7} | "
          f"{exact_talent(level):>6.3f} -> {stepped_talent(level)}")
