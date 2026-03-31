# FSharp.MCP.DevKit

`FSharp.MCP.DevKit` is an MCP server for running **remote out-of-process F# Interactive hosts and sessions**.  
The practical focus of this fork is:

- create a remote FSI host
- create one or more remote sessions on that host
- run expensive initialization once
- keep the initialized state alive
- query that state repeatedly from later MCP calls

> [!IMPORTANT]
> All host paths, mount paths, host IPs, container names, and service snippets in this README are **deployment examples from one working environment**, not universal defaults.
> Before using them, replace them with values that match **your own host**, **your own Docker mounts**, and **your own network layout**.

## Upstream / Author

This project was originally created by **EHotwagner**. The upstream/original repository is:

- <https://github.com/ehotw/FSharp.MCP.DevKit>

The upstream README states that the project is currently **on hold**. This fork keeps the remote host / remote session workflow working because it is still useful for long-running, stateful F# workloads.

## csharp-sdk Dependency

This project uses the official `ModelContextProtocol` C# SDK source tree (`csharp-sdk`), not a private fork of the protocol surface.

- upstream repository:
  - <https://github.com/modelcontextprotocol/csharp-sdk>
- local commit used in this workspace:
  - `498de089fc3d42d9dfd28b0e26bee21e6b89b174`

If you want to build this project yourself, make sure you obtain a compatible checkout of `csharp-sdk` first.

## What This Fork Is For

The main use case is:

1. create a remote out-of-process host
2. create a session on that host
3. run heavy setup once
4. keep the session alive
5. evaluate multiple follow-up expressions against the same initialized session

This is useful when plain `dotnet fsi some.fsx` would force you to pay the full initialization cost on every query.

## Akka.NET-Based Isolation

This fork does use **Akka.NET** in the remote execution path.

In practice the isolation model is:

- `Akka.Proc.Supervisor` manages remote procnode processes
- each remote host is provisioned as a separate procnode process
- inside that procnode, `Akka.FSI.Supervisor` manages the FSI control plane
- each FSI session is represented and coordinated through actors

This gives useful operational isolation:

- process-level isolation between remote hosts
- session-level isolation inside a host
- actor-based control messages for execution, health checks, listing sessions, reset, and evaluation routing

It is not “just using Akka for branding”; the remote host/session model really is built around actor boundaries and message routing.

## Core MCP Tools

The minimal tool flow is:

1. `register_fsi_agent`
2. `create_fsi_host`
3. `create_fsi_session`
4. `execute_f_sharp_code_routed` or `execute_f_sharp_code_async_routed`
5. `get_async_status` if using async execution
6. `evaluate_f_sharp_expression_routed`

Useful companion tools:

- `get_fsi_host_health`
- `get_fsi_state_routed`
- `reset_fsi_session_routed`
- `restart_fsi_session`
- `add_search_path_routed`
- `reference_assembly_routed`
- `get_lines`

## Recommended Execution Pattern

For short setup and quick probes:

- use `execute_f_sharp_code_routed`

For heavy initialization:

- use `execute_f_sharp_code_async_routed`
- poll with `get_async_status`
- once completed, query values using `evaluate_f_sharp_expression_routed`

This is the intended pattern for expensive initialization followed by many cheap queries.

## Remote Path Mapping: The Main Trap

The most common failure is **path confusion between containers**.

In the typical deployment used here:

- host path `/home/sa/gemini4/...`
- mounted into `fsharp-devkit` container as `/gemini4/...` (read-only)
- mounted into `fsharp-devkit` container as `/workspace/...` for writable workspace data

These example paths are not special. They only describe one concrete deployment. In your environment, you must substitute your own host paths and container mount points.

Practical rule:

- use `/gemini4/...` for reading source trees, `.fsx`, referenced DLLs, and data already present on the host
- use `/workspace/...` only for writable outputs or temporary files inside the devkit container

If your agent runs in a different container and sees `/workspace/home/...`, that **does not mean** the remote FSI host sees the same path.  
When sending code into the remote host, rewrite paths to the paths visible **inside the `fsharp-devkit` container**.

Typical examples:

- local/agent-visible path:
  - `/workspace/home/work/coldfar-symbolics/...`
- remote host-visible path:
  - `/gemini4/work/coldfar-symbolics/...`

- local/agent-visible path:
  - `/workspace/home/work/sharftrade7/實驗/SharFTrade.Exp/bin/net10.0`
- remote host-visible path:
  - `/gemini4/work/sharftrade7/實驗/SharFTrade.Exp/bin/net10.0`

If you need this workflow, in practice you usually want:

- local machine
- Docker
- explicit `-v <host-path>:<container-path>` mounts

Without correct path mapping, `#I` / `#r` for non-NuGet assemblies will fail.

## Basic Service Example

Below is a basic `systemd` service example for running the server in Docker with host networking and the two important mounts:

