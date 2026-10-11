# No Magic Fridges

An [Oxygen Not Included](https://www.klei.com/games/oxygen-not-included) mod that makes a powered fridge behave like an insulated box with a thermostat: it **only ever cools**, its contents share heat with each other, and food already colder than the 1 °C target is never warmed up to it.

## The problem

A powered Refrigerator or Mini Fridge does not set its contents to 1 °C; it attaches a simulated heat reservoir at 1 °C (400 kJ/K, 1000 W/K) to every stored item. That exchange runs both ways, so food you deep-froze by keeping the fridge in a cold room is dragged back up to 1 °C the moment the fridge gets power, even for a few seconds. Unpowered, the food just exchanges heat with the room as normal.

## What the mod does

Everything here applies **only while the fridge is powered**; an unpowered fridge is exactly vanilla (its contents exchange heat with the room at the normal rate). With power, the fridge is modelled as an insulated box with a thermostat, instead of a 1 °C reservoir glued to every item. Once a second:

- **The contents share heat, in both directions.** Every item moves toward one interior temperature worked out from everything in the fridge, so frozen stock chills a warm newcomer fast, and is warmed a little in return. Put 5 kg of 30 °C food into 95 kg at -20 °C and it all settles at -17.5 °C: the heat is conserved, not deleted.
- **The compressor only ever cools.** While the contents' average is above 1 °C, the fridge's own thermal mass (worth about 25 kg of food, held at 1 °C) is blended into that interior temperature and pulls it down. At or below 1 °C the compressor is off and nothing pulls the food *up*: deep-frozen food stays deep-frozen however often you power the fridge.
- **Thermal buffer matters.** A single warm item in an otherwise empty or cold fridge is chilled about as fast as in vanilla. A fridge crammed with warm food (after a long power cut, say) cools at roughly a fifth of the vanilla rate, because a small compressor is working against a big warm mass.
- **Contents are insulated from a warmer room.** An item colder than the room around the fridge has its heat exchange with the room scaled down by the factor an Insulated Tile uses (1/100): slowed, not stopped (that would be Insulite). The room can only warm such an item, and in a conductive atmosphere it would otherwise out-pull the compressor: a kilogram of food in a hydrogen room at 22 °C sat at -13 °C in a -19 °C Freezer. An item warmer than the room keeps exchanging with it, so a fridge kept in a cold room still deep-freezes its contents.

Unpowered fridges, and items taken out of a fridge, behave exactly as in vanilla.

- Applies to the Refrigerator, the Mini Fridge, and any modded building that reuses the vanilla fridge controller.
- Nothing else changes: power draw, the cooling and energy-saver states (still decided by vanilla from the items' temperatures), storage, the full signal (which still requires power, as in vanilla), and spoilage rules are all untouched. Nothing is saved with the game. Safe to add to or remove from an existing save.

## Installing

As a local mod:

1. Download `NoMagicFridges-<version>.zip` from the [latest release](https://github.com/isochronous/no-magic-fridges/releases/latest).
2. Extract it into a new folder named `NoMagicFridges` inside the game's local mods folder, so that `mod.yaml` ends up directly inside it (create `local` if it does not exist):
   - Windows: `Documents\Klei\OxygenNotIncluded\mods\local\NoMagicFridges`
   - Linux: `~/.config/unity3d/Klei/Oxygen Not Included/mods/local/NoMagicFridges`
   - macOS: `~/Library/Application Support/unity.Klei.Oxygen Not Included/mods/local/NoMagicFridges`
3. Start the game, enable the mod under **Mods** in the main menu, and let the game restart.

## Building

Requires the .NET SDK (8+). Shared build configuration lives in the [oni-mods-common](https://github.com/isochronous/oni-mods-common) submodule, so clone with `--recurse-submodules` (or run `git submodule update --init`). The game DLLs are referenced directly from the game install; override the path if yours differs:

```
dotnet build src/NoMagicFridges -c Release -p:GameFolder="<path-to>\OxygenNotIncluded"
```

A successful build deploys the mod to `Documents\Klei\OxygenNotIncluded\mods\local\NoMagicFridges` (disable with `-p:ModDeployFolder=none`). Harmony only, no PLib.

## Implementation notes

- A Harmony prefix on `SimulatedTemperatureAdjuster.OnItemSimRegistered` keeps the vanilla reservoir off every item stored in a building that has a `FridgeThermostat`; other users of the adjuster are left alone.
- `FridgeThermostat` (added to every prefab with a `RefrigeratorController.Def` after buildings are generated) runs the model in `Sim1000ms`. Each item closes the fraction `1 - exp(-k·dt / C)` of its gap to the interior temperature, with `k` = the def's `simulatedThermalConductivity` read as W/K (1 kDTU/s/K) and `C` = mass × specific heat. Compressor on: interior = (C_base·1 °C + Σ C·T) / (C_base + Σ C), which always lies between 1 °C and the contents' mean. Compressor off: interior = Σ C·f·T / Σ C·f, the weighting that makes the step conserve energy exactly. Temperatures are written through `PrimaryElement.Temperature`, which forwards to the sim chunk; steps under 0.0005 K are skipped, so a fridge at rest sends nothing.
- Insulation is applied by scaling the item's `SimTemperatureTransfer.SurfaceArea` and `GroundTransferScale` by 0.01 and re-registering its sim chunk (protected `SimUnregister`/`SimRegister`, which sync the temperature first), once the cell the item sits in is 0.25 K warmer than the item, undone once it is 0.25 K colder, so an item near room temperature does not flip every second. Originals are restored when the fridge loses power, the item leaves, or the fridge is removed.
- Tuning lives in two constants in `FridgeThermostat`: `BaseHeatCapacity` (compressor strength) and `InsulationFactor`.
