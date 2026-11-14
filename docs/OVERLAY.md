# The in-game overlay

Kippix Helper includes an overlay you can open on top of any game you started from Playnite: achievements and guides,
screenshots and clips, sound, Discord, PC stats and power options, all with the controller.

**Contents:** [Opening it](#opening-and-closing) · [What's in it](#whats-in-it) ·
[Screenshots and clips](#screenshots-and-clips) · [Achievements and pop-ups](#achievements-and-pop-ups) ·
[Achievement guides](#achievement-guides) · [Discord](#discord) · [Settings](#settings) ·
[Problems](#if-something-isnt-right)

---

## Opening and closing

| How | |
|---|---|
| **Guide / Xbox / PS button** | Tap it while a game is running |
| **View + Menu** together | (Create + Options on PlayStation) |
| **Keyboard** | **Ctrl + Shift + O** |
| **Steam Controller / Steam Deck** | The Steam button (Steam may open its own overlay as well; you can also map any button to Ctrl + Shift + O in Steam Input) |

Close it the same way, with **Resume game**, or with **B** on the first page.

Which buttons open it is your choice (Guide, View + Menu, or either; either is the default). **Holding** the Guide button
for about 1.5 seconds opens a small menu to power off or hibernate instead (it closes the game first); you can switch this
off.

Works with Xbox and other XInput controllers (including anything Steam Input or DS4Windows presents as one), DualSense,
DualSense Edge and DualShock 4 (USB and Bluetooth), and the Steam Controller and Steam Deck.

**In the overlay**: D-pad or left stick to move, **A** to choose, **B** to go back, **LB / RB** to switch tabs. On a
keyboard: arrows, Enter, Esc, and Q / E or Tab for tabs.

The overlay opens only while a game is running (or a full-screen program other than Playnite is in front), so it never
gets in the way in Kippix itself.

### What happens to the game

Choose in the overlay's settings, under **When the overlay opens**:

- **Pause the game** (default): the overlay takes the focus, so games that pause when you switch away will pause.
- **Let it keep running**: the game keeps the focus and carries on. Good for online games, and for games that drop out
  of full screen or flash black when they lose focus. The game may also see your button presses.
- **Automatic**: online and MMO games keep running; everything else pauses. A game counts as online when Playnite lists a
  feature or genre with "online", "MMO" or "Massively Multiplayer".

The overlay never pauses, suspends, injects into or reads the game itself, and it's left out of your screenshots and
recordings.

---

## What's in it

Four tabs.

### Home

- **Now playing**: total play time and this session's, achievement progress, *How long to beat* times and your story
  progress.
- **Resume game**, **Open Playnite**, **Close game** (asks first; it asks the game to close, as its own close button would).
- **Achievements** (see below).
- **Keyboard**: an on-screen keyboard that types into the game.
- **Take a screenshot** and **Save recent gameplay** (see below).
- **Switch to**: your five most recent games (the current one is closed first).

### Party (Discord)

Start or open Discord, mute and deafen, and with a connection set up: your chats, quick replies and voice channels.
See [Discord](#discord).

### Audio

- **Sound plays on**: switch the output (speakers, headphones, TV…) without leaving the game.
- **Bluetooth headphones and speakers**: connect or disconnect a paired one.
- **Volume**: master volume, the game, and Discord, each with mute.

### Settings

- Live **PC stats**: CPU, GPU (temperature, load and watts on NVIDIA cards), memory, and how long the PC has been on.
- **Display and Bluetooth**: HDR on or off (one switch per display that supports it), Bluetooth on or off, and your
  paired Bluetooth headphones and speakers to connect or disconnect.
- **Storage** per drive, and **Downloads** (Steam with progress; Epic and Playnite installs).
- **Plugin settings**, **Update achievement guides**, **Shut down** and **Restart PC**.
  - *Shut down* hibernates the PC, so your game is still there when you turn it back on.

You also get notices when your controller battery is low and, with Discord connected, when a message comes in.

---

## Screenshots and clips

**Take a screenshot** saves a PNG to `Pictures\PlayniteGame` by default, named after the game and the time. You can change
the folder in the overlay's settings, or use another tool instead: Xbox Game Bar, Steam (F12), the NVIDIA app (Alt+F1) or
your own hotkey.

**Save recent gameplay** saves the last moments as a clip. Kippix doesn't record anything itself: it presses your
recorder's "save" hotkey and then finds the new clip. Choose the recorder in the overlay's settings:

- **NVIDIA Instant Replay** (Alt+F10), the default. Instant Replay must be switched on in the NVIDIA app.
- **Xbox Game Bar** ("Record what happened"). Background recording must be on in Windows' capture settings.
- **Your own hotkey**, or **Off** (the button is then hidden).

When the "Screenshot saved" or "Clip saved" notice appears, press **Guide** to open it straight away:

- **Photos**: crop (16:9, 21:9, 4:3, square, tall; saved as a copy), delete (to the Recycle Bin), done.
- **Clips**: back / forward 10 seconds, play / pause, save the current frame as a photo, delete, done.

Everything also shows up in Kippix's **Gallery**, sorted by game.

---

## Achievements and pop-ups

The **Achievements** page lists what you've unlocked and what's left, with dates and rarity, and a separate view for hidden
ones. Open any achievement to see its tip, or **Set as next achievement** to pin it to the overlay's first page. Missable
achievements are marked.

Achievements come from **SuccessStory** or **Playnite Achievements**, or for Steam games from Steam's own files on your PC
(no Steam key needed).

**Achievement pop-ups** appear in the game when you unlock something (needs Playnite Achievements). There are 7 designs
(Classic, Glass, Rarity edge, Pill, Banner, Spotlight, Kippix), 6 positions, 3–12 seconds on screen, progress updates and
your own unlock sound. Press **Guide** while one is showing to open that achievement. *Show a test pop-up* and *Show all
designs* in the overlay's settings let you try them.

---

## Achievement guides

Tips for every achievement, written by AI. **Off by default**; it needs your own key from one of these:

| Service | Cost | Get a key |
|---|---|---|
| **Google Gemini** | Free, with a daily limit (big libraries take a few days) | [aistudio.google.com](https://aistudio.google.com) |
| **OpenAI** | Paid API credit (separate from a ChatGPT subscription); spending cap per run, US$2 by default | [platform.openai.com](https://platform.openai.com) |

Set it up in Playnite's desktop mode › Add-ons › *Kippix Helper* › **Achievement guides**: pick the service, paste the key,
and press *Test key*. Tips can optionally use a web search for better answers.

Guides are fetched the first time you play a game, when Playnite starts (for new games), from a game's right-click menu in
desktop mode (Kippix › *Download achievement guides*), or from the overlay (Settings › *Update achievement guides*). You can
ask for a new tip for a single achievement from its page in the overlay.

**No key?** *Fill in guides yourself* exports a list you can complete yourself (or with any chatbot) and import back.

Good to know:

- AI tips can be wrong. Tips for hidden achievements stay covered until you choose **Reveal**.
- Your key is stored on your PC and only sent to the service you chose. With Gemini's free tier, Google may use the
  requests to improve its products.

---

## Discord

**Without any setup** you can start and open Discord, use the controller as a mouse in Discord, and mute or deafen. For
mute and deafen Kippix presses Discord's keybinds: set **Ctrl+Shift+M** (mute) and **Ctrl+Shift+D** (deafen) in Discord's
settings › Keybinds. A DualSense's mic button mutes Discord too.

**Chats, voice channels and message notices** need a one-time connection:

1. Go to [discord.com/developers](https://discord.com/developers/applications) and create an application (free; any name).
2. Under **OAuth2**, add the redirect `http://localhost`.
3. Copy the **Client ID** and **Client Secret** into Playnite's desktop mode › Add-ons › *Kippix Helper* › In-game
   overlay › Discord.
4. Restart Playnite. With Discord open, Discord asks once to **Authorise** Kippix.

The connection is saved on your PC (`discord-login.json` in the overlay's data folder). Discord's desktop app must be
running.

---

## Settings

Most overlay settings are in the overlay itself (Settings tab › *Plugin settings*) and in Playnite's desktop mode ›
Add-ons › *Kippix Helper* › **In-game overlay**. Some advanced ones (the keyboard shortcut, quick replies, hotkeys) are in
its config file, which *Edit config file* opens.

The overlay's data lives in Playnite's `ExtensionsData\b4a7d1c2-5e8f-4b9a-9c3d-2f6e1a8b7c55` folder (`config.json`, the
`guides` folder, and so on).

Don't want the overlay at all? Switch off **Settings › General settings › In-game overlay (Guide button in a game)** in
Kippix (takes effect the next time Playnite starts).

---

## If something isn't right

- **HDR or Bluetooth doesn't switch**: HDR needs a display (and cable) that supports it, and some games only notice the
  change after a restart. Bluetooth headphones need to be paired with Windows first and switched on.
- **The overlay doesn't open**: it only opens while a game started from Playnite is running (or a full-screen program
  other than Playnite is in front). Check which buttons are set to open it, or try **Ctrl+Shift+O**. If Steam's overlay
  opens instead, that's Steam's own setting for the Guide button.
- **The game minimises, flashes black or drops out of full screen**: set *When the overlay opens* to **Let it keep
  running**.
- **The game pauses and I don't want it to** (or an online game disconnects): use **Let it keep running** or **Automatic**.
- **"No achievements found"**: install SuccessStory or Playnite Achievements and let it fetch the game's achievements, or
  for a Steam game, start it once through Steam.
- **No GPU temperature or watts**: those come from NVIDIA's tools, so they show on NVIDIA cards only.
- **Save recent gameplay does nothing**: make sure the recorder you picked is set up and recording (NVIDIA Instant Replay
  switched on, or Game Bar background recording on), and that its hotkey is the one in the overlay's settings.
- **I had the old separate overlay add-on**: uninstall it. The overlay is built into Kippix Helper now, and the old add-on
  makes the overlay's settings read-only.
