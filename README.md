<div align="center">

# UDriver

**Let your agent see, play and test your Unity game.**

![Unity 6000.0+](https://img.shields.io/badge/Unity-6000.0%2B-black?logo=unity)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![MCP](https://img.shields.io/badge/MCP-server-6E56CF)
![Platforms](https://img.shields.io/badge/platforms-macOS%20%7C%20Windows%20%7C%20Linux-informational)
![License](https://img.shields.io/badge/license-UDriver%201.0-blue)

</div>

UDriver connects any agent with MCP support to a running Unity game or the Unity Editor. The agent looks at the
screen, presses buttons, plays, reads logs and game data, and backs every answer with what it actually saw.

It works with UI Toolkit and uGUI, in Play Mode, Edit Mode and game builds.

## Install

You need Unity 6000.0 or newer. UDriver also needs the free .NET 10 SDK from Microsoft; if it is missing, the launcher
installs it (with Homebrew on a Mac, with winget on Windows) or opens Microsoft's download page.

1. Put the `udriver` folder somewhere it can stay, for example in Documents.
2. Start the launcher. On a Mac, double-click `UDriver.command` (if macOS blocks it, right-click it and choose **Open**).
   On Windows, double-click `UDriver.cmd` (if Windows warns you, choose **More info**, then **Run anyway**).
   On Linux, run `sh UDriver.command`.
3. The UDriver panel opens in your browser. Answer three questions there. You don't type anything in the launcher
   window: it only says that UDriver is running. Keep it open; closing it stops UDriver.

To answer the same questions in a terminal instead, run `sh UDriver.command setup` on a Mac or Linux, or
`UDriver.cmd setup` in a Windows command prompt opened in the `udriver` folder.

The three questions are: which game the agent should control, what it may do, and where your agent runs. At the end
UDriver shows the command to add to your agent's MCP settings. Then open your game or the Unity project and ask the
agent to run `self_test`: it checks what works in your project.

To change your answers later, start the launcher again. The panel also shows the exact terminal command.

To open the panel again while UDriver is running, start the launcher again: it opens the panel right away. The
link it opens works once and only for a minute, so a copied link or browser history can't open the panel later.

The panel and the server run only while the launcher window is open. Nothing is added to startup and no background
service is installed.

## What the agent can do

| Group | Tools |
|---|---|
| Start and find games | `app_launch`, `app_kill`, `app_status`, `app_connect`, `targets` |
| See the screen | `status`, `ui_tree`, `ui_find`, `ui_inspect`, `screenshot`, `screen_pick`, `locate` |
| Act like a player | `ui_click`, `ui_hover`, `ui_drag`, `pointer`, `ui_scroll`, `ui_scroll_to`, `ui_set_value`, `ui_focus`, `type_text`, `key`, `play`, `input_actions`, `batch` |
| Logs and events | `events_query`, `events_config`, `errors`, `wait_for` |
| Game data | `scene_tree`, `state_get`, `state_query`, `state_export`, `state_set`, `state_call`, `types_find`, `type_members` |
| Probes and time | `watch_add`, `trace_add`, `trigger_add`, `probe_list`, `probe_remove`, `frames_record`, `frames_get`, `time_control` |
| Performance | `profiler_get`, `profiler_list`, `render_stats`, `render_scene` |
| Memory | `memory_summary`, `memory_top`, `memory_snapshot`, `memory_watch`, `memory_refs`, `memory_capture` |
| Unity Editor | `editor_status`, `editor_play`, `editor_compile`, `editor_tests`, `editor_build`, `editor_scene`, `editor_assets`, `editor_menu` |
| Check | `self_test` |

Every tool has a level. In the panel, under **What agents may do**, each level is turned on or off in advance, separately
for agents on this computer and for remote agents. A tool whose level is off is not shown to agents at all.

- **0 Look.** Sees the screen, the scene, values, logs and performance. Changes nothing.
- **1 Play.** Also clicks, types, presses keys, starts and stops your games and runs tests.
- **2 Change the game.** Also changes values, scenes and builds inside the game and its project. At your own risk.
- **3 Computer access.** Also runs any game code (`state_call`) and editor menu commands (`editor_menu`), which can reach
  files, programs and the network of this computer. At your own risk.

Levels 0 to 2 are on for agents on this computer, 0 and 1 for remote agents. Computer access is off; only you can turn
it on, on the computer that runs UDriver (`udriver-mcp computer on`), and remote agents never get it.
`udriver-mcp levels` shows the levels; `udriver-mcp levels on|off <0-2> [--remote]` changes them.
`editor_build` writes builds only inside the project folder and the folders listed in the environment variable `UDRIVER_BUILDS_DIR`, and is off without it.

## Several games and access levels

Add as many games as you need, for example two game clients side by side. Each one is a Unity project or a game build.
The agent can drive them at the same time and tells them apart by name.

Each game has its own access level, which you set in the panel or in the terminal setup:

- **Watch.** Looks at the screen, the interface, the logs and the game data. Changes nothing.
- **Play.** Also clicks, types, moves, presses keys, starts and stops the game and runs tests, like a player or a tester.
- **Full control.** Also changes game data, calls game code, builds the game and opens scenes. Use it for debugging.

The panel lists every connected agent. You can disconnect any of them at any time.

**Game builds without the project.** The build must be a Development Build made with the `com.udriver.agent` package in
its project. To leave UDriver out of the builds you ship to players, add `UDRIVER_DISABLE` under *Player Settings >
Scripting Define Symbols*.

## Remote access

To use UDriver from another device or from a web agent, turn on **Remote access** in the panel. The panel walks you
through two steps: it installs `cloudflared`, signs you in to Cloudflare and lets you pick an address on your domain.
You need a domain in a Cloudflare account.

Agents and devices get in with a **one-time code**. A code lets in one agent or one device, once, and expires in 10
minutes. You get it in the panel on this computer only. While no code is waiting, new agents cannot even register.

- **Adding an agent.** Under Remote access, choose **Add a remote agent**. Paste the address into your agent and enter
  the code on the sign-in page.
- **Opening the panel on a phone, tablet or another computer.** Under Remote access, choose **Open on another
  device**. Open the address on that device and enter the code. The device stays signed in for 30 days and is listed
  under Connected devices, where you can disconnect it at any time.
- After 10 wrong codes from one address, or 50 from everyone together, sign-in pauses for 10 minutes.
- **From another device** the panel can show your games and agents, stop games, lower access, disconnect agents and devices, give
  one-time codes and restart or turn off the tunnel. Adding games, changing their folders, launching games and raising
  access work only on the computer that runs UDriver.
- **Turning it off** closes the tunnel at once. Remote agents and devices lose access; agents on this computer keep
  working.

Calls through remote access are limited to about 60 to 90 seconds, because the proxy drops longer requests.

## Security

The agent works only inside the games you add. UDriver doesn't give it your files, your terminal or other apps.

- Games and the editor listen only on `127.0.0.1` and accept only requests with a random key made for each run. The key
  stays in memory, never in a file. A game or editor you start yourself gets its key once you allow it in the panel.
  Web pages can't reach them.
- Reading and changing game data can't reach files, processes, the network or environment variables.
- `app_launch` runs only the games you added, with short window and graphics arguments only.
- Screenshots show only the game window, never the desktop.
- `state_export` and `memory_capture` write files to `~/.udriver/exports`. The folder keeps at most 2 GB and deletes
  its oldest files first. To change the limit, set the environment variable `UDRIVER_EXPORTS_MAX_MB` (in megabytes)
  before starting the launcher or the Unity editor.
- Settings and keys are stored in `~/.udriver` (on Windows `%USERPROFILE%\.udriver`), readable only by you.
- Remote access goes only through a Cloudflare tunnel over HTTPS. Each agent and each device signs in with its own
  one-time code.

What UDriver can't protect against:

- **The agent's own tools.** Many agents can read files or run commands by themselves, without UDriver. Turn that
  off in the agent's settings if you need it isolated.
- **Your game's own code.** With Full control the agent can call any method of your game, including one that writes
  files. Use Watch or Play when you don't need more.
- **Real actions are real.** If the game talks to real servers or accounts, the agent's actions reach them too. Use a
  test environment.
- **Text on the screen.** What the game shows and logs reaches the agent and may try to steer it.
- **A connected device** can control UDriver while remote access is on. Disconnect devices you no longer use.
- For the strongest isolation, run the agent and Unity in a separate user account or a virtual machine.

Found a vulnerability? Please report it privately, see [SECURITY.md](SECURITY.md).

## Good to know

- Interfaces drawn with IMGUI (`OnGUI`) aren't in the UI tree. The agent uses screenshots and clicks by position.
- `trace_add` needs Harmony 2.4 or newer (`0Harmony.dll` in `Assets/Plugins`) and doesn't work in IL2CPP builds.
- IL2CPP builds strip unused code, so game data reads reach only what was kept. Set *Managed Stripping Level* to Minimal.
- Input through the Input System reaches the game only while its window is in front. Plain UI clicks work in the background.
- While the editor compiles or reloads scripts, it doesn't answer for up to 25 seconds.
- Windows has been tested less than macOS.

## In the terminal

`udriver` (in the `packaging` folder, or next to the server in an installed copy; `udriver.cmd` on Windows) opens a
full-screen terminal view with four tabs: Games, Agents, Tools and Access. It starts UDriver if it isn't running.
When an app asks for access, the view shows its check number; press **A** to allow or **D** to deny. **N** shows a
one-time code for a new agent, **Q** quits. The same things as single commands:

```bash
udriver status
udriver approve <id>
udriver deny <id>
udriver code
udriver tools off <tool> --for <app>
```

The Linux AppImage takes the same words: `./UDriver.AppImage tui`, `./UDriver.AppImage status`.

## Installers and updates

Each script builds one system's installer and its update files into `packaging/releases`. The version is the only
argument, and each needs the .NET 10 SDK:

- Mac: `packaging/pack-mac.sh 1.0.0` makes `UDriver-osx-Setup.pkg` and a zip with UDriver.app and the server inside.
- Linux: `packaging/pack-linux.sh 1.0.0` makes `UDriver.AppImage` (`RID=linux-arm64` for ARM computers).
- Windows: `powershell -File packaging\pack-windows.ps1 1.0.0` makes `UDriver-win-Setup.exe` (`-Runtime win-arm64`
  for ARM computers). After Setup the terminal view opens; later, run `udriver.cmd` from the install folder.

To publish a version, upload every file from `packaging/releases` to a new release at
github.com/dev-pikapik/unity-mcp-driver/releases. Installed copies look there: the panel's update button and
`GET /panel/api/update` show a newer version, and `POST /panel/api/update/apply` installs it. Set
`UDRIVER_UPDATE_URL` to a folder or another address to test updates from there.

### Signing the Mac version

Without signing, macOS warns on first start and people must right-click UDriver and choose **Open**. To sign:

1. In your Apple Developer account, create two certificates: **Developer ID Application** and
   **Developer ID Installer**. Install both in Keychain on this Mac.
2. Create an app-specific password at account.apple.com (Sign-In and Security, App-Specific Passwords).
3. Save the notarization login once under a name you choose, for example `udriver-notary`:

   ```bash
   xcrun notarytool store-credentials udriver-notary --apple-id you@example.com --team-id ABCDE12345 --password xxxx-xxxx-xxxx-xxxx
   ```

   Your team ID is on developer.apple.com under Membership details.
4. Build with your names and that profile name:

   ```bash
   SIGN_APP="Developer ID Application: Your Name (ABCDE12345)" SIGN_INSTALL="Developer ID Installer: Your Name (ABCDE12345)" NOTARY_PROFILE=udriver-notary packaging/pack-mac.sh 1.0.0
   ```

   `security find-identity -v -p basic` lists the exact certificate names.

## Uninstall

On a Mac or Linux, run:

```bash
sh uninstall.sh
```

It stops the UDriver panel, puts back the original files of every game build UDriver was added to, lists the
Unity projects you added, and asks before deleting your settings (kept if a game could not be put back). Agents that use UDriver keep running; remove UDriver from their MCP settings.

On Windows, double-click `uninstall.cmd`. It does the same: stops UDriver, puts back the games, lists the Unity projects you added, tells
you how to delete the Cloudflare tunnel, and asks before deleting `%USERPROFILE%\.udriver`.

## License

Free to use, including at work. Redistribution and resale need written permission. Full terms: [LICENSE](LICENSE).

Copyright (c) 2026 Vladislav Pesotchi.
