# Lunar user guide

Lunar is a small "island" at the top of your screen, with a pair of eyes. Click it to open quick controls:
- your sound, screen brightness, Wi-Fi and Bluetooth;
- what is playing;
- a Pomodoro focus timer that turns on Do Not Disturb;
- a file converter;
- notes and a voice recorder;
- optionally, your Claude usage limits.

Lunar stays out of screen sharing, screenshots and recordings: you see it, the people you share your screen with do not (see 2).

It works on your PC only. It has no account and no telemetry. Once a day it asks GitHub whether a newer Lunar was released, and it never installs one unless you choose **Install now** (see 1).

Version 1.0.x · Windows 11 x64.

---

## 1. Install, start and remove

**Install.** Run `Lunar.msi`. It installs for your Windows account only and does not ask for administrator rights. The program goes to `%LocalAppData%\Programs\Lunar`, and a **Lunar** shortcut is added to the Start menu. The installer has no wizard: it shows a short progress bar and closes by itself when it is done. That is the installation finishing, not a failure.

> The installer is not signed yet. Windows SmartScreen may show "Windows protected your PC". Choose **More info**, then **Run anyway**, if you got the file from a source you trust.

**Start.** Open **Lunar** from the Start menu. The island appears at the top centre of your main display, and a Lunar icon appears in the notification area (system tray). Only one Lunar runs at a time.

**Start with Windows.** This is off by default. Turn it on in **Settings** or from the tray menu (**Start with Windows**). Windows' own **Task Manager → Startup apps** can switch it off as well; Lunar shows whatever Windows has.

**Update.** When a newer Lunar is released, a small notice appears at the bottom right: **Install now** downloads it, checks that the file is exactly the one released, closes Lunar, installs the update and starts Lunar again. **Later** asks again the next day. The notice waits while a focus session or a recording is running. The same choice is in **Settings → Updates**, where **Check for updates** can also be turned off. You can still update by hand: exit Lunar, then run the newer `Lunar.msi` (there is no need to remove the old version first). Either way your settings, notes, recordings and Start with Windows choice are kept.

> Lunar 1.0.5 and older have no update notice: install 1.0.6 by hand once.

**Remove.** Go to **Windows Settings → Apps → Installed apps → Lunar → Uninstall**. Lunar asks whether to also delete your settings, notes and logs. Choose **No** to keep them for a later reinstall. Your recordings are in your own folder (normally `Music\Lunar Recordings`) and are never deleted by uninstalling. Lunar also removes itself from Start with Windows.

## 2. The island

| What you do | What happens |
|---|---|
| Click the eyes | The island opens. Click again to close it |
| **Esc** | Closes the open island |
| Tray icon → **Open** | Opens the island with the keyboard ready |
| **← / →**, or the arrows and dots at the sides and bottom | Moves between the pages: **System**, **Media**, **Pomodoro**, **File converter**, **Notes**, **Usage** |
| Drag a page sideways with the mouse | Moves to the next or previous page (dragging a slider never changes the page) |
| Tray icon → **Exit** | Closes Lunar completely |

While closed, the eyes blink and look around. You can turn that off (see Settings).

### While recording or focusing

While the island is closed it shows what is running instead of the eyes:
- **Recording:** Pause and Stop at the left, red sound lines in the middle, the recorded time at the right. The island is a little wider. Pause and Stop work without opening the island; a click on the lines opens it.
- **Focus session:** the time left in the middle. While paused, the time is dimmed with a pause symbol. A click opens the island.

A recording is shown before a focus session when both run. Opening the island shows the eyes again. When the focus time is over, the eyes come back and smile.

### Hidden from screen sharing

Every Lunar window stays on **your** screen but is left out of screen sharing (Teams, Zoom, Meet in a browser), screenshots (Snipping Tool, Print Screen) and screen recordings. This covers the island open or closed on every page, the Settings window, the reset notice, tooltips, the tray menu and Lunar's dialogs. It is always on and cannot be turned off.

Good to know:
- You cannot take a screenshot of Lunar yourself either. To report a problem, describe it or take a photo with your phone.
- It keeps Lunar out of images that Windows' capture features make. A phone camera, or a tool built to ignore this Windows setting, can still see it.
- The tray icon belongs to the Windows taskbar, so it stays visible in a shared screen.
- **Settings → Privacy** shows whether Windows allowed it. If Windows refused, it says that Lunar may be visible.

