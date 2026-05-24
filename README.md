# Peek

Windows overlay companion for `Roco Kingdom: World`.

Peek captures a user-selected screen region, translates visible Chinese game text with Gemini, and can identify visible Roco skill names against a bundled offline skill database.

## User Flow

- Drag the move button to position the overlay.
- Drag the resize button to reveal or resize the capture frame.
- Click the play button to translate the framed area.
- Click the search button to check visible skills and show local skill cards.
- Click a translated skill name to open Bilibili search for the original Chinese skill name.
- Right-click the move button for `Settings` or `Quit`.

Search URLs are deterministic and use the Roco prefix:

```text
洛克王国 {Chinese skill/search term}
```

## Settings

Settings are available from the move-button right-click menu.

- `Gemini API key`: stored encrypted for the current Windows user with DPAPI.
- `Target language`: fixed dropdown with `English` and `Vietnamese`.
- `Keep troubleshooting data`: off by default; when enabled, raw captures and model text are retained locally for troubleshooting.
- `Clear data`: removes local logs and saved captures.

Runtime data is stored under the current Windows user's local app data folder, normally `%LOCALAPPDATA%\Peek`:

- `settings.json`: encrypted settings.
- `peek.log.jsonl`: app log. Raw translated text and search terms are redacted unless diagnostics are enabled.
- `captures/`: saved screenshots, only when diagnostics are enabled.

Use `Clear data` in settings, or delete the local app data folder, to reset logs and saved captures.

## Bundled Data

- Skill data comes from `https://wikiroco.com/api/skills`.
- Skill records are bundled in `Resources/Data/skills.json`.
- Skill icons are bundled in `Resources/Skills` and normalized to `128x128`.
- Elf data comes from `https://wikiroco.com/api/pokemon` plus per-elf detail and evolution-chain endpoints.
- Elf records are bundled in `Resources/Data/elves.json`; each elf skill relation includes the matching bundled `skill_id`, and evolution chains are normalized at the top level.
- Bundled skill data uses schema v2 with canonical normalized source hashes, icon content hashes, and translations bound to each skill source hash.
- Element and skill-type icons are bundled as WPF vector resources.
- Overlay buttons and overlay-frame text use bundled Roboto Condensed Variable from Google Fonts under the SIL Open Font License. Settings use the default Windows UI font.

The app does not refresh bundled Wikiroco data at runtime. Data updates are prepared before release.

## Development

Run from source:

```powershell
dotnet run --project .\Peek.csproj
```

Validate the bundled skill database:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -ValidateOnly
```

Validate the bundled elf database and skill links:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-elves.ps1 -ValidateOnly
```

Validate the local bundle and quickly check whether bundled skill data differs from upstream wikiroco data:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -CheckFreshness
```

Run the slower release audit that also checks same-URL upstream icon content changes:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -CheckFreshness -DeepIcons
```

Check whether bundled elf data differs from upstream Wikiroco:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-elves.ps1 -CheckFreshness
```

Build a local Windows x64 release:

```powershell
$out = ".\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish-current"
dotnet publish .\Peek.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
Compress-Archive -LiteralPath "$out\Peek.exe" -DestinationPath .\releases\Peek-win-x64.zip -CompressionLevel Optimal -Force
```

`releases/Peek-win-x64.zip` is the tracked Windows x64 release artifact. Refresh it after user-facing release changes.

## Updating Skill Data

Fetch the latest wikiroco skills/icons and preserve translations when unchanged:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1
```

Repair same-URL upstream icon content changes reported by the deep audit:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -DeepIcons
```

Translate only new or changed skills:

```powershell
$env:GEMINI_API_KEY = "AIza..."
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -TranslateChanged
```

Build or refresh the model-maintained translation memory from the current corpus:

```powershell
$env:GEMINI_API_KEY = "AIza..."
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -BuildTranslationMemory
```

Run a consistency editor pass over existing translations:

```powershell
$env:GEMINI_API_KEY = "AIza..."
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -HarmonizeTranslations
```

The updater:

- Fetches wikiroco skill data.
- Checks upstream freshness without writing files when `-CheckFreshness` is used. By default this is a quick skill data and icon URL check.
- Checks same-URL upstream icon content changes when `-DeepIcons` is added, using parallel remote icon downloads controlled by `-IconCheckThrottle`.
- Downloads missing or changed skill icons.
- Normalizes skill icons to `128x128`.
- Updates element/type vector icons from rocomwiki assets.
- Stores schema v2 source metadata, per-skill `source_hash`, icon source URL, and icon content hash for change detection.
- Preserves English/Vietnamese translations only when their `translated_from_hash` matches unchanged source content.
- Builds `Resources/Data/translation-memory.json` from the current corpus when `-BuildTranslationMemory` is used, or automatically before changed-skill translation when the memory file is missing.
- Calls Gemini for changed records when `-TranslateChanged` is used, using the translation memory as corpus style context.
- Runs a Gemini consistency editor pass after `-TranslateChanged` unless `-SkipHarmonization` is passed.
- Re-edits existing translations for corpus consistency when `-HarmonizeTranslations` is used.
- Writes backups under ignored `data/skill-backups`.
- Validates schema/source metadata, dataset hash, generated IDs, duplicate IDs/names, canonical source hashes, icon paths/content/size, and EN/VI localization coverage.

After updating data, run validation and commit changed source assets such as `Resources/Data/skills.json`, `Resources/Skills`, and icon resources. Refresh `releases/Peek-win-x64.zip` when the update should ship in the bundled app.

## Updating Elf Data

Fetch the latest Wikiroco elf details, evolution chains, and skill links:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-elves.ps1
```

The updater:

- Pages through the Wikiroco `pokemon` list endpoint.
- Fetches each elf detail record and evolution chain by Chinese name.
- Preserves Wikiroco fields such as attributes, egg groups, obtain method, stats, trait, restraints, defensive type chart, images, and evolution chains.
- Normalizes evolution chains at the top level and links each elf by `evolution_chain_id`.
- Stores relation-only elf skills with `skill_id`, source/acquisition labels, and sort order; display fields resolve from `Resources/Data/skills.json`.
- Stores schema v1 source metadata, raw-source-backed per-elf `source_hash`, the exact skill bundle hash, and a dataset hash for change detection.
- Writes backups under ignored `data/elf-backups`.
- Validates schema/source metadata, generated IDs, duplicate IDs/names, source hashes, dataset hash, and skill-link coverage.

After updating elf data, run validation and commit `Resources/Data/elves.json` with any updater changes. Refresh `releases/Peek-win-x64.zip` when the update should ship in the bundled app.

## Anti-Cheat Notes

Peek is an external desktop overlay. It does not inject into the game, hook graphics APIs, read or write process memory, install global input hooks, or automate input.

It uses normal Windows screen capture for the selected area, so compatibility can still vary by game and anti-cheat configuration. Borderless windowed mode is usually the safest display mode.
