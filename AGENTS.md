# Rules for every AI agent (Claude, Codex, Cursor, any other)

## Everything runs in the sandbox, never on the Mac

The owner's Mac is not a build machine. Builds, tests, servers, package installs and any background process run
inside the development sandbox (`.devcontainer/`, OrbStack/Docker), so that `tools/dev stop` switches all of it off
and nothing keeps running or piles up on the Mac.

- One command: `tools/dev run <command>`, e.g. `tools/dev run dotnet build UDriver.Mcp`,
  `tools/dev run dotnet build apps/linux`, `tools/dev run sh native/build.sh` (Linux `libudriver-il2cpp.so` for this
  container's CPU), `tools/dev run sh packaging/pack-linux.sh 1.0.0` (AppImage; `RID=linux-arm64` for ARM).
- A shell inside: `tools/dev`. The agent itself inside: `tools/dev claude` or `tools/dev codex`.
- Never run `dotnet`, `clang`, `npm`, `npx`, `node`, `make`, `vpk`, a server or a test script directly on the Mac.
  Never install anything on the Mac (no `brew`, no global `npm -g`, no `pip install`, no `dotnet tool install -g`).
- Inside the sandbox anything is allowed: `sudo apt-get install`, global packages, extra tools.
- A server started inside is reachable from the Mac at http://dev.unity-mcp-driver-dev.orb.local:<port> (OrbStack
  gives every container such a name); it must listen on 0.0.0.0, not 127.0.0.1.
- `bin/` and `obj/` of `UDriver.Mcp`, `apps/linux`, `apps/windows` and `packaging/build/` inside the sandbox are its
  own; the Mac's copies are not used. NuGet packages and logins live in the sandbox home.
- Built on macOS only (the Mac or a UTM virtual machine), never in the sandbox: the Swift app `apps/mac`
  (`apps/mac/build-app.sh`), `packaging/pack-mac.sh`, the macOS `native/udriver-il2cpp.dylib`, and everything that
  needs the Unity editor: the package `com.udriver.agent` in a Unity project, the projects in `tests/*`, game builds,
  `tests/apps-check.py` and `native/test.py` (they drive built games).
- Windows (`apps/windows`, WinUI) does not build on Linux: its XAML compiler runs only on Windows. Build it on Windows
  with `powershell -File packaging\pack-windows.ps1 1.0.0`, or let GitHub Actions do it (`.github/workflows/release.yml`).
- When the task is done, stop what you started (servers, watchers). The owner may run `tools/dev stop` at any time:
  never rely on a process inside surviving it.
- A git worktree gets a sandbox of its own (containers and volumes named after the worktree). Before removing a
  worktree run `tools/dev reset` in it, so nothing of it stays in OrbStack.
- `tools/dev` starts OrbStack by itself when it is not running.
- Only git (status, diff, commit, branch) and reading or editing files may happen on the Mac. Pushing too.
- Every commit is checked by `.githooks/pre-commit` (`tools/dev` switches it on): the repository is public, so it
  refuses any file in `secrets/`, a `.env` file, a private key or an access token. Never skip it.

## No AI marks

No trace of AI anywhere: no `Co-Authored-By` lines naming a model, no "Generated with ..." lines, no links to
agent sessions, no notes that an AI did the work — in commits, PRs, code, comments, docs and messages.