## 3. System page

- **Clock:** the date and time in your Windows region format.
- **Volume:** drag the slider or press mute. The slider follows changes made elsewhere, such as your keyboard's volume keys. If no sound device is available, the page says so.
- **Brightness** (under volume): drag the slider to change the brightness of the display the island is on. It follows changes made elsewhere, such as your keyboard's brightness keys or Windows quick settings.
  - A laptop's own screen works through Windows.
  - An external monitor works only if it supports DDC/CI (many do, some docks and adapters do not). Its changes made with the monitor's own buttons appear within a few seconds while the page is open.
  - If the brightness cannot be changed from Lunar, the row says so; **Settings** opens Windows' display settings.
- **Wi-Fi and Bluetooth:** show whether they are on, off or unavailable. Where Windows allows it, a switch turns them on or off. Where Windows or the driver does not allow it, Lunar says why and offers a button that opens the right page in Windows Settings. Lunar never shows Wi-Fi network names.

## 4. Media page

The Media page shows what a media app is playing: the title, the artist, the app and the artwork when the app shares them. It works with apps that report to Windows' media controls, such as Spotify, Media Player and most browsers.

- **Play/pause, previous and next** appear only when that app supports them.
- **Seek:** drag the position bar and release. The bar is disabled for live streams and for apps that do not allow seeking, with a short reason.
- **Several apps:** when more than one app is playing, you can switch between them.
- **Nothing playing:** the page says so. If Windows cannot be read, it shows the reason and a **Retry** button.

Lunar does not keep a history of what you play.

## 5. Pomodoro (focus timer)

The **Pomodoro** page is the third page.

- Choose a length (15, 25, 45 or 60 minutes, or **−** and **+** for 1 to 180 minutes), then **Start**. Lunar remembers the last length.
- While the time runs, Lunar turns on Windows **Do Not Disturb** (alarms only). A line on the page says whether Windows really turned it on.
- **Pause** stops the time and turns Do Not Disturb off until you **Resume**. **Stop** ends the session.
- At the end, or when you stop, Do Not Disturb goes back to exactly how it was. If you already had it on, it stays on. If you change it yourself during a session, Lunar leaves your choice alone.
- If Lunar is closed unexpectedly during a session, it puts Do Not Disturb back the next time it starts.

What Do Not Disturb does is Windows' choice, not Lunar's:
- Windows holds back notification banners and keeps them in the notification centre.
- Alarms, and apps on your Windows priority list, can still come through.
- Apps that draw their own pop-up windows are not affected.
- If Windows does not let Lunar turn it on, the timer still runs, the page says so, and **Open focus settings** takes you to Windows' own setting.

The way Lunar turns on Do Not Disturb is not officially documented by Microsoft, so a Windows update may stop it working; the page then says so.

## 6. File converter

Your original files are **never changed**. Results are new files.

### What can be converted

| From \ To | PNG | JPG / JPEG | Word (DOCX) | PDF |
|---|---|---|---|---|
| PNG | — | ✓ (transparent areas get the background colour you choose) | ✓ picture placed in a document | ✓ one page per picture |
| JPG / JPEG | ✓ | — (same format) | ✓ picture placed in a document | ✓ one page per picture |
| PDF | ✓ one picture per page | ✓ one picture per page | ✗ not in this version | — |
| Word (DOCX) | ✗ not in this version | ✗ not in this version | — | ✗ not in this version |

Before you convert, Lunar tells you what each route does. For example:
- JPG loses some detail and fills transparency.
- A picture placed in Word or PDF is a picture, not editable text: there is no text recognition (OCR).

### How

1. **Add files:** press **Add files** or drop files onto the page (up to 100 at a time).
2. **Pick a format.** Formats that cannot be made from your files are shown but disabled.
3. **Choose options** (only the ones that apply):
   - JPEG quality (1–100);
   - background for transparent areas;
   - page size, margin and how the picture sits on the page, for PDF;
   - resolution (72, 150 or 300 pixels per inch) and which pages, from a PDF;
   - the password of a protected PDF, which is kept in memory only and never saved.
