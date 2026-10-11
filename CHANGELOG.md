# Changelog

## 0.2.2 - 2026-10-11

- A powered fridge now shuts out a room warmer than its contents at any temperature, not only once they are below the setpoint. In a hydrogen atmosphere the room could out-pull the compressor on a small item: 1 kg of Barbeque in a modded -19 °C Freezer sat flickering at -13 °C and never got colder. Contents warmer than the room still exchange heat with it, so a fridge in a cold room still deep-freezes its food.

## 0.2.1 - 2026-10-03

- An item with no thermal conductivity no longer disturbs the fridge's temperature mixing.
- If a game update changes the internals the mod relies on, it now logs a warning and leaves vanilla cooling alone instead of erroring.
