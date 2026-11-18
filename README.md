<div align="center">

# UDriver

**Let your agent see, play and test your Unity game.**

![Unity 6000.0+](https://img.shields.io/badge/Unity-6000.0%2B-black?logo=unity)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![MCP](https://img.shields.io/badge/MCP-server-6E56CF)
![Platforms](https://img.shields.io/badge/platforms-macOS%20%7C%20Windows%20%7C%20Linux-informational)
![License](https://img.shields.io/badge/license-UDriver%201.0-blue)

</div>

UDriver connects any agent with MCP support to a running Unity game or the Unity Editor. The agent looks at the
screen, presses buttons, plays, reads logs and game data, and backs every answer with what it actually saw.

It works with UI Toolkit and uGUI, in Play Mode, Edit Mode and game builds.

## Install

You need Unity 6000.0 or newer. UDriver also needs the free .NET 8 SDK from Microsoft; the launcher offers to install it.

1. Put the `udriver` folder somewhere it can stay, for example in Documents.
2. Start the launcher. On a Mac, double-click `UDriver.command` (if macOS blocks it, right-click it and choose **Open**).
   On Windows, double-click `UDriver.cmd` (if Windows warns you, choose **More info**, then **Run anyway**).
   On Linux, run `sh UDriver.command`.
3. Choose how to set up:
   - **In the browser.** The UDriver panel opens. Answer three questions there. You don't type anything in the terminal.
   - **In the terminal.** The same three questions, answered in the launcher window.

The three questions are: which game the agent should control, what it may do, and where your agent runs. At the end
UDriver shows the command to add to your agent's MCP settings. Then open your game or the Unity project and ask the
agent to run `self_test`: it checks what works in your project.

To change your answers later, start the launcher again. The panel also shows the exact terminal command.

To open the panel again while UDriver is running, start the launcher again: it opens the panel without a password. The
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

`editor_menu` is off unless you turn it on with the environment variable `UDRIVER_ENABLE_TOOLS=editor_menu`.
`editor_build` writes builds only to the folders listed in the environment variable `UDRIVER_BUILDS_DIR`, and is off without it.

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
through it: it installs `cloudflared`, signs you in to Cloudflare, lets you pick an address on your domain and asks for
a panel password. You need a domain in a Cloudflare account.

- **Adding an agent.** The panel shows an address and a one-time code. Paste the address into your agent and enter the
  code on the sign-in page. A code works once and expires in 10 minutes.
- **The panel password** opens the panel from another device. Agents never get it. After 10 wrong passwords or codes
  from one address, or 50 from everyone together, sign-in pauses for 10 minutes.
- **From another device** the panel can show your games and agents, stop games, lower access, disconnect agents, give
  one-time codes and restart or turn off the tunnel. Adding games, changing their folders, launching games and raising
  access work only on the computer that runs UDriver.
- **Turning it off** closes the tunnel at once. Agents on this computer keep working.

Calls through remote access are limited to about 60 to 90 seconds, because the proxy drops longer requests. Some web
agents sign in from an address UDriver doesn't know yet. For those, set the environment variable
`UDRIVER_OAUTH_REDIRECT_HOSTS` to the sign-in host before starting the launcher.

## Security

The agent works only inside the games you add. UDriver doesn't give it your files, your terminal or other apps.

- Games and the editor listen only on `127.0.0.1` and accept only requests with a random token made for each run.
  Web pages can't reach them.
- Reading and changing game data can't reach files, processes, the network or environment variables.
- `app_launch` runs only the games you added, with short window and graphics arguments only.
- Screenshots show only the game window, never the desktop.
- `state_export` and `memory_capture` write files to `~/.udriver/exports`. The folder keeps at most 2 GB and deletes
  its oldest files first. To change the limit, set the environment variable `UDRIVER_EXPORTS_MAX_MB` (in megabytes)
  before starting the launcher or the Unity editor.
- Settings, passwords and keys are stored in `~/.udriver` (on Windows `%USERPROFILE%\.udriver`), readable only by you.
- Remote access goes only through a Cloudflare tunnel over HTTPS, with a sign-in for each agent.

What UDriver can't protect against:

- **The agent's own tools.** Many agents can read files or run commands by themselves, without UDriver. Turn that
  off in the agent's settings if you need it isolated.
- **Your game's own code.** With Full control the agent can call any method of your game, including one that writes
  files. Use Watch or Play when you don't need more.
- **Real actions are real.** If the game talks to real servers or accounts, the agent's actions reach them too. Use a
  test environment.
- **Text on the screen.** What the game shows and logs reaches the agent and may try to steer it.
- **Anyone with your panel password** can control UDriver from another device while remote access is on.
- For the strongest isolation, run the agent and Unity in a separate user account or a virtual machine.

Found a vulnerability? Please report it privately, see [SECURITY.md](SECURITY.md).

## Good to know

- Interfaces drawn with IMGUI (`OnGUI`) aren't in the UI tree. The agent uses screenshots and clicks by position.
- `trace_add` needs Harmony 2.4 or newer (`0Harmony.dll` in `Assets/Plugins`) and doesn't work in IL2CPP builds.
- IL2CPP builds strip unused code, so game data reads reach only what was kept. Set *Managed Stripping Level* to Minimal.
- Input through the Input System reaches the game only while its window is in front. Plain UI clicks work in the background.
- While the editor compiles or reloads scripts, it doesn't answer for up to 25 seconds.
- Windows has been tested less than macOS.

## Uninstall

On a Mac or Linux, run:

```bash
sh uninstall.sh
```

It stops the UDriver panel, lists the Unity projects you added, and asks before deleting your
settings. Agents that use UDriver keep running; remove UDriver from their MCP settings.

On Windows, close the UDriver window, remove UDriver from your agent's MCP settings, delete the folder
`Packages\com.udriver.agent` in your Unity projects and the folder `%USERPROFILE%\.udriver`. If you used remote access,
delete the tunnel `udriver` in the Cloudflare dashboard.

## License

Free to use, including at work. Redistribution and resale need written permission. Full terms: [LICENSE](LICENSE).

Copyright (c) 2026 Vladislav Pesotchi.