4. **Choose where to save.** By default results go next to your files; you can choose another folder.
5. **Choose what happens if a result's name is taken:** add a number (the default), replace (Lunar asks you to confirm), or skip.
6. **Convert.** Each file shows its progress. You can **Cancel** one or all of them, **Retry** a failed one, and **Show in folder** a finished one.

You can close the island while files convert; the work continues. When a conversion finishes, the eyes smile.

### Limits

These limits protect your PC from damaged or hostile files:
- up to 100 files per batch;
- up to 200 MB per file;
- up to 200 pages per PDF;
- up to 50 megapixels per picture.

A file outside the limits, of the wrong type, or damaged is refused with a reason. A cancelled or failed conversion leaves no partial file behind.

## 7. Notes and recordings

The **Notes** page keeps short plain-text notes on this PC.

- **New note** opens an empty note: just type. Lunar saves it on its own about a second after you stop typing, and again when you close it.
- **Done** or **Esc** closes the note (Esc closes only the note, not the island). A note left empty is removed when you close it.
- The list shows every note, newest first, with its first line and when it changed. It scrolls; there is no limit on pages.
- **Delete** (in an open note, the bin next to a note, or the **Delete** key) removes it at once; **Undo** brings it back for a few seconds.
- To type, click in the note: the island takes the keyboard while you type and gives it back when you click elsewhere. The arrow keys move the cursor in the note; they never change the page.

Limits: up to 100,000 characters per note and 10,000 notes. Lunar tells you when one is reached.

Notes are stored only on this PC in `%LocalAppData%\Lunar\notes.json`, with the previous version kept as `notes.json.bak`. They are not encrypted: anyone who can open your Windows account's files can read them. If the file is damaged, Lunar keeps it aside as `notes.corrupt.json`, opens the last good copy and tells you. You can back up your notes by copying `notes.json`.

### Records (voice recorder)

The **Notes | Records** switch at the top of the page opens the recorder.

