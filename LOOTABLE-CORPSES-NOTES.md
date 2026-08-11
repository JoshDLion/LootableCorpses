# LootableCorpses dev notes

This branch is based on DeathCorpses 2.8.1 and keeps the original `deathcorpses`
mod id so existing configuration and persistent corpse data remain compatible.

## Intended behavior

- Right-click a corpse to open a loot-only inventory window.
- Shift + right-click to quick-loot everything that fits in the player's inventory.
- `FreeCorpseAfterTime = 0` preserves DeathCorpses' existing immediate-public-loot behavior.
- Partial looting leaves the corpse in the world with the remaining items.
- Empty corpses remove themselves.
- Partial inventory changes are written back to the existing persistent corpse `.dat` file.
- The corpse inventory rejects deposits (`PutLocked = true`) so corpses cannot be used as free storage.
- The GUI shows the original owner, real death date/time, and death cause, but not coordinates.

## GUI concept

    Corpse of Jarothi — Cadáver de Jarothi
    Death: 11 Aug 2026, 14:43 — Muerte: 11 ago 2026, 14:43
    Cause: Wolf — Causa: Lobo

    [ corpse inventory grid ]

## Compatibility choices

- Mod id stays `deathcorpses` deliberately.
- Existing `/dc corpse ...` commands and config keys are left in place.
- Existing corpse save schema version remains unchanged; partial-loot persistence rewrites
  the inventory portion of the existing save file.
- Old corpses created before this fork may not have detailed cause metadata on the entity;
  new corpses do.

## First test checklist

1. Build both VS targets through the repository's Nix build.
2. Verify the mod loads on VS 1.22.6 client and server.
3. Die with several stacks and equipment.
4. Open the corpse with right-click.
5. Confirm items can be taken but cannot be inserted into the corpse.
6. Have two players open the same corpse and loot different items.
7. Restart the server after partially looting and confirm taken items do not return.
8. Run `/dc corpse get` after partial looting and confirm already-looted items are not restored.
9. Empty the corpse and confirm the entity and persistent save are removed.
10. Test Shift + right-click with both free inventory space and a nearly-full inventory.

This is a development build until CI compiles it successfully and the multiplayer tests above pass.
