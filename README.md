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
- Element and skill-type icons are bundled as WPF vector resources.
- App text uses bundled Roboto Variable from Google Fonts under the SIL Open Font License.

The app does not refresh skill data at runtime. Data updates are prepared before release.

## Development

Run from source:

```powershell
dotnet run --project .\Peek.csproj
```

Validate the bundled skill database:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -ValidateOnly
```

Build a local Windows x64 release:

```powershell
$out = ".\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish-current"
dotnet publish .\Peek.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
Compress-Archive -LiteralPath "$out\Peek.exe" -DestinationPath .\releases\Peek-win-x64.zip -CompressionLevel Optimal -Force
```

`releases/Peek-win-x64.zip` is the tracked Windows x64 release artifact. Refresh it after user-facing release changes.

## Updating Skill Data

Fetch the latest wikiroco skills/icons and preserve translations when unchanged:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1
```

Translate only new or changed skills:

```powershell
$env:GEMINI_API_KEY = "AIza..."
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -TranslateChanged
```

The updater:

- Fetches wikiroco skill data.
- Downloads missing or changed skill icons.
- Normalizes skill icons to `128x128`.
- Updates element/type vector icons from rocomwiki assets.
- Stores `source_hash` and `source_icon_url` for change detection.
- Preserves English/Vietnamese translations when source content is unchanged.
- Calls Gemini only for changed records when `-TranslateChanged` is used.
- Writes backups under ignored `data/skill-backups`.
- Validates skill count, duplicate IDs/names, icon presence, icon size, and EN/VI localization coverage.

After updating data, run validation and commit changed source assets such as `Resources/Data/skills.json`, `Resources/Skills`, and icon resources. Refresh `releases/Peek-win-x64.zip` when the update should ship in the bundled app.

## Anti-Cheat Notes

Peek is an external desktop overlay. It does not inject into the game, hook graphics APIs, read or write process memory, install global input hooks, or automate input.

It uses normal Windows screen capture for the selected area, so compatibility can still vary by game and anti-cheat configuration. Borderless windowed mode is usually the safest display mode.
