# Peek

Minimal Windows overlay for manually translating a selected game-screen area.

## Release

Build `releases/Peek-win-x64.zip`, unzip it, then run `Peek.exe`.

The release zip is self-contained for Windows x64, so it should not require a separate .NET install. Windows SmartScreen may warn because the app is not code-signed.

## How it works

- Use the four-button strip to move, translate, clear, and resize.
- Drag the resize button downward to reveal the capture frame.
- The app captures only the framed area underneath, sends the image to OpenRouter, then shows translated text inside the frame.
- Right-click the move button to choose the target game, open Settings, or quit.

## Settings

Right-click the move button, then choose `Settings`.

- Target language: configurable, default English
- Model: configurable OpenRouter model id, default `google/gemini-3.1-flash-lite`
- Search: search buttons open Bilibili Chinese search.
- Target game: selected from the move-button context menu; choose `Any game` for no added prefix. Current games are Roco Kingdom: World, Honor of Kings: World, Honor of Kings: Chess, and Honor of Kings.
- API key storage: encrypted for the current Windows user with DPAPI in `data/settings.json`
- Log: `data/peek.log.jsonl`
- Review data: source captures are saved in `data/captures`, and translations/search queries are logged as `text_result` events.
- Font: Roboto and Roboto Condensed are bundled from Google Fonts under the SIL Open Font License in `Resources/Fonts`; Settings uses regular Roboto and overlay translation text uses Roboto Condensed Semibold.
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

Before pushing code changes that affect the app, rebuild and commit `releases/Peek-win-x64.zip` with the same change.

For strict anti-cheat games, use borderless windowed mode when possible. The app is an external desktop overlay: it does not inject into the game, hook graphics APIs, read or write process memory, install global input hooks, or automate input. It still uses normal Windows screen capture for the selected area, so no app can guarantee compatibility with every anti-cheat.
