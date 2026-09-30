# LlamaMonitor — local inference server monitor

[中文说明](README.md)

LlamaMonitor is a small Windows desktop window that shows what a local LLM inference server is doing: the active backend and model, GPU usage, generation speed, context usage, and recent server log lines.

It reads server status without changing server configuration, sending inference requests, or modifying model files. It may write a local `monitor-diag.log` file for diagnostics.

**v0.2.0** adds service uptime, a floating Mini window, and log refresh that preserves your reading position. It also corrects the calculation of retained slot tokens.

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
| llama.cpp (`llama-server`) | 8080 | Retained slot tokens/limits, prefill/cache/output counts, per-slot speed, KV pool, task timing, logs |
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
- **Service uptime:** time since the actual local listening process started, when its start time is available. Otherwise, including remote services, the label explicitly says **Observed online**: the time the monitor has continuously observed the service online, rather than its total lifetime. Offline data is cleared, and the source is checked again after reconnection or a service change.
- **GPU row:** utilization, power, temperature, and VRAM for NVIDIA GPUs when `nvidia-smi` is available.
- **Live slots:** task state, prefill/cache/output counts, current and recent speed, and retained slot tokens/limit from `/slots`.
- **Recent tasks:** generation speed, recent speed, draft acceptance, prefill progress, and last update parsed from server logs. The table keeps recent task data for up to ten minutes.
- **KV pool:** usage, safety margin, output reserve, and policy when the server exposes `kv_budget_guard`.
- **Log tail:** recent raw server log lines. New lines follow automatically while you are at the bottom. Scrolling up preserves the reading position and selection during refresh; **Latest** returns to the bottom and resumes following. Warning and error counts appear in the status bar.

Slot usage is the token sequence currently retained in a slot, divided by `n_ctx`; a usage percentage is shown when the limit is known. The monitor uses `n_past` when available, otherwise `n_prompt_tokens`. In current llama.cpp, `n_prompt_tokens` already includes generated tokens, so adding the generated count again would double-count them. An idle slot may still retain context. Missing counts or limits display `—`; prefill percentages and cache hit rates are not inferred from these fields. Generated counts support both the top-level field and `next_token.n_decoded`. Generation speed displays `—` when the slot is idle or the generated count is unknown. Offline services do not retain stale slot data in the window.

Buttons: **Topmost** keeps the window on top; **Pause** stops refreshing; **View log** opens the current log in Notepad or its directory in Explorer; **WebUI** opens the server's UI at the configured UI port (by default the server port plus one); **EN / 中文** switches language; **Mini** opens the floating view; **Latest** jumps to the newest log lines. Press `Esc` to close the full window.

The **Mini window** is resizable and shares the full monitor's data refresh. It shows connection status, uptime, compact GPU information, and scrollable slot cards with token usage/limit, generated tokens, cached tokens, and current speed. Backends without slot data show an explicit message. Use the native title bar to drag the window, **Full** to restore the full view, **Pause** to pause or resume shared refresh, **EN / 中文** to switch language, and **Pin** to control Mini's always-on-top setting. Double-clicking the service title inside Mini also restores the full view. Closing Mini with its **X exits the entire monitor**. Start directly in Mini with `--mini`.

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
| `--mini` | Off | Start in the floating Mini window |
| `--list` | Off | List discovered services without opening a window |
| `--lang en` / `--lang zh` | Windows UI language | Select output and interface language |

Examples:

```text
LlamaMonitor.exe
LlamaMonitor-cli.exe --list
LlamaMonitor.exe --port 8080 --lang en
LlamaMonitor.exe --interval 500 --no-topmost
LlamaMonitor.exe --mini
```

If no service is found, try `LlamaMonitor-cli.exe --list`. Automatic port probes include 8080, 11434, 1234, 8000, 30000, 5001, 5000, 1235, and 8081. For a different port, use `--port N`.

If GPU output is missing, check whether an NVIDIA driver is installed and `nvidia-smi` is on `PATH`. If the task table is empty, the selected backend may not support slot data, may be idle, or its log may not be found; use `--log-file P` when needed. Reading another user's server process command line may require elevated rights.

## Build from source

The source is C# 5 WinForms targeting .NET Framework 4.x. On a supported Windows installation with the framework's C# compiler, run `build.cmd` in the project root. The script compiles all eight `src/*.cs` files and writes `dist\LlamaMonitor.exe` (desktop) and `dist\LlamaMonitor-cli.exe` (console). The first argument selects a different output directory:

```text
build.cmd
build.cmd "release-build"
```

The source repository ignores generated `dist` contents. Use the console build with `--list` to pipe discovery output into a script. Building uses the system framework compiler; no .NET SDK is needed. If that compiler is unavailable on a customized system, install or enable .NET Framework 4.x before rebuilding.

Run `test.cmd` for checks using local server and log fixtures. These cover token fields, uptime sources, log reading position, and the full/Mini windows in both languages. The test executable and rendered window artifacts are written under `dist\tests`. Fixture checks do not replace validation against a real inference service, another computer, or human visual review.

## License

Licensed under [MIT](LICENSE).
