# Scripts

## Production-ready

- `deploy-remote-services.ps1`
  - Publishes or reuses artifacts, copies them to a remote Windows machine over PowerShell remoting, registers `fsihost` and `fsharp-devkit` as Windows services, and verifies `/healthz`.
  - `RemoteRoot` must be a writable local fixed disk on the target machine, for example `C:\services\FSharp.MCP.DevKit.Async`.

## Placeholder / demo only

- `fsi-exec.ps1`
- `fsi-smart.ps1`
- `fsi-discover.ps1`
- `fsi-exec-session.ps1`
- `fsi-exec-advanced.ps1`
- `fsi-exec-terminal.ps1`
- `fsi-exec.cmd`
- `fsharp-aliases.ps1`
- `build-packages.sh`

These scripts do not implement a real MCP client flow. They are kept only as stubs or examples and now fail fast instead of pretending to execute successfully.
