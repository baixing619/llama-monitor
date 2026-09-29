# LlamaMonitor — local inference server monitor

[中文说明](README.md)

LlamaMonitor is a small Windows desktop window that shows what a local LLM inference server is doing: the active backend and model, GPU usage, generation speed, context usage, and recent server log lines.

It reads server status without changing server configuration, sending inference requests, or modifying model files. It may write a local `monitor-diag.log` file for diagnostics.

## Requirements

- Windows 10 version 1809 or later, or Windows 11; x64. ARM64 can run it through x64 emulation. Older Windows versions have not been tested.
- The .NET Framework 4.x runtime included with standard supported Windows installations. You do not need the .NET SDK, PowerShell, Python, Node.js, or a Visual C++ redistributable to run the published executable.
- An NVIDIA driver and `nvidia-smi` on `PATH` are optional. Without them, the monitor still works, but GPU statistics are unavailable.

If a customized Windows installation is missing .NET Framework, install the [official .NET Framework 4.8 Runtime](https://dotnet.microsoft.com/download/dotnet-framework/net48).

## Download and start

Download the Windows ZIP from [GitHub Releases](https://github.com/baixing619/llama-monitor/releases), extract it, and double-click `启动监控.cmd` or `LlamaMonitor.exe`. The release also includes `LlamaMonitor-cli.exe` for terminal use. The source repository does not include compiled executables; see [Build from source](#build-from-source).

The interface follows the Windows UI language by default: Chinese for Chinese Windows, English otherwise. Use the `EN / 中文` button to switch while the program is running, or start it with `--lang en` or `--lang zh`.

## Supported backends

LlamaMonitor discovers local backends by inspecting relevant processes and their command-line ports, then probing common local ports. A custom port can be selected with `--port`.

| Backend | Typical port | Available data |
|---|---:|---|
| llama.cpp (`llama-server`) | 8080 | Slot details, prompt progress, cache hit rate, per-slot speed, KV pool, task timing, logs |
| Ollama | 11434 | Model list and availability |
| LM Studio | 1234 | Model list and availability |
| vLLM | 8000 | Model list and cumulative generation metrics |
| SGLang | 30000 | Model list and availability |
| KoboldCpp | 5001 | Model list and availability |
| text-generation-webui | 5000 | Model list and availability |
| LocalAI | 8080 | Model list and availability |
| TabbyAPI | 5000 | Model list and availability |

Only backends with a compatible `/slots` endpoint provide slot-level data. The slot and task tables are hidden for other backends. The optional KV guard row appears only with a custom `kv_budget_guard` build of llama.cpp; official builds do not expose it.

## Window contents

- **Header:** connection status, server address, discovery source, model, slot count, build, and selected log.
- **GPU row:** utilization, power, temperature, and VRAM for NVIDIA GPUs when `nvidia-smi` is available.
- **Live slots:** task state, prompt progress and cache hit rate, generated tokens, current and recent speed, and context usage from `/slots`.
- **Recent tasks:** generation speed, recent speed, draft acceptance, prefill progress, and last update parsed from server logs. The table keeps recent task data for up to ten minutes.
- **KV pool:** usage, safety margin, output reserve, and policy when the server exposes `kv_budget_guard`.
- **Log tail:** recent raw server log lines. Warning and error counts appear in the status bar.

Buttons: **Topmost** keeps the window on top; **Pause** stops refreshing; **Open log** opens the current log in Notepad or its directory in Explorer; **WebUI** opens the server's UI at the configured UI port (by default the server port plus one); **EN / 中文** switches language. Press `Esc` to close the window.

## Command-line options

All options are optional. Without them, LlamaMonitor attempts automatic discovery.

| Option | Default | Purpose |
|---|---|---|
| `--port N` | Auto | Use a specific server port |
| `--host H` | `127.0.0.1` | Server host |
| `--uiport N` | Server port + 1 | Port opened by the WebUI button |
| `--log-file P` | Auto | Watch only this log file |
| `--log-dir D` | Auto | Search this log directory |
| `--interval N` | `1000` ms | Refresh interval; minimum 200 ms |
| `--pid N` | None | Target a specific process |
| `--no-topmost` | Off | Do not start always on top |
| `--list` | Off | List discovered services without opening a window |
| `--lang en` / `--lang zh` | Windows UI language | Select output and interface language |

Examples:

```text
LlamaMonitor.exe
LlamaMonitor-cli.exe --list
LlamaMonitor.exe --port 8080 --lang en
LlamaMonitor.exe --interval 500 --no-topmost
```

If no service is found, try `LlamaMonitor-cli.exe --list`. Automatic port probes include 8080, 11434, 1234, 8000, 30000, 5001, 5000, 1235, and 8081. For a different port, use `--port N`.

If GPU output is missing, check whether an NVIDIA driver is installed and `nvidia-smi` is on `PATH`. If the task table is empty, the selected backend may not support slot data, may be idle, or its log may not be found; use `--log-file P` when needed. Reading another user's server process command line may require elevated rights.

## Build from source

The source is C# WinForms targeting .NET Framework 4.x. On a supported Windows installation with the framework's C# compiler, run `build.cmd` in the project root. The script compiles all four `src/*.cs` files and writes `dist\LlamaMonitor.exe` (desktop) and `dist\LlamaMonitor-cli.exe` (console). Use the console build with `--list` to pipe discovery output into a script. It uses the system framework compiler; no .NET SDK is needed. If that compiler is unavailable on a customized system, install or enable .NET Framework 4.x before rebuilding.

## License and roadmap

Licensed under [MIT](LICENSE). A floating Mini window is planned for a later version; it is not included in this release.
