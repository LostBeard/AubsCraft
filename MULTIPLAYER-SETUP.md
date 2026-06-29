# Playing AubsCraft on PC - Setup Guide for Parents and Families

Welcome! AubsCraft runs both **Java** and **Bedrock** clients (the server uses
GeyserMC, which lets the two editions play together). Whatever version of
Minecraft you have on PC, you can almost certainly join.

This guide covers the most common setups - especially how to play together
with your kid on a PC at home, the way you used to do split-screen on Xbox.

---

## Server Address

| Edition | Address | Port |
|---------|---------|------|
| Java Edition (PC) | `mc.spawndev.com` | (default, 25565) |
| Bedrock Edition (PC, phone, Switch, Xbox) | `mc.spawndev.com` | `19132` |
| QuestCraft (VR) | `mc.spawndev.com` | (default) |

Use the **Direct Connect** / **Add Server** option in your Minecraft client.

Live map of the world: [map.spawndev.com:44365](https://map.spawndev.com:44365)

---

## What You'll Need

- A copy of Minecraft on PC. The standard "Minecraft: Java & Bedrock Edition
  for PC" purchase ($29.99 on minecraft.net) includes BOTH editions in one
  unified launcher. If you only have one edition, that's fine too.
- Xbox controllers (Bluetooth or USB) if you want controller play.
- Both PCs on the same WiFi if you also want LAN play in addition to AubsCraft.

---

## Pick Your Setup

### Option A: Two PCs, One Family - RECOMMENDED

**Best for:** Households where you have a desktop AND a laptop (or two of
either). Each player gets their own full screen. Zero mods. Native Xbox
controller support on Bedrock.

**The trick:** Microsoft's "Family Sharing" feature lets you share your ONE
Minecraft purchase with your kid for free. Each of you signs in with your
own Microsoft account, but only one of you had to buy the game.

**One-time setup:**

1. Go to [family.microsoft.com](https://family.microsoft.com) and sign in
   with the Microsoft account that owns Minecraft.
2. Click "Add a family member" and either invite your child's existing
   Microsoft account, or create a free child account for them.
   - Note: For under-13 accounts in the US, Microsoft requires a one-time
     ~$0.50 verification charge to comply with COPPA child-safety law.
3. On your kid's PC: open the Microsoft Store, sign in with YOUR Microsoft
   account, find Minecraft (it'll show as "Owned" thanks to family sharing),
   and click Install.
4. After install, sign OUT of the Microsoft Store on your kid's PC (so no
   accidental purchases on your card).
5. Open the Minecraft Launcher on your kid's PC, sign in with your kid's
   Microsoft account.
6. Enable multiplayer permission for your kid: go to
   [account.xbox.com/settings](https://account.xbox.com/settings), select
   their account, click "Online Safety," and turn on multiplayer access.
   (This is OFF by default for child accounts.)

**Playing:**

- Both PCs on the same WiFi.
- Open Bedrock on each PC, signed in to its respective account.
- Connect to AubsCraft using `mc.spawndev.com` port `19132`.
- Plug in your Bluetooth Xbox controllers - Bedrock recognizes them
  automatically.
- You're playing together with your kid on AubsCraft, each on your own screen.

---

### Option B: One PC, One Account, Real Split-Screen via Java + Bedrock

**Best for:** Households with only one PC, AND you own the Java + Bedrock
bundle (the standard Minecraft purchase since 2022).

**The trick:** Java and Bedrock use DIFFERENT login systems. Even though
they share your Microsoft account, you can run BOTH at the same time on
the same machine without one kicking the other. AubsCraft accepts both,
and the Geyser plugin will show the Bedrock version of you with a `.`
prefix so you don't conflict with the Java version of you.

**Setup:**

1. In Minecraft Launcher, install both Java Edition and Bedrock Edition
   (both come with the bundled purchase).
2. For the Java side, install the **Controlify** mod for controller support:
   - Install the Fabric loader from [fabricmc.net](https://fabricmc.net/).
   - Download Controlify from [Modrinth](https://modrinth.com/mod/controlify)
     and drop it in your `.minecraft/mods` folder.
3. Launch Bedrock - put it in **windowed mode** (Settings → Video → Window
   Size: anything other than Fullscreen). Resize/position to take half the screen.
4. Launch Java in windowed mode. Position to the other half.
5. Plug in two Xbox controllers - one for each window. Bedrock auto-binds
   to one. In Java, configure the other in Options → Controls → Controller
   Settings (Controlify menu).
6. Both windows: connect to AubsCraft.
   - Java client: `mc.spawndev.com`
   - Bedrock client: `mc.spawndev.com` port `19132`
7. Click into the window you want active to control that player.

**Caveats:**

- This is a single-screen split-view via two windows, not true splitscreen.
- The active window receives keyboard input - clicking into a window switches
  control. Not perfect, but workable.
- Java + Bedrock characters look slightly different (different inventory
  UIs, slightly different physics in places). Both still play together fine.

---

### Option C: One PC, True Single-Window Split-Screen (Advanced)

**Best for:** Tech-savvy parents willing to manage an alpha-quality mod.

**The mod:** [Controlify Splitscreen](https://www.patreon.com/posts/controlify-alpha-127654960)
by isXander - actual console-style split-screen rendered in one Java window.
Two players, two viewports, two controllers, one screen.

**Status as of 2026:** Alpha. "Contains many bugs" (creator's own words).
Windows-only. Requires Minecraft 1.21.1+ on Fabric or NeoForge.

**Where to get it:**

- Official builds: subscriber-only on isXander's Patreon (~$5/month).
- Free fork: [PlayerRishi/ControlifySplitscreen](https://github.com/PlayerRishi/ControlifySplitscreen) -
  same code, but you'll likely need to build the JAR from source (clone
  repo, install JDK 21, run `./gradlew build`).

**Recommendation:** If you're not comfortable building a Java project from
source OR willing to subscribe to Patreon, skip this and use Option A or B.
This option exists for completeness; Options A and B are easier and more
reliable for most families.

---

### Option D: One PC, Two Windows, Fake Split-Screen via Nucleus Co-Op (Advanced)

**Best for:** Households on one PC who want Java only and don't want to
mess with the Java + Bedrock dual-client trick.

**The tools:**

- [Prism Launcher](https://prismlauncher.org/) - lets you run two Java
  Minecraft instances side-by-side.
- [Controlify](https://modrinth.com/mod/controlify) mod - controller
  support for Java.
- [Nucleus Co-Op](https://nucleus-coop.github.io/docs/games/minecraft/) -
  positions windows automatically and assigns one controller to each
  instance.

**Setup:** Follow the [Nucleus Co-Op Minecraft guide](https://nucleus-coop.github.io/docs/games/minecraft/).
You'll create two Prism instances, install Fabric + Controlify in both,
load Nucleus's Minecraft script, assign one controller per instance, and
launch.

**Caveats:** Mods break across Minecraft updates. Plan to refresh this
setup once or twice a year. Also requires either two Java accounts or
the launcher's offline mode (which only works for LAN, not online servers
like AubsCraft).

---

## Quick Decision Tree

```
Do you have two PCs / a desktop and a laptop?
├── YES → Option A (Family Sharing) - easiest, free, recommended.
└── NO → Do you have the Java & Bedrock bundle?
    ├── YES → Option B (Java + Bedrock dual-client) - free, works today.
    └── NO → Option C or D - both require technical setup.
```

---

## Troubleshooting

**"My kid's child account can't join multiplayer."**
→ Multiplayer is OFF by default for child Microsoft accounts. Turn it on at
[account.xbox.com/settings](https://account.xbox.com/settings) → Online
Safety → Allow Multiplayer.

**"Bedrock kicks me when my kid joins from the same account."**
→ You're both signed in to the same Microsoft account. Use Option A
(family sharing with separate accounts), not the same account.

**"Family Sharing didn't show Minecraft as Owned in the Microsoft Store."**
→ Make sure you signed into the Microsoft Store with the OWNER's account
(yours), not the child's. Install first, then sign out of the Store, THEN
have your kid sign into the Minecraft Launcher with their account.

**"My controller doesn't work in Java."**
→ Java doesn't support controllers natively. You need the Controlify mod.
See Options B and D for setup.

**"AubsCraft doesn't show up in my server list."**
→ Use the Direct Connect option and type `mc.spawndev.com` manually.
For Bedrock, also specify port `19132`.

---

## Splitscreen on Xbox: A Note

If you're coming from Xbox split-screen and wondering why PC is more
complicated: PC Minecraft has never had native split-screen (in either
edition). It's a console-only feature. Options A through D above are the
ways the community has filled that gap on PC.

The good news: AubsCraft is the same world either way. Whatever path you
pick, your kid can play with Aubs on the same server.

---

## Need Help?

If you're stuck on setup, ask TJ.

---

*Last verified: April 2026. Minecraft updates and Microsoft policy changes
can break these instructions over time - if something's outdated, let us know.*
