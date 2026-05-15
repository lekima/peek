# Peek

Minimal Windows overlay for manually translating a selected game-screen area.

## Download

Download `releases/Peek-win-x64.zip`, unzip it, then run `Peek.exe`.

The zip is self-contained for Windows x64, so it should not require a separate .NET install. Windows SmartScreen may warn because the app is not code-signed.

## How it works

- Use the four-button strip to move, translate, clear, and resize.
- Drag the resize button downward to reveal the capture frame.
- The app captures only the framed area underneath, sends the image to OpenRouter, then shows translated text inside the frame.
- Peek stays available from the Windows notification area while running, using the same yellow move-button icon.

## Settings

Right-click the floating window and choose `Settings`.

- Model: `google/gemini-3.1-flash-lite-preview`
- Default languages: Chinese to Vietnamese
- The app uses vision-to-text translation, so it does not generate edited image output.
- API key storage: encrypted for the current Windows user with DPAPI in `data/settings.json`
- Run on startup: optional per-user Windows startup entry; disabling it removes Peek's startup entry
- Log: `data/peek.log.jsonl`
- Local data cleanup: delete the `data` folder next to `Peek.exe`
- Cost tracking: cumulative total is stored locally, and each request is written as a structured `usage` event in the log

## Run

```powershell
dotnet run --project .\Peek.csproj
```

For strict anti-cheat games, use borderless windowed mode when possible. The app is an external desktop overlay: it does not inject into the game, hook graphics APIs, read or write process memory, install global input hooks, or automate input. It still uses normal Windows screen capture for the selected area, so no app can guarantee compatibility with every anti-cheat.
