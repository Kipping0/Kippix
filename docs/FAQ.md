# FAQ

**Contents:** [General](#general) · [Installing and updating](#installing-and-updating) · [Using Kippix](#using-kippix) ·
[Controllers](#controllers) · [The overlay](#the-overlay) · [Store and prices](#store-and-prices) ·
[Privacy and data](#privacy-and-data) · [Problems](#problems)

---

## General

### What is Kippix?
A fullscreen theme for [Playnite](https://playnite.link), the free game launcher that brings all your games together.
Kippix turns Playnite's fullscreen mode into a console-style home screen for your TV, desk or handheld, with its own
in-game overlay.

### Is it free?
Yes.

### Does it replace Steam, Epic, Xbox and the other launchers?
No. Playnite shows your games from all of them in one place and starts them through their own launchers, which still need
to be installed. Kippix is how that looks and works in fullscreen mode.

### Does it work in Playnite's desktop mode?
Kippix is a **fullscreen** theme. Desktop mode keeps whatever desktop theme you use. Some Kippix settings (keys, the
overlay, Doctor) are set from desktop mode, under Add-ons › Kippix Helper.

### Why are there two files to install?
**Kippix** (the `.pthm`) is the theme: how everything looks. **Kippix Helper** (the `.pext`) is an add-on that does
everything a theme can't do on its own: layouts, widgets, the Store, the overlay and so on. They need each other, and they
must be from the same release.

### Which languages are there?
English for now. Kippix follows Playnite's language setting, so more languages can be added by translating one file
(`Localization/en_US.xaml`). Translations are welcome.

---

## Installing and updating

### Which Playnite version do I need?
Playnite 10 or newer, installed or portable.

### Double-clicking the files doesn't do anything.
Drag them onto the Playnite window instead (this is normal with the portable version of Playnite). See the
[install guide](GUIDE.md#1-install).

### I installed it but fullscreen still looks like the default theme.
Choose Kippix as the fullscreen theme: in fullscreen mode open Playnite's menu (Back / View button) › Settings › Visuals ›
Theme › **Kippix**, then let Playnite restart.

### Kippix says the theme and the helper don't match.
Download both files from the same release and install them again, then restart Playnite.

### How do I update?
Open Settings › **Help**: it checks by itself, and when there's a new version **Install version …** appears at the top. It
downloads both files and restarts Playnite to finish. (You can also download both files from GitHub and install them over the
old ones.)
Nothing you've set up is lost.

### Can I skip the Getting started tour?
Not the first time: it sets up your layout, style, game page and stores, and it comes back each time Kippix starts until
you've finished it once. It takes a couple of minutes, and everything can be changed later. After that you can take it
again from Settings › Help, with a Skip button.

### Will updating reset my layout?
No. Your settings are kept in Playnite's data folder, and named layouts in `Documents\Kippix Layouts`.

### How do I uninstall it?
See [Updating and uninstalling](GUIDE.md#13-updating-and-uninstalling).

### Do I need any other add-ons?
No, but some add more: SuccessStory or Playnite Achievements (achievements), HowLongToBeat, Extra Metadata Loader (logos
and trailers), Ludusavi (save backups), PlayerActivities (friends) and others. See the
[list in the README](../README.md#works-with).

---

## Using Kippix

### Where are the settings?
The **⋯** icon in the top bar (*Settings & Customisation*). Use **Search settings** at the top to find anything.

### How do I change the layout?
Settings › Home settings › Game cards and layout › **Games shown as**. Or run the tour again (Settings › Help › Take the
tour again).

### How do I move or hide something on the screen?
**Hold A** on it (Free move). Move it with the D-pad, Y resizes, LT / RT rotate, X opens its settings, B when you're done.
Settings › Home settings › **Put things back** undoes moves.

### I've made a mess of my layout. How do I start again?
Settings › Layouts & reset › reset the home screen, the game page or everything. You can undo the last reset. Saving your
layout first (Layouts & reset › Your layouts) means you can always go back.

### Can I have a different background for each layout?
Yes: each layout remembers its own background position and arrangement.

### How do I play a game straight from the home screen without its page?
Settings › Home settings › **Pressing A on a game** › play straight away.

### There's no logo for some games.
Logos come from **Extra Metadata Loader**. Install it and let it download logos for your library.

### Why don't I see achievements / how long to beat / friends?
Each needs its add-on: SuccessStory or Playnite Achievements, HowLongToBeat, and PlayerActivities. Settings › General
settings › **Other add-ons** shows what Kippix has found.

### How does the "Story ~%" estimate work?
It's a rough guess at how far through the main story you are, because PC games don't report that themselves. Kippix
looks, in order, at whether you've marked the game finished, at the story achievements you've unlocked (chapters, acts,
"finish the story"), and otherwise at your play time compared with *How long to beat*'s main-story time. It shows
"Story ~…%" while it works it out, and "~?%" when there's nothing to go on.

### Can I share my layout?
Yes: Settings › **Community** › Yours › **Upload**. Sign in to GitHub once (a free account; you enter a code on your phone
or PC) and it goes straight to the gallery, where it shows once it's approved. You can also get other people's layouts, look
packs, game page designs and widgets there, sorted by newest or most downloaded.

### Why does uploading ask to access public repositories?
That's how GitHub names the permission Kippix needs to make your copy of the gallery and send your upload from it as a
request. Kippix uses it for nothing else, the sign-in stays on your PC (encrypted), and you can sign out on Yours or remove
Kippix at github.com › Settings › Applications.

### Can I use my own intro video?
Yes: Settings › General settings › Look and feel › Intro and outro › your own video (MP4, WMV or GIF). Kippix doesn't
include an intro of its own; it's off by default.

### Does Kippix have a screensaver / night mode?
Both: Settings › General settings › Look and feel.

### Can kids have their own profile with time limits?
Yes, with **Profiles** (a preview feature, switched on in desktop mode › Add-ons › Kippix Helper › Testing). Each profile can
have daily limits, playing hours, age ratings and its own layout, protected by a grown-ups' PIN. See
[Profiles and time limits](GUIDE.md#10-profiles-and-time-limits).

### How do I restore an older save backup?
On the game page, **Save backups › Restore a backup** lists every backup Ludusavi has for the game, with its date and time.
See [Save backups](GUIDE.md#9-save-backups).

---

## Controllers

### Which controllers work?
Anything Playnite supports, such as Xbox and other XInput controllers and PlayStation controllers (DualSense,
DualShock 4), plus the Steam Controller and Steam Deck, which Kippix supports specially. Keyboard, mouse and touch work
too.

### The button icons show the wrong controller.
Settings › General settings › Look and feel › **Button icons**: pick Xbox, PlayStation, Switch or keyboard, or leave it on
automatic to follow what you're using.

### Can I change which button does what?
Kippix's own actions, yes: Settings › General settings › **Controller buttons**. Playnite's A, B, D-pad and triggers are
fixed; you can swap A and B in Playnite's own input settings.

### My Steam Controller presses things twice.
Kippix filters Steam's duplicate key presses for you. If you still have the old *KippixSteamController* add-on installed,
remove it: it's built into Kippix now, and the two together can clash.

### What does B do on the home screen?
It takes you back to the first game (or the top bar, or nothing: Settings › General settings › Controller). **Holding B**
goes back to the home screen from anywhere.

### Does it work on a handheld (ROG Ally, Legion Go, Steam Deck with Windows…)?
Yes. **Handheld mode** (on, off or automatic for small screens with a battery) makes things more compact and saves battery,
and touch works throughout. Kippix adapts to screen shapes other than 16:9, such as 16:10.

---

## The overlay

### How do I open the overlay?
In a game started from Playnite, press the **Guide / Xbox / PS** button, **View + Menu** together, or **Ctrl+Shift+O**. See
[In-game overlay](OVERLAY.md).

### The overlay opens and my game minimises or goes black.
In the overlay's settings set *When the overlay opens* to **Let it keep running**.

### Is the overlay safe with anti-cheat and online games?
The overlay is a separate window. It doesn't inject into, read or change the game. For online games use **Let it keep
running** or **Automatic** so the game isn't paused. As with any overlay, it's your call for games with strict anti-cheat
rules.

### Does the overlay record video?
No. *Save recent gameplay* tells your recorder (NVIDIA Instant Replay, Xbox Game Bar or your own hotkey) to save, then
shows the clip. Screenshots are taken by Kippix itself, or by the tool you choose.

### Where are my screenshots?
`Pictures\PlayniteGame` by default (changeable in the overlay's settings). They also show in Kippix's **Gallery**.

### Do achievement guides cost anything?
They're off by default. With **Google Gemini** they're free (with a daily limit); with **OpenAI** they use paid API credit,
with a spending cap. You can also fill them in yourself without any key. See
[Achievement guides](OVERLAY.md#achievement-guides).

### Can I turn the overlay off?
Yes: Settings › General settings › **In-game overlay**. It takes effect the next time Playnite starts.

### Can I switch HDR or connect my Bluetooth headphones from a game?
Yes: the overlay's Settings tab has HDR and Bluetooth, and the Audio tab lists your Bluetooth headphones and speakers. In
Kippix itself they're in Quick settings and on the Bluetooth page. Headphones need to be paired with Windows once first.

---

## Store and prices

### Can I buy games in Kippix?
Kippix lets you browse stores and compare prices. Buying opens the store's own page (in Kippix's browser or the store's
app), so you always buy from the store itself.

### Where do the prices come from?
[IsThereAnyDeal](https://isthereanydeal.com), across official stores (Steam, Epic, GOG, EA, Ubisoft, Battle.net, Microsoft).
Resellers such as Humble, Fanatical and GMG can be added in Store settings. Key resale sites aren't included.

### The prices are in the wrong currency.
Store settings › **Prices in**: pick your country, or leave it on automatic (Windows' region).

### How do I get my wishlist in?
Sign in to the Steam store once (in the tour, or Store settings), and import your Steam and GOG wishlists. Kippix can tell
you about sales and releases on it.

---

## Privacy and data

### Does Kippix collect anything about me?
No. There are no accounts, no ads, no analytics and nothing is sent to the author. Kippix only contacts public services to
show you things:

| Service | What for | What's sent |
|---|---|---|
| Steam, GOG, Epic | Store pages, prices, news, player counts, your wishlist | Game IDs or names; your Steam ID and Steam key go only to Steam |
| IsThereAnyDeal, CheapShark | Prices, deals, price history | Game IDs or names, your country |
| Open-Meteo, postcodes.io, OpenStreetMap | The weather widget and weather effects | The town you typed |
| PCGamingWiki, Nexus Mods | Split-screen games, mods | Game names |
| GitHub | Update checks, the Community gallery and download counts | Nothing personal; if you upload, your GitHub name shows on your upload |
| Discord, OpenAI or Gemini | Only if you set them up | What's needed for that feature, using your own app or key |

Store and media icons may be fetched from Google's or DuckDuckGo's favicon services.

### Where are my keys kept?
On your PC. The Steam Web API key is encrypted for your Windows user and only sent to Steam. The Discord and AI keys are
in the overlay's config file and only sent to Discord or the AI service you chose.

### What's in a bug report?
Playnite's recent logs, your Kippix settings with keys and personal details taken out, and your Windows, Playnite and
Kippix versions. It's saved on your PC (`Documents\Kippix Logs`) and goes nowhere unless you attach it to an issue yourself.

### Does Kippix change my games or Steam?
No, with one exception you choose yourself: *Update Steam games straight away* (profile menu, or automatically in General
settings › Steam) sets your installed Steam games to update first. It closes Steam briefly to do this, and never while a game
is running. With profiles switched on, Kippix can also switch which Steam account signs in; it never sees passwords.

### Where does Kippix keep its files?
- Settings and cache: Playnite's `ExtensionsData\3f6c2d1e-8a47-4b9e-b1d5-7e2a9c4f0b18` folder
- The overlay: `ExtensionsData\b4a7d1c2-5e8f-4b9a-9c3d-2f6e1a8b7c55`
- Your saved and shared layouts: `Documents\Kippix Layouts`
- Bug reports: `Documents\Kippix Logs`
- Share cards: `Pictures\Kippix`

(`ExtensionsData` is in `%APPDATA%\Playnite`, or in the Playnite folder for the portable version.)

---

## Problems

### Kippix won't start / Playnite crashes in fullscreen.
Start Playnite in desktop mode, open Add-ons › Kippix Helper › **Doctor** and turn on **Safe mode** (widgets, live
backgrounds, the overlay and animations off), then try fullscreen again and switch things back on one at a time. Kippix
offers safe mode by itself after two failed starts.

### It feels slow.
Try, in this order: a still background instead of a trailer or slideshow (Home settings › Background), fewer widgets,
trailer preview off, Accessibility › reduce motion, and Handheld mode's battery saver on small devices. Kippix Doctor also
points out common causes.

### Things don't line up on my screen.
Kippix is laid out for 16:9 and adjusts to other shapes (16:10, 4:3, ultrawide). If something still sits wrong, hold A on it
to move it, or reset that page. Please [report it](../../../issues/new/choose) with a screenshot and your screen size.

### My settings disappeared.
Desktop mode › Add-ons › Kippix Helper › Layouts › **Undo last reset**, if a reset did it. Kippix also keeps
`settings.before-reset.json` in its data folder.

### Something else is wrong.
Run **Settings › Help › Kippix Doctor**. If that doesn't sort it, save a bug report (Settings › Help › *Save a log*) and
[open an issue](../../../issues/new/choose) with what you did, what happened, and the report attached.
