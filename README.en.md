<img src="docs/images/icon.png" width="72" align="right" />

# GameTrans

[简体中文](README.md) | **English**

A real-time chat translator for PC games. It captures a chosen screen region with a hotkey and sends it to the DeepSeek vision model for OCR and translation, then draws the result in place as a configurable overlay. A separate input-translation channel translates text you type and copies the result to the clipboard.

- Standalone 64-bit process, single-file self-contained build, **no administrator rights required**
- No dependency on the game client, account system or accelerator software
- Click-through overlay that never steals focus and is excluded from screen capture (so it never translates itself)
- UI switchable between **简体中文 / English**; translation direction and target API are dropdowns

Current version: **1.3.0** · Project: [github.com/ab-Royo/GameTrans](https://github.com/ab-Royo/GameTrans) · QQ group: **419733401** · Bilibili: [ab_Roy's channel](https://m.bilibili.com/space/51952428)

## Features

| Capability | Details |
|---|---|
| Region capture | Full-screen frozen screenshot with drag selection; multi-monitor and mixed-DPI aware |
| Screen translation | Default `F8`: capture → vision model OCR + translation → overlay; press again to hide |
| Input translation | Default `F9`: input box anchored to the capture region, translated in the configured direction and copied |
| UI language | One-click 简体中文 / English switch in Settings; every UI string updates immediately |
| Translation direction | Source: auto / Chinese / English / Korean / Japanese; target: Chinese / English / Korean / Japanese (screen and F9 directions are independent) |
| Target API | DeepSeek China / DeepSeek International, default follows the UI language, with links to the open platform, API docs and website |
| Hotkey binding | Keyboard keys plus mouse middle button, side button 1 and side button 2, optionally with `Ctrl` / `Alt` / `Shift` |
| Overlay style | Background color/opacity, text color, font family/size, padding, corner radius, border and alignment are configurable |
| Overflow strategy | Extend downward by default (same font size), or shrink to fit the region |
| Glossary | Hand-maintained term → meaning table injected into the prompt to constrain game-specific wording |
| Startup page | Summarizes model API, capture region, hotkeys and glossary, with shortcuts to each setting |
| Headless self-test | `--selftest` verifies config, capture, off-screen rendering, request body, prompts and language/site normalization |

## Screenshots

**Startup page**

<img src="docs/images/en/startup.png" width="620">

**Settings (English)**

<img src="docs/images/en/settings.png" width="520">

**Settings (简体中文)**

<img src="docs/images/settings.png" width="520">

**Text overlay** (formatted for a 370 × 130 capture region)

<img src="docs/images/overlay.png" width="247">

**Input translation (F9)**

<img src="docs/images/en/input.png" width="534">

**Glossary / slang table**

<img src="docs/images/en/glossary.png" width="712">

## Requirements

- Windows 10 2004 or later (the overlay relies on `WDA_EXCLUDEFROMCAPTURE`)
- x64 architecture
- A valid DeepSeek API key (use a model with image input; `deepseek-flash` by default)

Building requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0); running does not require the runtime (published as a self-contained single file).

Use **borderless windowed** or windowed mode for the game. Exclusive fullscreen usually produces black screenshots and may hide the overlay.

## Quick start

