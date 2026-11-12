---
name: units
description: Engineering unit conversions and calculations with pint (convert, calc expressions with units, dimension check, unit info; m3/s2 shorthand, Nm3, ppm, EUR). Use instead of doing unit arithmetic in your head.
version: 0.1.1
entry: units.py
requires: [pint]
secrets: []
---

# units

Расчёты и пересчёт единиц (pint), чтобы агент не считал единицы в уме.

## Вызов

```
python units.py convert "12 bar" psi [--digits 6] [--json]
python units.py calc "3 m3/h * 1000 kg/m3 * 9.81 m/s2 * 50 m" [--to kW]
python units.py check "1 kWh" "3.6 MJ"
python units.py info psi
```

Сокращения `m3`, `m2`, `s2` понимаются как степени; определены `Nm3` (нормальный м³ как объём, без пересчёта к рабочим условиям), `ppm`, `EUR`, `kEUR`, `MEUR`. Температуры (`degC`, `degF`, `K`) пересчитываются со смещением.

## Ограничения

- `Nm3` не пересчитывается в фактический объём (нужны давление и температура — считать явно).
- Валюты — только масштаб (EUR/kEUR/MEUR), без курсов.

## История

- 0.1.1 — 2026-11-12 — Freedom: история и примеры без личных и корпоративных имён; поведение не изменилось.
- 0.1.0 — 2026-10-17 — первая версия (код — корпоративная модель claude-opus-5.5).
