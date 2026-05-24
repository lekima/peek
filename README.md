# Peek

Windows overlay translator for `Roco Kingdom: World`.

Peek captures a user-selected screen region, translates visible Chinese game text with Gemini, and offers Bilibili guide searches for the translated context.

## User Flow

- Drag the move button to position the overlay.
- Drag the resize button to reveal or resize the capture frame.
- Click the play button to translate the framed area.
- Click a numbered search button to open a Bilibili search generated from the translation context.
- Right-click the move button for `Settings` or `Quit`.

Search URLs are deterministic and use the Roco prefix:

```text
洛克王国 {Chinese search term}
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

## Development

Run from source:

```powershell
dotnet run --project .\Peek.csproj
```

Build a local Windows x64 release:

```powershell
$out = ".\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish-current"
dotnet publish .\Peek.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
New-Item -ItemType Directory -Force -Path .\releases | Out-Null
Compress-Archive -LiteralPath "$out\Peek.exe" -DestinationPath .\releases\Peek-win-x64.zip -CompressionLevel Optimal -Force
```

The zip is a local release artifact. Publish it through CI or GitHub Releases with the source commit SHA and checksum; do not commit generated release archives.

## Anti-Cheat Notes

Peek is an external desktop overlay. It does not inject into the game, hook graphics APIs, read or write process memory, install global input hooks, or automate input.

It uses normal Windows screen capture for the selected area, so compatibility can still vary by game and anti-cheat configuration. Borderless windowed mode is usually the safest display mode.
