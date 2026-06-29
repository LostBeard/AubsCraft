# QuestCraft: Simple Voice Chat Setup Guide

Follow these steps to enable proximity voice chat for Aubs and her friend on your private Ubuntu/VMware Minecraft server.

---

## Step 1: Download the Headset Mod File
You need the client-side mod file that matches the server's version (Minecraft 1.21.1) and mod engine (Fabric).
1. Open a browser on your PC.
2. Download this file: **Simple Voice Chat for Fabric 1.21.1** (Version 2.5.22 or matching).
3. Save the `.jar` file somewhere memorable on your PC.

---

## Step 2: Transfer the Mod File to the Headsets
Because Android hides app folders, you must use SideQuest to place the file.
1. Connect the Quest headset to your PC via a USB cable.
2. Put the headset on for a second and click **"Allow Access to Data"** if prompted inside VR.
3. Open **SideQuest** on your computer.
4. Click the **Folder Icon** (Manage files on device) in the top-right toolbar.
5. Navigate through this exact path:
   `Android` ➔ `data` ➔ `com.neofetch.questcraft` ➔ `files` ➔ `gamedir` ➔ `mods`
6. Drag and drop the downloaded `.jar` file from your PC directly into this `mods` folder.
7. *Repeat this entire step for the second headset.*

---

## Step 3: Grant Microphone Permissions
Android blocks background microphone use by default. You must force it on via SideQuest.
1. Keep the headset plugged into the PC with **SideQuest** open.
2. Click the **9-Square Grid Icon** (Currently Installed Apps) in the top-right toolbar.
3. Scroll down or search to find **QuestCraft**.
4. Click the **Gear Icon** right next to QuestCraft.
5. Scroll down to the permissions list and click **Grant/Allow** next to the **Microphone** setting.
6. *Repeat this entire step for the second headset.*

---

## Step 4: Map the Menu Button & Turn on Voice Activation
Now that the files and permissions are ready, configure the settings inside the VR game.
1. Put on the headset, launch QuestCraft, and join the server.
2. Look at the **bottom-left corner** of the screen. You should now see a small grey microphone icon.
3. Press the **Left Touch Controller Menu Button** to open the QuestCraft pause menu.
4. Go to **Options** ➔ **Controls** ➔ **Keybinds**.
5. Scroll down to the **Voice Chat** section and find **"Open Voice Chat Menu"** (assigned to 'V' by default).
6. Click it, then press a controller button (like clicking down the **Right Thumbstick**) to rebind it.
7. Save, return to the game, and press your new button to open the circular Voice Chat menu.
8. Click the **Settings (Gear Icon)**.
9. Change the **Activation Type** from *Push-to-Talk* to **Voice Activation**.
10. Talk out loud and adjust the **Microphone Threshold slider** so the bar turns green when speaking, but stays grey when quiet.
    
---
    
# PC Java Edition: Simple Voice Chat Setup Guide

Follow these steps to connect your PC Minecraft game to the server's proximity voice chat.

---

## Step 1: Download the Required Files
You need two free files to make this work. They must match Minecraft version **1.21.1**.

1. **The Fabric Installer**: Go to [fabricmc.net/use/installer](https://fabricmc.net) and download the **Windows .EXE installer**.
2. **The Voice Chat Mod**: Go to Modrinth and download [Simple Voice Chat for Fabric 1.21.1](https://modrinth.com). 

---

## Step 2: Install the Fabric Mod Loader
1. Make sure your official Minecraft Launcher is completely closed.
2. Double-click the downloaded **Fabric Installer** file on your PC.
3. In the installer window, make sure the Minecraft Version is set to **1.21.1**.
4. Click **Install**. 
5. Once it finishes, close the installer.

---

## Step 3: Put the Mod into Your Minecraft Folder
1. Press the **Windows Key + R** on your keyboard to open the "Run" box.
2. Type exactly `path:%appdata%\.minecraft` and press **Enter**.
3. Look for a folder named **`mods`**. 
   *(If you do not see a `mods` folder, right-click in the empty space, select **New > Folder**, and name it exactly `mods`).*
4. Open the `mods` folder and drag the downloaded **`voicechat-fabric-1.21.1-....jar`** file directly into it.

---

## Step 4: Launch the Game & Turn on Voice Activation
1. Open your standard **Minecraft Launcher**.
2. In the bottom-left dropdown menu (next to the green Play button), make sure **Fabric Loader 1.21.1** is selected.
3. Click **Play** and join the server.
4. Once inside the server, look at the **bottom-left corner** of your screen. You should see a small grey microphone icon.
5. Press the **V** key on your keyboard to open the circular Voice Chat menu.
6. Click the **Settings (Gear Icon)**.
7. Change the **Activation Type** from *Push-to-Talk* to **Voice Activation**.
8. Speak out loud into your microphone and adjust the **Microphone Threshold slider** so the bar turns green only when you talk, and stays grey when your room is quiet.
