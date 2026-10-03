# MCP / dependency knowledge

## DEVKIT-DEP52（2026-10-04）

- 本repo current build設定以 `global.json` 與 `Directory.Build.props` 為準；Server SDK10.0.203/patch，Server既有explicitFSharp.Core10.1.400覆蓋其他library的10.1.203。不要為consumer驗證替換SDK或全repo版本。
- `FAkka.FSI.Supervisor [1.571.101.400-win3]`／`FAkka.Proc.Supervisor [1.571.101.400-win16]` 是Server active exactrefs；runtime DLL名稱分別 `Akka.FSI.Supervisor.dll`／`Akka.Proc.Supervisor.dll`，不能用packageID直接假設assemblyname。
- 本輪候選放在SDK10.0.401的 `FSharp/library-packs`；用 `RestoreAdditionalProjectSources` 明傳來源，不建立nuget.config。新C `--artifacts-path` 可隔離本機build output/obj；所有pack/publish flags保持false。
- build/assets/deps與DLLhash只證consumer compile/payload，不能證Proc lease、FSI執行、MCP endpoint或production部署。不要用會重啟Docker的build.host.sh代替dependencybuild。
- Current範圍／actual evidence：`doc/SD.md`、`doc/WBS.md` 的DEVKIT-DEP52、`doc/E2EScenarioTest.md` r1；操作與時序偏差見 `log/20261004/20261004052552.aster_dep52_infra_refs.log`。