```ini
[Unit]
Description=FSharp MCP DevKit Docker Service
After=network-online.target docker.service
Wants=network-online.target
Requires=docker.service

[Service]
Type=simple
Restart=always
RestartSec=5
EnvironmentFile=-/etc/default/fsdevkit

ExecStartPre=/usr/bin/bash -lc '\
  if /usr/bin/docker ps -aq --filter name=^fsharp-mcp-devkit$$ | grep -q .; then \
    exec /usr/bin/docker rm -f fsharp-mcp-devkit; \
  fi'

ExecStart=/usr/bin/bash -lc '\
  set -- $$(hostname -I); \
  HOST_IP="$${FSDEVKIT_HOST_IP:-$$1}"; \
  if [ -z "$$HOST_IP" ]; then \
    echo "fsdevkit.service: unable to determine FSDEVKIT_HOST_IP" >&2; \
    exit 1; \
  fi; \
  exec /usr/bin/docker run --name fsharp-mcp-devkit \
    --network host \
    -e ASPNETCORE_URLS=http://0.0.0.0:15000 \
    -e MCP_ENABLE_STDIO=false \
    -e FSI_ENABLE_REMOTE_CLIENT=true \
    -e FSI_ENABLE_PROC_SUPERVISOR=true \
    -e FSI_PROC_SUPERVISOR_HOST=$$HOST_IP \
    -e FSI_PROC_SUPERVISOR_PORT=8110 \
    -e FSI_PROC_SUPERVISOR_WEB_HOST=$$HOST_IP \
    -e FSI_PROC_SUPERVISOR_WEB_PORT=6001 \
    -e FSI_PROC_SUPERVISOR_SYSTEM_NAME=proc-system \
    -e FSI_PROC_SUPERVISOR_PATH=akka.tcp://proc-system@$$HOST_IP:8110/user/proc-supervisor \
    -v /path/to/repo-root:/gemini4:ro \
    -v /path/to/devkit-workspace:/workspace \
    fsharp-mcp-devkit:ai-v0.6.2'

ExecStop=/usr/bin/docker stop -t 10 fsharp-mcp-devkit

ExecStopPost=/usr/bin/bash -lc '\
  if /usr/bin/docker ps -aq --filter name=^fsharp-mcp-devkit$$ | grep -q .; then \
    exec /usr/bin/docker rm -f fsharp-mcp-devkit; \
  fi'

[Install]
WantedBy=multi-user.target
```

Notes:

- `/gemini4` is the read-only mount exposing your checked-out source/data tree
- `/workspace` is the writable mount for runtime workspace usage
- the remote `net10` procnode host arguments usually reference `/app/Akka.Proc.Supervisor...` inside the container
- all paths, image tags, and hostnames in the example must be adapted to your environment

## Example: Create a Remote net10 Host

Typical `create_fsi_host` arguments for the out-of-process `net10` path look like:

```text
exec --runtimeconfig /app/Akka.Proc.Supervisor.runtimeconfig.json --depsfile /app/Akka.Proc.Supervisor.deps.json /app/Akka.Proc.Supervisor.dll --mode procnode --systemname fsi-proc --host <HOST_IP> --port 0 --supervisor akka.tcp://proc-system@<HOST_IP>:8110/user/proc-supervisor --procid <HOST_ID>
```

`probeMessage` and `probeIntervalMs` are optional. Leave them empty until basic host/session flow is confirmed.

Again, replace `<HOST_IP>`, `<HOST_ID>`, mount paths, and any example container-local paths with values valid in your own deployment.

## notifications/initialized

If you talk to the server over raw MCP Streamable HTTP:

- call `initialize` first
- then send `notifications/initialized`
- `notifications/initialized` must be a **notification**, so do **not** include `id`

Expected behavior:

- `initialize` returns the session id header
- `notifications/initialized` returns HTTP `202` with an empty body

## If You Do Not Trust the Published NuGet Packages

The repository root contains zipped dependency source snapshots that can be built locally instead of consuming NuGet packages:

- [Akka.FSI.Supervisor.zip](./Akka.FSI.Supervisor.zip)
- [Akka.FSI.Supervisor.Tests.zip](./Akka.FSI.Supervisor.Tests.zip)
- [Akka.Proc.Supervisor.zip](./Akka.Proc.Supervisor.zip)
- [Akka.Proc.Supervisor.Tests.zip](./Akka.Proc.Supervisor.Tests.zip)
- [FAkka.Fsi.Contracts.zip](./FAkka.Fsi.Contracts.zip)

These are the main dependency source bundles relevant to the remote host/session stack.

## Related Documents

For concrete end-to-end instructions:

- [doc/Runbook.md](./doc/Runbook.md)
- [doc/E2EScenarioTest.md](./doc/E2EScenarioTest.md)
- [doc/E2EScenarioTest_gemini.md](./doc/E2EScenarioTest_gemini.md)

For current issue tracking:

- [doc4dev/20260328_issues.md](./doc4dev/20260328_issues.md)