- **Microphone:** **Automatic** uses the Windows default microphone and follows it. You can pick another one; Lunar remembers it. The microphone cannot be changed during a recording.
- **Record** starts. **Pause**/**Resume** and **Stop** are on the page and in the closed island. Stop saves at once.
- **Where:** recordings are saved as MP3 files named by date and time, for example `Recording 2026-11-09 14-30-05.mp3`, in `Music\Lunar Recordings` unless you choose another folder. A name already taken gets ` (2)`. **Show in folder** opens it.
- **Nothing is lost silently:** if the microphone disappears, the PC sleeps or something fails, what was recorded so far is saved and Lunar tells you. Exiting Lunar during a recording saves it first. A recording stops on its own after 12 hours, or when the folder's drive has less than 100 MB free.
- **Microphone permission:** if Windows privacy settings block microphones for desktop apps, the page says so and **Open microphone settings** takes you there. Lunar opens no microphone until you press **Record**, and Windows shows its own microphone sign while it is recording.
- **Ask first.** Tell people before you record them. In many places recording a conversation without everyone's consent is against the law.

## 8. Usage page (optional, off by default)

This page shows your **Claude** 5-hour and weekly limits: the percentage used and when each resets. It needs **Claude Code** signed in on this PC.

- It is **off** until you turn on **Show Claude usage**. Before that, Lunar makes no network request.
- When it is on, Lunar reads Claude Code's sign-in on this PC, read-only, and asks Anthropic for your limits over HTTPS. It does this about every 5 minutes while the island is open and about every 15 minutes while it is closed. **Refresh** reads the limits again.
- When a limit that was in use resets, a small notice appears at the bottom right of the screen for 10 seconds.
- If Claude Code is not signed in, or the sign-in has expired, the page says so and makes no request.
- This uses an Anthropic address that is not officially documented, so it may stop working at any time. See the privacy notice for details.

Lunar is not made by, or affiliated with, Anthropic.

## 9. Settings

Open it from the tray icon → **Settings**. Changes apply at once, except where noted.

| Section | Choices |
|---|---|
| Appearance | Theme: Match Windows, Dark or Light. **Reduce motion**: no slides, fades or confetti; Windows' own animation setting is always respected. **Animated eyes**: on or off |
| Display | Which display shows Lunar, and the distance from the top of the screen |
| Start with Windows | On or off (the same switch as in the tray menu) |
| Conversions | The default folder for converted files |
| Language | Match Windows, English or Bahasa Indonesia (applies the next time Lunar starts) |
| Privacy | **Detailed diagnostics**: more detail in the local log files (applies the next time Lunar starts). A line says whether Lunar is hidden from screen sharing (always on, no switch) |
| Updates | **Check for updates** (on by default): once a day Lunar asks GitHub for the newest release. The line below says which version you have, or offers **Install now** when a newer one is out |
| Reset all settings | Every setting goes back to its default. This also turns the Usage page off |

Settings are saved only on this PC, in `%LocalAppData%\Lunar\settings.json`. If that file is damaged, Lunar uses the last good copy or the defaults and tells you so.

## 10. Privacy in short

- No telemetry, and none of your files are uploaded. Conversions run on your PC.
- Notes and recordings stay on this PC and are never logged or sent anywhere. Lunar windows are kept out of screen sharing and screenshots.
- Network use: the daily update check (GitHub sees your IP address and that Lunar asked; you can turn it off), the update download when you choose **Install now**, and the Usage page only after you turn it on.
- Local logs (`%LocalAppData%\Lunar\Logs`, at most about 7 MB) record what happened, never what you were doing. They never contain file names, file contents, note text, passwords, Wi-Fi names or what you play.
- The full notice is `PRIVACY.md` in the program folder (`%LocalAppData%\Programs\Lunar`), next to this guide.

## 11. Known limitations of this version

- **Windows 11 x64 only.** Windows 10 is not supported.
- **Word (DOCX) files cannot be converted to PDF or pictures, and PDF cannot be converted to editable Word.** Word files can only be made from pictures.
- **The installer is not signed.** Expect a SmartScreen warning. An update is checked against the file GitHub lists for the release, which proves it arrived intact, not who published it.
- **Do Not Disturb** is turned on in a way Microsoft does not document, so a Windows update may stop it; the timer then still works and the page says so.
- **Wi-Fi and Bluetooth switches** depend on Windows and your device's driver. Where they are not allowed, Lunar sends you to Windows Settings.
- **Seeking** depends on the media app.
- **External monitor brightness** depends on the monitor (DDC/CI) and was not tested on a real external monitor.
- **Screenshots of Lunar are not possible**, by design (see 2). Whether Windows Magnifier shows Lunar was not checked.
- **Not yet checked on real hardware:**
  - a second monitor with a different scaling;
  - Narrator (the screen reader) reading every control;
  - how the island behaves over fullscreen games and videos;
  - Remote Desktop.
- **Languages:** English and Bahasa Indonesia.

## 12. If something goes wrong

| Problem | Try |
|---|---|
| The island is not visible | Use the tray icon → **Open**. Check **Settings → Display**. Exit and start Lunar again |
| The tray icon is hidden | Click the **^** arrow in the notification area. You can drag Lunar's icon out of it |
| A switch says Windows refused the change | Use the **Open Settings** button it offers, or change it in Windows Settings |
| A conversion was refused | Read the reason shown next to the file: limits, file type, damaged file or a password needed |
| Typing in a note goes to another app | Click inside the note first; the island takes the keyboard only after that click |
| The brightness row says it can't be changed | Your display does not allow it from Lunar. Use **Settings** in that row (Windows display settings) or the monitor's own buttons |
| Lunar does not start with Windows | Turn it on in Settings, and check **Task Manager → Startup apps** |
| Record is greyed out or says the microphone is blocked | Connect a microphone, or use **Open microphone settings** and allow desktop apps to use it |
| Do Not Disturb did not turn on | The Pomodoro page says why. Use **Open focus settings**, or turn it on yourself; the timer works either way |
| An update says the file did not match | Nothing was installed. Try again later, or download `Lunar.msi` from the releases page and run it |
| Something else | Turn on **Detailed diagnostics**, restart Lunar, reproduce the problem, and look in `%LocalAppData%\Lunar\Logs` |

Lunar is licensed under the Apache License, Version 2.0. The program folder (`%LocalAppData%\Programs\Lunar`) holds:
- this guide (`USER_GUIDE.md`);
- the privacy notice (`PRIVACY.md`);
- Lunar's licence and notice (`LICENSE.txt`, `NOTICE.txt`);
- the licences of the third-party components Lunar includes (`THIRD_PARTY_NOTICES.txt`).
