# SVS_VirginBlood

A BepInEx 6 (IL2CPP) plugin for SamabakeScramble that adds blood when a character loses her virginity in an H scene.

When a virgin is penetrated for the first time, a small stain appears at the opening and drips trickle down her inner thighs. The drips follow gravity: they only run while her knee is below her hip, and pause while a leg is raised or lying flat. The blood has a wet, sweat-like shine, with round drops at the drip tips. It is removed when the H scene ends, and the loss of virginity is written to her save data so it only happens once.

The game keeps three copies of a character's virginity: its own record, which it follows for conversations, H and voice lines, plus her character card and a copy of it in the save, which it never updates. The plugin follows the game's own record in H and, after a first time, writes the loss to all three so they stay in step.

## Controls

| Key | Action |
|-----|--------|
| **F9** | Add blood to the selected character by hand (re-rolls the shape if she already has some) |
| **F10** | Open/close the menu |

The menu lets you pick the character, add or remove blood, and adjust its look and size. Sliders adjust the last placed blood live. **Restore Defaults** resets every setting except the keys.

**Reset Selected to Virgin** makes the selected character a virgin again in all three records and clears her sex history (H count and per-partner counts), as if she never had sex. Save the game afterwards to keep it. Use this rather than SVS_CheatTools' virgin checkbox, which only changes the card copy the game ignores.

## Settings

Saved in `BepInEx/config/com.svs.virginblood.cfg`.

| Setting | Default | Effect |
|---------|---------|--------|
| First Time Blood | on | Add blood automatically at a virgin's first penetration |
| Keep After H | off | Keep the blood after the H scene ends |
| Keep In Dialogue | on | Keep the blood (first time or placed with F9) on the character through the dialogue after H, then remove it |
| Fix H Virginity | on | Follow the game's own virginity record in H, and save the loss to all three records afterwards |
| Spread | 1 | Size of the stain at the opening |
| Streak Length | 1 | How far the drips run down the thighs |
| Drips | on | Drips down the inner thighs |
| Flow / Flow Seconds | on / 6 | Drips trickle down over this many seconds instead of appearing at once |
| Gravity | on | Drips only run while the leg points down |
| Wet Shine | 0.6 | Glossy wet reflection, 0 turns it off |
| Opacity | 0.9 | Blood opacity |
| Mix With Cum | on | Tint the game's cum that comes out of a girl who has blood, so it looks mixed with the blood |
| Mix Strength | 0.35 | How red that cum turns; 0 leaves it white |

## Installation

Copy `BepinEx/plugins/SVS_VirginBlood.dll` to `<GameRoot>/BepInEx/plugins/`. The drip images are embedded in the DLL.

## Building

```powershell
dotnet build src\SVS_VirginBlood.csproj -c Release "-p:GameRoot=<path to the game folder>"
```

## Credits

The blood streak images in `src/Resources/` were cut from KK_SkinEffects (GPL-3.0).

## License

GPL-3.0, see `LICENSE`.
