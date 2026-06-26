# Updating AubsCraft cross-play / cross-version plugins

When a Minecraft update rolls out, clients on the new version get **rejected**
until the bridge plugins are updated. This happens a few times a year:

- **Bedrock** clients (phone, console, Switch, Xbox, Quest/QuestCraft) rejected
  -> **Geyser** needs updating ("Geyser needs updating to support this version").
- **Newer Java** clients rejected -> **ViaVersion** needs updating.
- **Older Java** clients rejected -> **ViaBackwards** (and **ViaRewind** for very
  old 1.8.x / 1.7.10) keep them working.

The goal is that **any** Minecraft client - any edition, any version - can join.

---

## The easy way: run the updater

Double-click **`update-server-plugins.bat`** (or run `dotnet run sync-plugins.cs`).

It will, for each plugin:

1. Download the latest build (Geyser/Floodgate from GeyserMC; ViaVersion/
   ViaBackwards/ViaRewind - latest **stable** release - from Modrinth, matched to
   the server's Minecraft version).
2. Verify the jar is real (reads its `plugin.yml`).
3. Back up the current jar to `plugins-backup/` (dated).
4. Copy the new jar into `plugins/`.
5. **chmod the jars readable** over SSH (see gotcha below).
6. Restart `minecraft.service` and confirm each plugin loaded.

It prints a summary. The only thing it can't do is connect a real client - so the
last step is yours: join and confirm.

**Requirements** (same as `deploy-aubscraft.bat`): the aubscraft VM mounted at
`M:`, and `ssh aubscraft` configured (key-based, passwordless `sudo systemctl`).

---

## The plugins, and what each one does

| Plugin | Lets this client connect | Source |
|--------|--------------------------|--------|
| Geyser-Spigot | Bedrock edition (phone/console/Switch/Xbox/Quest) | download.geysermc.org |
| Floodgate-Spigot | authenticates Bedrock players (keep in lockstep with Geyser) | download.geysermc.org |
| ViaVersion | **newer** Java clients than the server | Modrinth `viaversion` |
| ViaBackwards | **older** Java clients than the server | Modrinth `viabackwards` |
| ViaRewind | very old Java clients (1.8.x / 1.7.10) | Modrinth `viarewind` |

The three Via plugins must be a **matched family** (e.g. ViaVersion 5.10.0 +
ViaBackwards 5.10.0 + ViaRewind 4.1.2). The updater picks the latest stable of
each for the server's MC version, which lines them up automatically.

---

## Important gotcha (why the script chmods)

New files written through the `M:` mount land **owner-only** (`zed`, `0700`),
which the `minecraft` service user **cannot read** - the plugin then fails to
load with `Permission denied` and that client stays locked out. Overwriting an
*existing* jar keeps its old readable perms, so only brand-new plugins hit this.

The script fixes it by running `chmod 0644` on the jars over SSH after copying.
If you ever copy a jar in by hand, do the same:

```bash
ssh aubscraft "chmod 0644 /opt/minecraft/server/plugins/<NewPlugin>.jar"
ssh aubscraft "sudo systemctl restart minecraft.service"
```

---

## Doing it by hand (fallback)

```bash
# Geyser + Floodgate (latest builds)
curl -fsSL -o /m/opt/minecraft/server/plugins/Geyser-Spigot.jar \
  "https://download.geysermc.org/v2/projects/geyser/versions/latest/builds/latest/downloads/spigot"
curl -fsSL -o /m/opt/minecraft/server/plugins/Floodgate-Spigot.jar \
  "https://download.geysermc.org/v2/projects/floodgate/versions/latest/builds/latest/downloads/spigot"

# Via family: grab the latest *release* jars for the server's MC version from
#   https://modrinth.com/plugin/viaversion  (and viabackwards, viarewind)
# Drop them in /m/opt/minecraft/server/plugins/

# Make them readable, then restart
ssh aubscraft "chmod 0644 /opt/minecraft/server/plugins/*.jar"
ssh aubscraft "sudo systemctl restart minecraft.service"
```

Verify in `M:\opt\minecraft\server\logs\latest.log`: look for
`Started Geyser on UDP port 19132`, `Enabling ViaBackwards`, and no
`Permission denied`.

---

## Rolling back

Old jars are kept in `M:\opt\minecraft\server\plugins-backup\` with a date stamp.
To roll one back, copy it over the current jar (drop the `.bak-...` suffix),
`chmod 0644` it over SSH, and restart.

---

*Server: Paper 1.21.5 at mc.spawndev.com. Bedrock port 19132. Last updated
procedure verified 2026-06-26 (Geyser 2.10.1, Via family 5.10.0 / 4.1.2).*
