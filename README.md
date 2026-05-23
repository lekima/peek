# Peek

Minimal Windows overlay companion for Roco Kingdom: World.

## Release

Build `releases/Peek-win-x64.zip`, unzip it, then run `Peek.exe`.

The release zip is self-contained for Windows x64, so it should not require a separate .NET install. Windows SmartScreen may warn because the app is not code-signed.

## How it works

- Use the five-button strip to move, translate, check skills, clear, and resize.
- Drag the resize button downward to reveal the capture frame.
- The app captures only the framed area underneath, sends the image to the Gemini API, then streams translated text inside the frame.
- Click the skill button (`S`) to identify visible Chinese skill names and show matching local skill details with icons.
- Right-click the move button to open Settings or quit.

## Settings

Right-click the move button, then choose `Settings`.

- Target language: configurable, default English
- Model: configurable Gemini model id, default `gemini-3.1-flash-lite`
- Search: search buttons open Bilibili Chinese search and always prepend the Roco Kingdom search prefix.
- API key storage: Gemini API keys are encrypted for the current Windows user with DPAPI in `data/settings.json`
- Log: `data/peek.log.jsonl`
- Review data: source captures are saved in `data/captures`, and read translations/search queries are logged as `text_result` events.
- Skill data: bundled from `wikiroco.com` under `Resources/Data/skills.json`; skill icons are bundled under `Resources/Skills`, and skill-card element/type icons are bundled from `rocomwiki.app`.
- Font: Roboto Variable is bundled from Google Fonts under the SIL Open Font License in `Resources/Fonts`; app text, settings, translations, and skill cards use Roboto Variable with explicit weights.
- Local data cleanup: delete the `data` folder next to `Peek.exe`

## Run

```powershell
dotnet run --project .\Peek.csproj
```

## Release build

```powershell
dotnet publish .\Peek.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
Compress-Archive -LiteralPath .\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Peek.exe -DestinationPath .\releases\Peek-win-x64.zip -CompressionLevel Optimal -Force
```

The zip is a local/generated release artifact and is ignored by git. Publish it through the release channel rather than committing it with source changes.

## Updating Skill Data

Skill data is a generated offline asset. Update it before building a new app release; do not require users to refresh data at runtime.

Validate the current bundled database:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -ValidateOnly
```

Fetch the latest wikiroco skills/icons and preserve existing translations when source text is unchanged:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1
```

If the script reports new or changed skills, translate only those changed records:

```powershell
$env:GEMINI_API_KEY = "AIza..."
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\update-skills.ps1 -TranslateChanged
```

The updater:

- Fetches `https://wikiroco.com/api/skills` and rocomwiki icon assets
- Regenerates `Resources/Data/skills.json`
- Downloads missing or changed skill icons
- Normalizes all skill icons to `128x128`
- Updates skill-card element/type vector icons
- Stores `source_hash` and `source_icon_url` for future change detection
- Preserves EN/VI translations when `name_zh`, `description_zh`, stats, element, and category are unchanged
- Calls Gemini only for new/changed skills when `-TranslateChanged` is used
- Validates skill count, duplicate IDs/names, icon presence, icon size, and EN/VI localization coverage

After updating data, run the release build command above and commit the changed generated assets plus `releases/Peek-win-x64.zip`.

For strict anti-cheat games, use borderless windowed mode when possible. The app is an external desktop overlay: it does not inject into the game, hook graphics APIs, read or write process memory, install global input hooks, or automate input. It still uses normal Windows screen capture for the selected area, so no app can guarantee compatibility with every anti-cheat.
