# Kippix user guide

Everything from installing Kippix to making it your own. For every setting in detail see the
[Settings reference](SETTINGS.md); for the in-game overlay see [In-game overlay](OVERLAY.md); quick answers are in the
[FAQ](FAQ.md).

**Contents**

1. [Install](#1-install)
2. [The Getting started tour](#2-the-getting-started-tour)
3. [Controls](#3-controls)
4. [Finding your way around](#4-finding-your-way-around)
5. [Make the home screen yours](#5-make-the-home-screen-yours)
6. [Game pages](#6-game-pages)
7. [Store and wishlist](#7-store-and-wishlist)
8. [Gallery, screenshots and clips](#8-gallery-screenshots-and-clips)
9. [Save backups](#9-save-backups)
10. [Profiles and time limits](#10-profiles-and-time-limits)
11. [Kippix's page in Playnite's desktop settings](#11-kippixs-page-in-playnites-desktop-settings)
12. [Troubleshooting](#12-troubleshooting)
13. [Updating and uninstalling](#13-updating-and-uninstalling)

---

## 1. Install

### What you need

- Windows 10 or 11
- [Playnite](https://playnite.link) 10 or newer. Either the installed or the portable version works. If you're on an
  older Playnite, update it first (Playnite's menu › Help › Check for updates).
- Your games added to Playnite. Kippix shows your Playnite library, so connect your stores in Playnite first
  (desktop mode › Add-ons › Libraries) if you haven't yet.

### Step by step

1. Go to the [latest release](../../../releases/latest) and download **both** files under *Assets*:
   - **`Kippix…pthm`**: the theme
   - **`KippixHelper…pext`**: Kippix Helper, the add-on that powers the theme (and includes the in-game overlay)
2. Start Playnite (desktop mode is easiest for this).
3. Double-click the `.pthm` file. Playnite asks if you want to install the theme: choose **Yes**.
   Then do the same with the `.pext` file.
   - If double-clicking doesn't open Playnite (common with the portable version), drag each file onto the Playnite
     window instead.
4. Restart Playnite when it asks you to.
5. Switch to fullscreen mode (the controller icon at the top of desktop mode, or press F11).
6. Open Playnite's menu (the Back / View button on a controller, or click the Playnite icon at the top), go to
   **Settings › Visuals › Theme**, pick **Kippix**, close the menu and let Playnite restart.

That's it: Kippix starts and the [Getting started tour](#2-the-getting-started-tour) opens.

> **Always install the theme and the add-on from the same release.** Kippix checks this for you (Settings › Help ›
> Kippix Doctor) and warns if they don't match.

### Want Playnite to start in fullscreen?

That's Playnite's own setting: in desktop mode, Playnite's menu › Settings, look for the option to start in fullscreen mode. Kippix's
settings also have a shortcut for going back to desktop mode: **Settings › Playnite › Desktop mode**.

### Recommended add-ons

Kippix works without any other add-ons, but these make it much richer. Install them from Playnite's desktop mode
(Add-ons › Browse › Generic) and restart Playnite:

- **SuccessStory** or **Playnite Achievements**: achievements everywhere, and achievement pop-ups while you play
- **HowLongToBeat**: how long each game takes, and Kippix's story-progress estimate
- **Extra Metadata Loader**: game logos (used on game pages and cards) and trailers
- **Ludusavi** (plus the [Ludusavi program](https://github.com/mtkennerly/ludusavi)): save backups
- **PlayerActivities**: which of your friends play a game (Steam, GOG, Epic, EA)

The full list is in the [README](../README.md#works-with).

---

## 2. The Getting started tour

The first time Kippix starts, a short tour walks you through the main choices. Use the D-pad and A, or
**Back** / **Next**. Everything you pick here can be changed later in Settings.

The tour has to be finished once before you get to the home screen. If you stop part-way (or close Playnite), it starts
again the next time Kippix starts. After that it never appears by itself again.

1. **Welcome**: a few things that are good to know.
2. **Your look**: pick a layout (Row, List, Grid, Board or Manual board), then for Row a design (Lower, Tall or
   Full Screen), then a preset. Press X to see a photo of a preset full size.
3. **Style**: the glow around the highlighted game (none, soft, neon or a dark shadow), colours taken from the game's art,
   and the background: the game's art, its trailer (muted), a slideshow, a trailer preview, the game's tile or a plain
   colour. You see each choice live behind the tour.
4. **Game page**: the default design (logo and buttons on the left, cover or trailer on the right) or the alternative one
   (cover or trailer on top, info centred).
5. **Stores**: which of your Playnite store add-ons are signed in (press A on one to open its settings), and signing in to
   the Steam store for your wishlist and prices.
6. **About you**: your name and picture (and, with profiles switched on, who else plays).
7. **All set**: press **Start playing**.

Want it again later? **Settings › Help › Take the tour again**. The first time you open each new screen you'll also see a
short tip; Help can switch those off or show them again.

---

## 3. Controls

Kippix is made for a controller, but a keyboard, a mouse and a touch screen all work too.

### Controller

| Button | What it does |
|---|---|
| D-pad / left stick | Move around |
| **A** | Choose; on a game: open its game page (or play it, if you've set that up) |
| **B** | Back. On the home screen: back to the first game (or the top bar; you can change this) |
| **Hold B** | Go back to the home screen from anywhere |
| **Hold A** on something | Pick it up to move it (Free move) |
| **X / Y** | Shown on screen where they do something (for example the game's options) |
| **LB / RB** | Switch tabs and pages |
| **Guide / Home / PS** | Out of a game: back to the home screen and the Guide quick bar. In a game: the [in-game overlay](OVERLAY.md) |
| Back / View | Playnite's own menu |

The hints along the bottom of the screen always show what each button does where you are. Button icons can be Xbox,
PlayStation, Nintendo Switch or keyboard, or follow whatever you're using (Settings › General settings › Look and feel ›
Button icons).

Almost any Kippix action can be moved to a different button: **Settings › General settings › Controller buttons**.
(Playnite's own A, B, D-pad and triggers stay where they are. To swap A and B, use Playnite's own input settings.)

### Steam Controller and Steam Deck

Supported out of the box. Steam sends some presses twice (as the controller and as keyboard keys); Kippix filters out
the duplicates. If you used the old separate *KippixSteamController* add-on, you can remove it: it's built in now.

### Keyboard

Arrow keys to move, **Enter** to choose, **Esc** or **Backspace** to go back.

### Mouse and touch

Click or tap to choose; the mouse wheel scrolls; right-click is Back. On touch screens swipe to scroll, swipe in from the
left edge to go back and long-press instead of hold A. Settings › General settings › Controller › *Handheld, touch and
mouse* has the details, including where the mouse pointer goes when Playnite starts (out of sight, where it was, or the
middle of the screen).

---

## 4. Finding your way around

### The home screen

Your games, in the layout you picked. The highlighted game's logo and details show above or beside it, with the button
hints at the bottom.

### The top bar

Along the top of the screen. From the left:

- **Your profile** (your picture): your profile, achievements, the Store, the Gallery, media apps (Netflix, YouTube, Prime
  Video, Crunchyroll), more pages (release calendar, news, friends, collections, "pick for me", storage, split-screen
  games, controller test, your year in review…) and power options.
- **Shortcuts** you choose: Store, Achievements, Gallery, Search, Downloads, Sound output, Bluetooth, Quick settings, and
  more. **Quick settings** has volume, brightness, HDR, Wi-Fi, Bluetooth and battery in one place.
- **⋯ Settings & Customisation**: all of Kippix's settings.
- The clock, controller battery and notifications.

The top bar can hide itself until you move up to it (Settings › Top bar › Auto-hide).

### Settings

Press the **⋯** icon in the top bar. Settings are grouped into:

- **Look and screens**: Home settings, Game page, Top bar, Store settings
- **Everywhere**: Audio, General settings, Notifications
- **Your setup**: Layouts & reset, Accessibility, Profiles
- **Community**
- **Help**

Can't find something? **Search settings** is at the top of the menu. A setting only shows when it applies (for example,
trailer options only appear once trailers are switched on). Every page has **Reset this page** at the bottom.

See the [Settings reference](SETTINGS.md) for every page.

### Bluetooth and HDR

- **Bluetooth** (a top bar shortcut, Quick settings, or your profile › This PC): turn Bluetooth on or off, and connect or
  disconnect paired headphones and speakers. Pairing something new opens Windows' Bluetooth settings. Controllers connect
  from the controller itself.
- **HDR**: Quick settings has an HDR switch for each display that supports it. The screen goes dark for a moment while it
  switches, and some games only notice after a restart. Both are in the in-game overlay too.

### Playnite's own menu

Playnite's menu (Back / View button) is still there for Playnite's own things: updating your library, Playnite's
settings, switching to desktop mode and exiting. Kippix has shortcuts to it in **Settings › Playnite**.

---

## 5. Make the home screen yours

### Pick a layout

**Settings › Home settings › Game cards and layout › Games shown as**:

| Layout | What it's like |
|---|---|
| **Row** | One row of games, console-style. Three designs: **Lower** (the row near the bottom, the art above), **Tall** (tall panels across the screen) and **Full Screen** (the game's art fills the screen) |
| **Grid** | Rows of covers that scroll down |
| **List** | A list of games with wide pictures, the art beside it |
| **Board** | Tiles of different sizes, filled from sources you choose (recent, favourites, a collection, widgets…), in pages |
| **Manual board** | You place every tile yourself, with crop, groups and several pages |
| **Folders** *(work in progress)* | Folder tiles first, then your games inside them |

Each layout remembers its own arrangement and background, so you can switch between them freely.

### Use a preset

Every layout has ready-made presets (for example *Cinema* and *Minimal* for Row Lower, *Library wall* and *Showroom* for
Grid, *Dashboard* and *Shelves* for Board) next to **Default**, which is your own arrangement. Any changes you make to a
preset are kept for that preset. Find them under Home settings, or in the tour.

### Move anything: Free move

**Hold A** on any part of the screen (or Home settings › *Free move*) to pick it up:

- **D-pad / stick**: move it (it snaps to a grid; X switches between grid and free)
- **Y**: resize it; **LT / RT**: rotate it
- **X**: that part's settings
- **B**: done

Moved something you shouldn't have? **Home settings › Put things back** puts parts back one by one, and every page has
*Reset this page*. Free move works on the game page too.

### Widgets, rows and tiles

- **Widgets** (Home settings › Widgets): weather, clock and date, game suggestions, a spotlight on one game, free games this
  week, wishlist deals, wishlist releases, downloads, friends online, PC stats, your latest capture, and your own text or
  picture tiles. Place them with Free move.
- **Extra rows** (Home settings): Favourites, Recently added, Installed, Never played, Up next, your shelves, and your
  Playnite saved filters.
- **Home tiles**: Library, Store, Achievements and Up next tiles before your games (hidden, shown when you move left past the
  first game, or always shown).
- **Games kept in place**: pin a game to a fixed spot so it doesn't move as you play other games.

### Background and look

- **Background** (Home settings › Background): the game's art, a muted trailer, a slideshow, a trailer preview, the game's
  tile, your own picture or a plain colour; a fade to black (which edge and how far), its position, and motion (a slow zoom
  and depth).
- **Effects**: seasonal effects, and weather effects that follow your real weather or a schedule.
- **Look and feel** (General settings): look packs (complete colour, font, card and panel styles), **Theme Studio** to make
  your own, the accent colour, night mode, screensaver, button icons, and an intro or outro video of your own.
- **Accessibility**: bigger text, less motion and high contrast.

### Save and share layouts

**Settings › Layouts & reset** keeps named copies of your layout, so you can try things and go back. **Community** lets
you get layouts, look packs and widgets other people have shared, and share your own: **Yours › Upload** sends yours to the
gallery (sign in to GitHub once, with a code on your phone or PC), and it shows for everyone once it's approved.

---

## 6. Game pages

Press A on a game to open its page. The top page has the logo, buttons, your play time and the game's art or trailer.
Press down for the lower page, with cards such as:

- **Achievements** and **story progress** (with SuccessStory or Playnite Achievements)
- **How long to beat** (with the HowLongToBeat add-on)
- **Play history**, **notes**, **scores** and **status**
- **Your captures** of this game
- **Save backups** (with Ludusavi, see [Save backups](#9-save-backups))
- **News** and **game updates**, **mods**, **friends who play it**
- **More like this in your library**

Set it up in **Settings › Game page**: layout (default or alternative), what media shows (cover, trailer, autoplay, sound),
which lines and cards appear, buttons and extras, friends, and the **Animation** when the page opens (Off, or one of 10
styles: Rise, Fade, From the sides, Drop in, Zoom in, Settle, Cinematic, Cascade, Pop, Focus). Hold A on the page to move
or resize any part.

Would you rather A started the game straight away? Home settings › *Pressing A on a game*.

---

## 7. Store and wishlist

Open the **Store** from the top bar or your profile menu.

- **Steam** and **GOG** can be browsed fully in Kippix: search, genres and tags, sort, price filters, sales, game pages
  with pictures, trailers, reviews, system requirements (checked against your PC), add-ons and editions.
- **Epic** shows its free games; **EA, Ubisoft, Battle.net, Microsoft, Humble, Fanatical and GMG** are listed through
  IsThereAnyDeal; **Rockstar** and **itch.io** open their websites.
- **Prices** come from IsThereAnyDeal across official stores (resellers are optional), with the lowest price ever and price
  history.
- **Your wishlist**: sign in to the Steam store once (the tour or Store settings) and import your Steam and GOG wishlists.
  Kippix can tell you when something on it goes on sale or comes out.

Store settings (the Store's gear icon, or Settings › Store settings) set which stores show, your currency, and whether store
pages open in Kippix's own browser.

---

## 8. Gallery, screenshots and clips

The **Gallery** (top bar or profile menu) shows your screenshots and clips by game: from the overlay, Steam, NVIDIA,
Xbox Game Bar and your Pictures and Videos folders.

- Open a photo or clip, use the **Edit** button to crop, rotate, filter or adjust a photo, or trim a clip and save a frame
  as a photo. Edits are saved as copies.
- **Hold A** on a tile to pick several, then delete them or move them to another game.
- Mark favourites, run a slideshow, or use a photo as the game's background.

Taking captures while you play is covered in [In-game overlay](OVERLAY.md#screenshots-and-clips).

---

## 9. Save backups

Kippix can back up and restore your saves with [Ludusavi](https://github.com/mtkennerly/ludusavi).

**Set up**

1. Install the **Ludusavi** add-on in Playnite (desktop mode › Add-ons › Browse) and the Ludusavi program, and set it up
   in the add-on's settings (the add-on's own guide explains this).
2. Restart Playnite. A **Save backups** card appears on game pages.

**Use it**

- **Back up now**: makes a backup of this game's saves.
- **Restore a backup**: lists every backup Ludusavi has for the game, newest first, with the date and time. Pick one and
  confirm. Your current saves are replaced by that backup.
- **Automatic backups**: before and/or after you play.

Backups are kept where Ludusavi keeps them (by default `%USERPROFILE%\ludusavi-playnite`). How many are kept is set in
Ludusavi.

---

## 10. Profiles and time limits

> Profiles are a preview feature. Switch them on in Playnite's **desktop mode** › Add-ons › Kippix Helper › Testing ›
> *Profiles*.

With profiles on, Kippix asks **Who's playing?** when it starts. Each profile has its own name and picture, its own
layout and settings, and optionally:

- which games and pages it can open, and age ratings
- a **daily time limit** (separately for weekdays and the weekend) and hours when games may be played. At 10 and 2
  minutes left a message appears (in the game too). When time's up, games can't be started; a game still running can be
  asked to close (it's asked nicely, as if you pressed its close button, never forced).
- a **grown-ups' PIN** to protect all of this. A grown-up can add 15, 30 or 60 minutes for today with the PIN.

Forgot the PIN? Desktop mode › Add-ons › Kippix Helper › *Forgotten your PIN?* removes it; the locks ask for a new PIN.

---

## 11. Kippix's page in Playnite's desktop settings

Some things are set from Playnite's **desktop mode**: Playnite's menu › Add-ons › *Kippix Helper* (or Settings ›
Add-ons). It has four tabs:

- **Kippix**
  - **Friends on the game page**, and an optional **Steam Web API key**. With a key, Kippix also shows which Steam
    friends are playing right now and when they last played. Get a free key at
    [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey) (any domain name works, for example
    `localhost`). The key is stored encrypted for your Windows user and only sent to Steam.
  - **Layouts**: reset the home screen, the game page or everything, and undo the last reset.
  - **Forgotten your PIN?**
  - **Testing**: profiles, showing the tour at every start, and where the Community gallery comes from.
- **In-game overlay**: everything about the overlay. See [In-game overlay](OVERLAY.md).
- **Achievement guides**: AI-written tips for achievements (optional, needs your own key). See
  [Achievement guides](OVERLAY.md#achievement-guides).
- **Doctor**: health checks, safe mode, and a bug report (see below).

---

## 12. Troubleshooting

**Kippix Doctor** checks the usual problems for you: **Settings › Help › Kippix Doctor** in fullscreen, or the *Doctor*
tab in desktop mode. It checks your Playnite version, that the theme and add-on match and are both installed, that Kippix
is the active theme, add-ons that clash with Kippix, your settings file, free disk space and recent errors.

**Safe mode** starts Kippix with widgets, live backgrounds, the overlay and animations switched off, so you can get in and
turn off whatever is causing trouble. Turn it on in the Doctor tab. Kippix also offers it by itself after two failed
starts.

**Reset**: Settings › Layouts & reset (or desktop mode › Kippix Helper › Layouts) can reset the home screen, the game page
or everything. Your wishlist, keys and play history are kept, and the last reset can be undone.

**Reporting a bug**: Settings › Help › *Save a log* (or *Make a bug report* in the Doctor tab) saves Playnite's
recent logs, your Kippix settings with keys and personal details removed, and your versions into
`Documents\Kippix Logs`. Attach that to a [new issue](../../../issues/new/choose) with what you did and what happened.

More answers in the [FAQ](FAQ.md).

---

## 13. Updating and uninstalling

### Updating

Open **Settings › Help**: it checks for a new version by itself. When there is one, **Install version …** appears at the
top. Press it: Kippix downloads the theme and the helper from GitHub and Playnite restarts to put them in. Your settings,
layouts and everything else are kept.

You can also update by hand: download both files from the [latest release](../../../releases/latest) and install them the
same way as the first time (double-click or drag onto Playnite), then restart Playnite.

### Uninstalling

1. In desktop mode: Playnite's menu › Add-ons › Installed › Generic › *Kippix Helper* › Uninstall. Themes are under
   *Themes Fullscreen* › *Kippix*.
2. Pick another fullscreen theme (fullscreen mode › Playnite's menu › Settings › Visuals › Theme).
3. Restart Playnite.

Your settings stay in Playnite's `ExtensionsData\3f6c2d1e-8a47-4b9e-b1d5-7e2a9c4f0b18` folder (and the overlay's in
`ExtensionsData\b4a7d1c2-5e8f-4b9a-9c3d-2f6e1a8b7c55`), and your saved layouts in `Documents\Kippix Layouts`, in case
you come back. Delete those folders to remove everything.
