# VR UI Design Reference - QuestCraft, Vivecraft, and VR Patterns

**Date:** 2026-04-13
**Source:** QuestCraft, Vivecraft, ImmersiveMC, Bedrock VR, industry VR UI guidelines

---

## Vivecraft HUD - Three Placement Modes

1. **Wrist (default for standing):** HUD on inner forearm of off-hand. Glance down like a watch. Doesn't obstruct view.
2. **Hand:** Floats above off-hand controller. More visible but can obstruct.
3. **Head (default for seated):** Fixed distance from view center. Best for gamepad/seated play.

All modes: Health, hunger, XP, hotbar, armor rendered on the HUD surface.

## Menu Patterns

- **Menus spawn world-locked** in front of player at open time. Do NOT follow head (causes nausea).
- **Block-context menus** (chest, crafting) anchor above the relevant block for spatial context.
- **Laser pointer** from dominant hand for interaction. Trigger = click.
- **Radial/pie menu:** 8 slices, 2 rings = 16 bindings. Hold-to-select or toggle-to-click. Standard for VR quick access (Half-Life: Alyx, Vivecraft).

## VR Keyboard

Two modes:
1. **Physical-press:** 3D buttons pushed by controller collision
2. **Pointer:** Laser aim + trigger click on keys

Opens automatically on text field focus. Manual open via long-press menu button.

## Locomotion Options

| Type | Comfort | Description |
|------|---------|-------------|
| Teleport | Most comfortable | Parabolic arc aim, instant move. Energy meter in survival. |
| Dash | Medium | Quick move to target with brief transition |
| Smooth | Least comfortable | Analog stick continuous movement |

**Turning:** Snap (30-45 degree increments, most comfortable) / Quick / Smooth (least comfortable). Always offer all three.

## ImmersiveMC Pattern (Physical 3D Interaction)

Replace 2D GUI with physical interaction:
- Crafting table: place items directly on the 3D grid surface
- Chests: lift lid, grab items from inside
- Furnace: place fuel/input physically
- **Key principle:** Minimize flat 2D panels. Everything spatial.

## Diorama/Tabletop Mode (NOVEL for Minecraft VR)

Looking down at a miniature world on a table (like Moss VR). No Minecraft VR mod has done this yet. AubsCraft's admin viewer is PERFECT for this - overview mode where the world is a diorama you look down at and interact with.

## Comfort Guidelines

- **Text:** Minimum 2-3 degrees visual angle. Bold sans-serif. High contrast. Stay within 60 degrees of forward view.
- **Interactive targets:** Minimum 22mm x 22mm in VR space
- **Viewing distance:** 0.5-10m, optimal 2-3m for reading. Never closer than 0.5m.
- **FPS:** 72+ minimum, 90+ preferred. Below 72 = motion sickness.
- **Menus:** Fade in (never pop). World-lock after spawn (never move). Recenter button always available.
- **Vignette/blinders:** Adjustable peripheral darkening during locomotion.

## Complete Comfort Settings Checklist

Every VR app should offer:
- Turning: Snap / Quick / Smooth with adjustable angle
- Movement: Teleport / Dash / Smooth
- Direction: Head-based vs controller-based
- Swappable movement hand
- Vignette strength
- Standing vs seated mode
- Adjustable player height
- Real-crouch vs button-crouch

## Controller Layout (Quest Touch)

| Button | Action |
|--------|--------|
| Left thumbstick | Movement |
| Right thumbstick | Turn (snap/smooth) |
| Right trigger | Primary action |
| Left trigger | Teleport / alt action |
| Grip | Context interact |
| Y (left) | Inventory/menu |
| A/X | Jump / use |
| B | Drop / back |
| Menu (left) | Pause, long-press = keyboard |

## Key Design Decisions for AubsCraft

1. **HUD:** Wrist-mounted for VR standing, head-locked for desktop. Configurable.
2. **Settings panel:** World-locked floating panel with laser pointer interaction in VR. GPU-rendered overlay in desktop mode.
3. **Radial menu:** 8-slice quick access for common viewer actions.
4. **Diorama mode:** World overview as a miniature tabletop. Novel differentiator.
5. **No 2D GUI panels where physical interaction is possible** (ImmersiveMC principle).
6. **All UI rendered by WebGPU engine** - portable to browser, Quest, desktop app.