1. Download `GameTrans.exe` from [Releases](https://github.com/ab-Royo/GameTrans/releases).
2. Launch it and click **Settings** on the startup page:
   - pick the UI language (简体中文 / English) in the **Interface** card;
   - check the **Target API** in the **Model API** card (default follows the UI language), paste your DeepSeek API key and click **Test connection**;
   - adjust the source / target languages as needed (dropdowns; the source language can be **Auto**).
3. Click **Select region** on the startup page, drag over the in-game chat or subtitle area and press Enter.
4. Back in game:
   - press `F8` to translate the current screen, press again to hide;
   - press `F9`, type your text and press Enter — the translation is copied to the clipboard for pasting.

After the first setup the app stays in the system tray. The tray menu can translate, re-select the capture region, open the glossary, open Settings or exit.

## Usage

### Screen translation (default F8)

```
F8 ─┬─ overlay visible ─────────────────► hide and cancel in-flight request
    └─ hidden ─┬─ no capture region ─────► open the region picker, then continue
               └─ region set ───────────► capture → overlay shows "Recognizing…"
                                          → vision model OCR + translation
                                          → overlay shows translation and latency
```

- The overlay is anchored to the capture region's top-left corner and matches its width; it extends downward when the translation is taller.
- Request failures are shown in the overlay instead of failing silently.
- The direction comes from **Source language / Target language** in Settings; default `Auto → Chinese`.

### Input translation (default F9)

```
F9 ─► translucent input box anchored to the capture region
    ─► type text → Enter
    ─► model translates in the configured direction → clipboard → result shown in the box
    ─► auto-closes after 3 s (any key cancels); Enter closes immediately
```

The direction comes from the **Input translation** source / target languages; default `Chinese → English`, independent of the screen direction.

If the clipboard is temporarily occupied by another app (some accelerators and recording/overlay tools poll it), the status bar says "Clipboard busy; not copied" and Enter still closes the box immediately.

### UI language

Entry: Settings → **Interface** → **Language**.

- Switch between **简体中文 / English**. The change applies immediately; no restart needed.
- The Settings window rebuilds in place with the new language (other unsaved edits are kept); the startup page, tray menu, input box and glossary pick up the new language the next time they open.
- The language also drives the **Target API** default: Chinese → DeepSeek China, other languages → DeepSeek International.
- Stored as `ui.language` in the settings file.

### Target API (DeepSeek China / International)

Entry: Settings → **Model API** → **Target API**.

| Option | Default base URL | Open platform | API docs |
|---|---|---|---|
| DeepSeek China | `https://api.deepseek.com` | [platform.deepseek.com](https://platform.deepseek.com/) | [api-docs.deepseek.com/zh-cn](https://api-docs.deepseek.com/zh-cn/) |
| DeepSeek International | `https://api.deepseek.com` | [platform.deepseek.com](https://platform.deepseek.com/) | [api-docs.deepseek.com](https://api-docs.deepseek.com/) |

- DeepSeek officially serves both Chinese and overseas accounts from the **same endpoint**, `https://api.deepseek.com`. The two options differ in the open-platform entry, documentation language and account region (the international platform supports email sign-up and USD top-up); Settings shows the matching **Open platform / API docs / Website** links.
- Switching the option resets the base URL to that preset; a custom gateway can still be entered manually in Base URL and is not overwritten.
- Chinese UI defaults to China; other languages default to International. If you change the target API manually, switching the UI language will not override your choice.

### Hotkey binding

Entry: Settings → **Hotkeys & capture region**.

- Click **Translate hotkey** or **Input hotkey**, then press the desired combination; press `Esc` to cancel recording.
- Keyboard keys (letters, digits, F-keys, arrows, `Home` / `End`, etc.) and three mouse buttons are supported: **middle button**, **side button 1** and **side button 2**.
- `Ctrl` / `Alt` / `Shift` / `Win` modifiers can be combined, e.g. `Ctrl+Mouse Side 1`.
- Keyboard hotkeys go through `RegisterHotKey`, so the system routes them to this app. Mouse buttons can only be polled and cannot be intercepted, so **if the game binds the same mouse button, both respond**.

### Glossary / slang table

Constrains the translation of in-game abbreviations, slang, item and location names.

- Entry: startup page **Glossary**, tray menu **Glossary / Slang…**, or Settings → **Glossary**.
- Left column: the term; right column: what it means in this game.
- Batch import: one `term=meaning` per line; Tab or `|` also work.
- Up to 200 entries stored, the first 60 are sent in the prompt.

## Configuration

Config directory: `%APPDATA%\GameTrans\`

| File | Description |
|---|---|
| `settings.json` | All settings |
| `GameTrans.log` | Runtime log (rotated above 2 MB) |

Put a `portable.txt` next to the executable, or set the `GAMETRANS_DATA` environment variable, to use a portable/custom config directory.

<details>
<summary>Settings fields</summary>

| Group | Field | Description |
|---|---|---|
| `ui` | `language` | UI language: `zh` / `en`, default `zh` |
| `api` | `site` | Target API: `cn` China / `intl` International; empty or invalid follows the UI language |
| | `baseUrl` | Endpoint, default `https://api.deepseek.com` (custom gateways allowed) |
| | `model` | Model name, default `deepseek-flash` |
| | `apiKeyProtected` | DPAPI-encrypted API key (written by the app, never store plain text here) |
| | `detail` | `high` / `original` keep image detail; `low` downscales to 512×512 |
| | `maxTokens` / `temperature` / `timeoutSeconds` | Defaults 2048 / 0.2 / 90 |
| `prompt` | `gameName` | Target game, injected into the prompt to constrain terms and tone |
| | `sourceLanguage` / `targetLanguage` | Screen translation direction, language codes `auto` / `zh` / `en` / `ko` / `ja`, default `auto` → `zh` |
| | `inputSourceLanguage` / `inputTargetLanguage` | F9 direction, same codes (source may be `auto`), default `zh` → `en` |
| | `outputMode` | `translated` only translation / `bilingual` original + translation / `ocr` OCR only |
| | `glossary` | Glossary, an array of `{ term, meaning }` |
| | `extraInstruction` | Extra instructions; `systemPrompt` empty means the built-in template |
| `overlay` | `bgColor` / `bgOpacity` | Background color and opacity |
| | `textColor` / `fontFamily` / `fontSize` | Text style |
| | `padding` / `cornerRadius` / `borderColor` / `borderThickness` | Padding, corner radius, border |
| | `textAlign` / `verticalAlign` | Alignment |
| | `showStatus` / `textShadow` | Latency status, text shadow |
| | `autoScaleFont` / `minFontSize` | Font auto-scaling and its lower bound |
| | `overflowMode` | `expand` downward / `shrink` to fit |
| | `clickThrough` | Click-through |
| `hotkeys` | `translateVk` / `translateMods` | Screen translation hotkey, default `F8` (`0x77`); mouse middle / side1 / side2 are `0x04` / `0x05` / `0x06` |
| | `inputVk` / `inputMods` | Input translation hotkey, default `F9` (`0x78`), same key codes |
| `region` | `left` / `top` / `right` / `bottom` | Capture region in virtual-desktop physical pixels |
| `behavior` | `showStartupWindow` / `inputOpacity` | Startup page toggle, input box opacity |
| | `showLatency` / `copyInputResult` | Latency display, auto-copy |

> When upgrading from 1.2.x or earlier, legacy language names (such as `中文（简体）`, `English`, `自动识别`) are normalized to language codes automatically; no manual edits needed.

</details>

## How it works

```
┌──────────────┐   RegisterHotKey    ┌──────────────┐
│ Win32 hotkeys│ ──────────────────► │  TrayApp     │
└──────────────┘                     └──────┬───────┘
                                            │
                    ┌───────────────────────┼────────────────────────┐
                    ▼                       ▼                        ▼
            ┌──────────────┐        ┌──────────────┐        ┌────────────────┐
            │ ScreenCapture│        │ DeepSeekClient│       │ OverlayWindow  │
            │ GDI BitBlt   │ ─────► │ chat/completions │ ───► │ layered window │
            └──────────────┘  PNG   └──────────────┘  text  └────────────────┘
```

See [docs/DESIGN.md](docs/DESIGN.md) for capture coordinates, overlay styling, localization and target-API design, and known limitations.

## Build & release

```powershell
# Build (solution; can be opened directly in Visual Studio 2022 / Rider)
dotnet build GameTrans.sln -c Release

# Publish single file (win-x64 / self-contained) and run the self-test
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

The output lands in `dist/`; `GameTrans.exe` there is a self-contained single file ready to distribute.

Self-test:

```powershell
dist\GameTrans.exe --selftest .\out
```

## Command-line options

| Option | Description |
|---|---|
| `--selftest <dir> [--headless]` | Headless self-test, writes `report.txt` and intermediate files; `--headless` skips screen-related checks (CI) |
| `--set-key <API_KEY>` | Encrypt and save the API key |
| `--api-test <API_KEY>` | Text probe to verify connectivity |
| `--api-text <API_KEY> <text>` | Translate text in the F9 direction |
| `--api-vision <API_KEY> <image>` | Run vision translation on an image |
| `--lang <zh\|en>` | UI language for off-screen demos/self-test (used to generate bilingual screenshots) |
| `--demo-startup <png>` | Off-screen render of the startup page |
| `--demo-glossary <png>` | Off-screen render of the glossary |
| `--demo-settings <png>` | Off-screen render of the full settings page |
| `--demo-overlay <x> <y> <w> <h>` | Show the overlay at the given position |
| `--demo-compose <png> <w> <h>` | Lay out and off-screen render the overlay for a region size |
| `--demo-input <png>` | Off-screen render of the input box |
| `--demo-selector` | Briefly open the region selector for verification |

## FAQ

**Hotkeys do nothing**
Another app already owns the shortcut. Settings reports the registration result on save; use a combination with modifiers (e.g. `Ctrl+Alt+F8`). Mouse middle / side buttons cannot be registered with `RegisterHotKey` and are polled instead: if the game binds the same button, both respond, so add a modifier to the mouse binding.

**F9 result box does not close on Enter / copy fails**
The clipboard is occupied by another program (accelerators and recording/overlay tools poll it regularly). Since 1.2.0 the copy waits at most 250 ms, reports the failure in the status bar and closes the box immediately instead of freezing the UI; press Enter again later to retry the copy.

**Nothing changed after switching to English?**
The language applies immediately and the Settings window rebuilds in place. Other open windows (startup page, input box) apply it the next time they open; if the startup page is open, it is rebuilt after saving. Make sure you clicked **Save**.

**Why do China and International use the same endpoint?**
DeepSeek officially serves Chinese and overseas accounts from `https://api.deepseek.com`. The two options select the open-platform entry, documentation language and account region; they do not change the API protocol. Use Base URL to point at a custom gateway if needed.

**Black screenshots / no overlay**
The game is in exclusive fullscreen or the frame is DRM-protected. Switch to borderless windowed mode.

**Translation text is too small**
Set **Overflow** to **Expand downward**, or increase the font size and disable **Auto-scale font to region** in Settings.

**API errors**
`401` invalid key; `402` insufficient balance; `429` rate limited (the client retries once); raise `timeoutSeconds` or shrink the capture region on timeouts.

**Multi-monitor and scaling**
Coordinates are virtual-desktop physical pixels; cross-monitor selections work and each monitor is converted with its own DPI.

## Privacy & security

- The API key is encrypted with Windows DPAPI and can only be decrypted by the current user.
- Screenshots are encoded to PNG in memory and sent only to the configured model endpoint; they are never written to disk.
- No telemetry, analytics or update checks.

## Community

- Project and issue tracker: [GitHub](https://github.com/ab-Royo/GameTrans) ([Issues](https://github.com/ab-Royo/GameTrans/issues))
- QQ group: **419733401** (click the number in the startup page, Settings or the tray menu to copy it)
- Bilibili: [ab_Roy's channel](https://m.bilibili.com/space/51952428)

## Disclaimer

- This project is not affiliated with or endorsed by any game publisher, accelerator product or model provider.
- The interaction model follows common practice among similar tools; the implementation and code are independently written.
- Using this tool may violate the terms of service of some games; assess account risk yourself.
- Model output may contain errors; API usage costs are borne by the user.

## License

[MIT](LICENSE)
