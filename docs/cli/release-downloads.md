# Headless from a release download

The desktop EXE opens the editor window. For commands without a window, use the separate CLI launcher beside it. You do not need to run the editor first.

## Windows

Open a terminal in the extracted download folder:

```powershell
.\metroidvania-studio-cli.cmd help
mkdir maps
.\metroidvania-studio-cli.cmd --workspace ./maps init --map world.map.json
.\metroidvania-studio-cli.cmd --workspace ./maps inspect --map world.map.json
```

The Windows download includes the runtime. The web download uses an installed .NET 10 runtime. Neither CLI launcher requires Node.js or a game engine. The workspace folder must exist before running a map command.

## macOS and Linux

```sh
bash ./metroidvania-studio-cli.sh help
mkdir maps
bash ./metroidvania-studio-cli.sh --workspace ./maps init --map world.map.json
bash ./metroidvania-studio-cli.sh --workspace ./maps inspect --map world.map.json
```

The macOS and Linux downloads include runtimes for ARM64 and x64. The web download uses an installed .NET 10 runtime.

## Run a Lua script

Copy a Lua example from `app/docs/examples/` into your `maps` folder, naming it `layout.lua`. Preview the changes before saving:

```powershell
.\metroidvania-studio-cli.cmd --workspace ./maps run --map world.map.json --script layout.lua --dry-run
.\metroidvania-studio-cli.cmd --workspace ./maps run --map world.map.json --script layout.lua
.\metroidvania-studio-cli.cmd --workspace ./maps validate --map world.map.json
```

On macOS or Linux, replace `.\metroidvania-studio-cli.cmd` with `bash ./metroidvania-studio-cli.sh`. Filenames after `--map`, `--script`, and `--output` are relative to `--workspace`, not to the launcher location. Arguments with spaces must be quoted.

Commands print JSON to standard output and return an exit code. They do not open a browser or desktop window. See [CLI commands](commands.md) for every command and [Lua examples](../scripting/quick-start.md) for script syntax.

## Use an MCP client

Configure a client to launch `metroidvania-studio-cli.cmd mcp --workspace ...` on Windows, or `bash metroidvania-studio-cli.sh mcp --workspace ...` on macOS/Linux. Use full paths in your own client configuration.

If a Windows client cannot start `.cmd` files directly, set its command to `app/runtime/win-x64/dotnet.exe` from the extracted Windows download and put `app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll` first in the arguments, followed by `mcp`, `--workspace`, and your workspace path. This starts the same server without a shell. See [MCP setup](../mcp/setup.md).

For a map already open in the web editor, use [live mode](../api/live-api.md). Offline mode protects the workspace from concurrent writers.

## Read the API documentation

In the editor, open **Help → Documentation** or **Help → API reference**. In the extracted download, open `app/metroidvania-studio/dist/docs/index.html` directly. Search, navigation, and examples work without an internet connection.
