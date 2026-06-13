# BrowseRouterAOT

Route every link click on Windows to the browser **you** want, based on JSON rules. Native-AOT, two-process design — the Launcher exe is tiny (~3 MB) and the long-lived Host caches your rules in memory so each click is forwarded over a named pipe in milliseconds.

```
 click in Teams                                         opens in Edge
 ─────────────►  Launcher.exe  ─── named pipe ───►  Host.exe  ───►  msedge.exe
                  (~3 MB AOT)        \\.\pipe\…       (resident,
                                                       rules cached)
```

Inspired by [nref/BrowseRouter](https://github.com/nref/BrowseRouter); rewritten on **.NET 10 + Native AOT** with a clean JSON schema, hot-reloading config.

Toast popup UI inspired by [noxad/windows-toast-notifications](https://github.com/noxad/windows-toast-notifications) — ported from WinForms to pure Win32 P/Invoke (no `System.Windows.Forms` / `System.Drawing`) to fit the AOT-only-Win32 rule.

---

## Features

- **JSON rules** with explicit matcher types: `hostSuffix` / `exactHost` / `pathPrefix` / `regex`, plus an optional `exclude` clause for "all of X except Y".
- **Source rules** route by the calling process (e.g. links from `TEAMS.EXE` always open in Edge).
- **URL filters** rewrite URLs before launch — strip `utm_*`, unwrap SafeLinks, etc. Regex with `$1` and an `unescape($1)` macro.
- **Background daemon** — rules parsed once, then in-memory. Hot-reloaded on file save.
- **Tray icon** with Reload / Open config / Open log / Default Apps / Quit menu.
- **Custom toast popups** (`BrowseRouter (AOT)` / `edge->https://…`) — self-drawn Win32 windows, not subject to Action Center's ~5 s floor.
- **Foreground-aware** — brings the chosen browser to the front even when it was sitting in the background.
- **No console flash** — Launcher runs as GUI subsystem, so Windows never briefly shows a black window on URL clicks.
- **Per-user install** — no admin needed. Registration is plain HKCU.
- **Native AOT** — fast cold start, no .NET runtime dependency.

---

## Install

```powershell
# 1. Build — both AOT exes (+ browsers.template.json) land in .\dist
.\build.ps1                       # win-x64 (default)
.\build.ps1 -Rid win-arm64        # cross-publish to Windows on ARM
# or, equivalent without the wrapper:
#   dotnet publish src\BrowseRouter.Launcher -c Release -r <rid> -o dist
#   dotnet publish src\BrowseRouter.Host     -c Release -r <rid> -o dist

# 2. Copy the dist payload to a permanent install dir, e.g.
$dst = "$env:LOCALAPPDATA\Programs\BrowseRouterAOT"
mkdir $dst -Force | Out-Null
Copy-Item dist\BrowseRouter.Launcher.exe $dst
Copy-Item dist\BrowseRouter.Host.exe     $dst
Copy-Item dist\browsers.template.json    $dst

# 3. Register as a default-browser candidate (writes HKCU keys + autostart)
& "$dst\BrowseRouter.Host.exe" --register
# Windows Settings opens — select "BrowseRouter (AOT)" as your default browser.
```

To uninstall:

```powershell
& "$dst\BrowseRouter.Host.exe" --unregister
# Then delete $dst.
```

---

## Configuration (`browsers.json`)

Location: `%AppData%\BrowseRouterAOT\browsers.json` — seeded from the template on first run. Edit; save; the Host picks the change up within ~300 ms.

```jsonc
{
  "notify": { "enabled": false },
  "log":    { "enabled": true },
  "defaultBrowser": "edge",

  "browsers": {
    "edge":    { "path": "%ProgramFiles(x86)%\\Microsoft\\Edge\\Application\\msedge.exe" },
    "chrome":  {
      "path": "%ProgramFiles%\\Google\\Chrome\\Application\\chrome.exe",
      "args": ["--profile-directory=Default", "{url}"]
    },
    "ff-work": {
      "path": "%ProgramFiles%\\Mozilla Firefox\\firefox.exe",
      "args": ["ext+container:name=Work&url={url}"]
    }
  },

  "rules": [
    { "browser": "edge",    "match": { "type": "hostSuffix",  "value": "teams.microsoft.com" } },
    { "browser": "ff-work", "match": { "type": "hostSuffix",  "value": "mycompany.com" } },
    { "browser": "chrome",  "match": { "type": "regex",       "value": "^https?://localhost(:\\d+)?/" } },
    { "browser": "edge",    "match": { "type": "pathPrefix",  "value": "/maps", "host": "google.com" } },

    {
      "browser": "chrome",
      "match":   { "type": "hostSuffix", "value": "google.com" },
      "exclude": { "type": "pathPrefix", "value": "/maps" }
    }
  ],

  "sourceRules": [
    { "browser": "edge", "match": { "type": "process", "value": "TEAMS.EXE" } }
  ],

  "filters": [
    { "name": "strip utm", "find": "(.*)[&?]utm_source=[^&]+(.*)", "replace": "$1$2", "priority": 1 }
  ]
}
```

### Resolution order

1. **`sourceRules`** — first matching rule wins.
2. **`rules`** — first matching `match` (and not matching `exclude`) wins.
3. **`defaultBrowser`** — fallback.

Filters are applied **before** matching so e.g. a SafeLinks-wrapped URL hits the rule for the real host.

### Matcher types (URL)

| `type`        | What it checks                                                |
|---------------|---------------------------------------------------------------|
| `hostSuffix`  | Host equals or ends with `.value` (case-insensitive). 80 % case. |
| `exactHost`   | Host equals `value`.                                          |
| `pathPrefix`  | Path starts with `value`; optional `host` adds a hostSuffix gate. |
| `regex`       | .NET regex against the **full URL**. AOT-interpreted (no `Compiled`). |

### Matcher types (source)

| `type`                 | What it checks                                  |
|------------------------|-------------------------------------------------|
| `process`              | Calling process file name (e.g. `TEAMS.EXE`).   |
| `processPath`          | Exact full path.                                |
| `processPathPrefix`    | Path starts with `value`.                       |
| `windowTitleContains`  | Substring of the calling window's title.        |
| `windowTitleRegex`     | Regex against the calling window's title.       |

### `args` template tags

Replaced inside each element of `browsers.<name>.args`:

| Tag         | Value                                          |
|-------------|------------------------------------------------|
| `{url}`     | Full URL (post-filter).                        |
| `{rawUrl}`  | Original URL as received from Windows.         |
| `{host}`    | Host name.                                     |
| `{authority}` | `user:pass@host:port`                        |
| `{path}`    | Path (with leading `/`).                       |
| `{query}`   | Query (with `?`).                              |
| `{fragment}`| Fragment (with `#`).                           |
| `{userinfo}`| Userinfo portion.                              |
| `{port}`    | Port number.                                   |

If **no** element contains any tag, `{url}` is appended automatically — so `"args": ["--incognito"]` works as you'd expect.

### Filters

```json
{ 
  "name":    "Unwrap Outlook SafeLinks",
  "find":    ".*safelinks\\.protection\\.outlook\\.com.*[?&]url=([^&]+).*",
  "replace": "unescape($1)",
  "priority": 2 
}
```

- Sorted by `priority` ascending; **the first filter that changes the URL wins** (matches original BrowseRouter semantics).
- `$N` → capture group N.
- `unescape($N)` → URL-decoded capture group N (handy when SafeLinks double-encodes).
- A single faulty filter is logged and skipped — others keep running.

---

## CLI

`BrowseRouter.Host.exe` is both the daemon and the setup tool:

```
BrowseRouter.Host.exe                  Run as daemon (default).
BrowseRouter.Host.exe --host           Same as above (explicit).
BrowseRouter.Host.exe --register       Register as a default-browser candidate.
BrowseRouter.Host.exe --unregister     Remove registration + autostart.
BrowseRouter.Host.exe --auto           Toggle register / unregister.
BrowseRouter.Host.exe --help           Show help.
```

`BrowseRouter.Launcher.exe` forwards URLs (this is what Windows actually invokes):

```
BrowseRouter.Launcher.exe <url> [<url> …]
```

Subcommand flags (`--register`, etc.) on the Launcher are delegated to the Host.

---

## Logs

`%LocalAppData%\BrowseRouterAOT\logs\YYYY-MM-DD.log` — one entry per click, plus reload / error notes. Disable with `"log": { "enabled": false }` in the config.

---

## Known limits

- **Windows 10 1803+** does not let any app silently set itself as the default browser. After `--register`, the Default Apps page opens — you must click "BrowseRouter (AOT)" once.
- **Regex from config** is AOT-interpreted (no JIT for `RegexOptions.Compiled`). Performance is fine for one URL per click but heavy regex on every match list would be visible.
- **Per-user only** — registration is HKCU. No machine-wide setup.