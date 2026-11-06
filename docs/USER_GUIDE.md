# Lunar user guide

Lunar is a small "island" at the top of your screen, with a pair of eyes. Click it to open quick controls:
- your sound, Wi-Fi and Bluetooth;
- what is playing;
- a file converter;
- optionally, your Claude usage limits.

It works on your PC only. It has no account, no telemetry and no automatic updates.

Version 1.0.x · Windows 11 x64.

---

## 1. Install, start and remove

**Install.** Run `Lunar.msi`. It installs for your Windows account only and does not ask for administrator rights. The program goes to `%LocalAppData%\Programs\Lunar`, and a **Lunar** shortcut is added to the Start menu.

> The installer is not signed yet. Windows SmartScreen may show "Windows protected your PC". Choose **More info**, then **Run anyway**, if you got the file from a source you trust.

**Start.** Open **Lunar** from the Start menu. The island appears at the top centre of your main display, and a Lunar icon appears in the notification area (system tray). Only one Lunar runs at a time.

**Start with Windows.** This is off by default. Turn it on in **Settings** or from the tray menu (**Start with Windows**). Windows' own **Task Manager → Startup apps** can switch it off as well; Lunar shows whatever Windows has.

**Update.** Run the newer `Lunar.msi`. It replaces the old version. Your settings and your Start with Windows choice are kept.

**Remove.** Go to **Windows Settings → Apps → Installed apps → Lunar → Uninstall**. Lunar asks whether to also delete your settings and logs. Choose **No** to keep them for a later reinstall. Lunar also removes itself from Start with Windows.

## 2. The island

| What you do | What happens |
|---|---|
| Click the eyes | The island opens. Click again to close it |
| **Esc** | Closes the open island |
| Tray icon → **Open** | Opens the island with the keyboard ready |
| **← / →**, or the arrows and dots at the sides and bottom | Moves between the pages: **System**, **Media**, **File converter**, **Usage** |
| Drag a page sideways with the mouse | Moves to the next or previous page (dragging a slider never changes the page) |
| Tray icon → **Exit** | Closes Lunar completely |

While closed, the eyes blink and look around. You can turn that off (see Settings).

## 3. System page

- **Clock:** the date and time in your Windows region format.
- **Volume:** drag the slider or press mute. The slider follows changes made elsewhere, such as your keyboard's volume keys. If no sound device is available, the page says so.
- **Wi-Fi and Bluetooth:** show whether they are on, off or unavailable. Where Windows allows it, a switch turns them on or off. Where Windows or the driver does not allow it, Lunar says why and offers a button that opens the right page in Windows Settings. Lunar never shows Wi-Fi network names.

## 4. Media page

The Media page shows what a media app is playing: the title, the artist, the app and the artwork when the app shares them. It works with apps that report to Windows' media controls, such as Spotify, Media Player and most browsers.

- **Play/pause, previous and next** appear only when that app supports them.
- **Seek:** drag the position bar and release. The bar is disabled for live streams and for apps that do not allow seeking, with a short reason.
- **Several apps:** when more than one app is playing, you can switch between them.
- **Nothing playing:** the page says so. If Windows cannot be read, it shows the reason and a **Retry** button.

Lunar does not keep a history of what you play.

## 5. File converter

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

## 6. Usage page (optional, off by default)

This page shows your **Claude** 5-hour and weekly limits: the percentage used and when each resets. It needs **Claude Code** signed in on this PC.

- It is **off** until you turn on **Show Claude usage**. Before that, Lunar makes no network request.
- When it is on, Lunar reads Claude Code's sign-in on this PC, read-only, and asks Anthropic for your limits over HTTPS. It does this about every 5 minutes while the island is open and about every 15 minutes while it is closed. **Refresh** reads the limits again.
- When a limit that was in use resets, a small notice appears at the bottom right of the screen for 10 seconds.
- If Claude Code is not signed in, or the sign-in has expired, the page says so and makes no request.
- This uses an Anthropic address that is not officially documented, so it may stop working at any time. See the privacy notice for details.

Lunar is not made by, or affiliated with, Anthropic.

## 7. Settings

Open it from the tray icon → **Settings**. Changes apply at once, except where noted.

| Section | Choices |
|---|---|
| Appearance | Theme: Match Windows, Dark or Light. **Reduce motion**: no slides, fades or confetti; Windows' own animation setting is always respected. **Animated eyes**: on or off |
| Display | Which display shows Lunar, and the distance from the top of the screen |
| Start with Windows | On or off (the same switch as in the tray menu) |
| Conversions | The default folder for converted files |
| Language | Match Windows, English or Bahasa Indonesia (applies the next time Lunar starts) |
| Privacy | **Detailed diagnostics**: more detail in the local log files (applies the next time Lunar starts) |
| Reset all settings | Every setting goes back to its default. This also turns the Usage page off |

Settings are saved only on this PC, in `%LocalAppData%\Lunar\settings.json`. If that file is damaged, Lunar uses the last good copy or the defaults and tells you so.

## 8. Privacy in short

- No telemetry, no automatic updates, and none of your files are uploaded. Conversions run on your PC.
- The only network use is the Usage page, and only after you turn it on.
- Local logs (`%LocalAppData%\Lunar\Logs`, at most about 7 MB) record what happened, never what you were doing. They never contain file names, file contents, passwords, Wi-Fi names or what you play.
- The full notice is `PRIVACY.md` in the program folder (`%LocalAppData%\Programs\Lunar`), next to this guide.

## 9. Known limitations of this version

- **Windows 11 x64 only.** Windows 10 is not supported.
- **Word (DOCX) files cannot be converted to PDF or pictures, and PDF cannot be converted to editable Word.** Word files can only be made from pictures.
- **The installer is not signed.** Expect a SmartScreen warning.
- **Wi-Fi and Bluetooth switches** depend on Windows and your device's driver. Where they are not allowed, Lunar sends you to Windows Settings.
- **Seeking** depends on the media app.
- **Not yet checked on real hardware:**
  - a second monitor with a different scaling;
  - Narrator (the screen reader) reading every control;
  - how the island behaves over fullscreen games and videos;
  - Remote Desktop.
- **Languages:** English and Bahasa Indonesia.

## 10. If something goes wrong

| Problem | Try |
|---|---|
| The island is not visible | Use the tray icon → **Open**. Check **Settings → Display**. Exit and start Lunar again |
| The tray icon is hidden | Click the **^** arrow in the notification area. You can drag Lunar's icon out of it |
| A switch says Windows refused the change | Use the **Open Settings** button it offers, or change it in Windows Settings |
| A conversion was refused | Read the reason shown next to the file: limits, file type, damaged file or a password needed |
| Lunar does not start with Windows | Turn it on in Settings, and check **Task Manager → Startup apps** |
| Something else | Turn on **Detailed diagnostics**, restart Lunar, reproduce the problem, and look in `%LocalAppData%\Lunar\Logs` |

Lunar is licensed under the Apache License, Version 2.0. The program folder (`%LocalAppData%\Programs\Lunar`) holds:
- this guide (`USER_GUIDE.md`);
- the privacy notice (`PRIVACY.md`);
- Lunar's licence and notice (`LICENSE.txt`, `NOTICE.txt`);
- the licences of the third-party components Lunar includes (`THIRD_PARTY_NOTICES.txt`).
