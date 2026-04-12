# Scripts

## Production-ready

- `deploy-remote-services.ps1`
  - Publishes or reuses artifacts, copies them to a remote Windows machine over PowerShell remoting, registers only `fsharp-devkit` as a Windows service, and verifies `/healthz`.
  - Stages legacy `.NET Framework` `FsiHost` artifacts under `<RemoteRoot>\hosts\netfx` for later `create_fsi_host` use, but does not register `fsihost` as a service.
  - `RemoteRoot` must be a writable local fixed disk on the target machine, for example `C:\services\FSharp.MCP.DevKit.Async`.
  - Use `-RecreateServices` when you need the script to delete existing service registrations before recreating them.
- `deploy-local-service.ps1`
  - Windows local deployment script.
  - Publishes and installs only the `fsharp-devkit` server as a Windows service.
  - Stages legacy `.NET Framework` `FsiHost` artifacts under the deploy root for later `create_fsi_host` use, but does not register `fsihost` as a service.
- `uninstall-local-service.ps1`
  - Windows local uninstall script.
  - Stops and deletes `fsharp-devkit` plus any legacy `fsihost` service registration, and can optionally remove the deploy root.
- `deploy-local-win-service.ps1`
  - Windows local deployment script aligned with the `FSharp.MCP.DevKit.exe` Windows Service path.
  - Publishes `FSharp.MCP.DevKit.Server`, registers `fsharp-devkit` as an auto-start service, and verifies `/healthz`.
- `uninstall-local-win-service.ps1`
  - Windows local uninstall script aligned with `deploy-local-win-service.ps1`.
  - Stops and deletes the `fsharp-devkit` service registration, and can optionally remove the staged service directory or deploy root.

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
