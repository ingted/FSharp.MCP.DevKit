#r "nuget: Argu, 6.2.5"

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography
open System.Diagnostics
open Argu

[<CLIMutable>]
type FileSnapshot =
    { Path: string
      Exists: bool
      LastWriteUtc: string
      Size: int64
      Sha256: string }

[<CLIMutable>]
type NamedFileSnapshot =
    { Name: string
      Snapshot: FileSnapshot }

[<CLIMutable>]
type DirEntrySnapshot =
    { RelativePath: string
      LastWriteUtc: string
      Size: int64
      Sha256: string }

[<CLIMutable>]
type DirectorySnapshot =
    { Name: string
      Path: string
      Exists: bool
      Entries: DirEntrySnapshot list }

[<CLIMutable>]
type LogSnapshot =
    { HasLatestLog: bool
      LatestLog: FileSnapshot
      HasLatestOpLog: bool
      LatestOpLog: FileSnapshot
      LatestLogHasPlan: bool
      LatestLogMissingFields: string list
      LatestLogNameValid: bool
      LatestOpLogNameValid: bool
      LatestOpLogCoversLatestLog: bool
      InvalidLogNames: string list
      InvalidOpLogNames: string list }

[<CLIMutable>]
type GitSnapshot =
    { RepoPath: string
      IsRepo: bool
      Branch: string
      Head: string
      HeadSummary: string
      Dirty: bool
      LocalBranches: string list
      RecentCommits: string list
      BranchNameValid: bool }

[<CLIMutable>]
type Finding =
    { Severity: string
      Code: string
      Message: string }

[<CLIMutable>]
type CheckState =
    { SchemaVersion: int
      SessionId: string
      CreatedUtc: string
      LogDir: string
      OpLogDir: string
      NotesDir: string
      WatchedFiles: NamedFileSnapshot list
      WatchedDirectories: DirectorySnapshot list
      Logs: LogSnapshot
      Git: GitSnapshot
      Findings: Finding list }

type Config =
    { SessionId: string
      LogDir: string
      OpLogDir: string
      NotesDir: string
      RepoPath: string
      RequireExternalTracking: bool
      Watches: (string * string) list }

[<CliPrefix(CliPrefix.DoubleDash)>]
type CheckCliArgs =
    | [<CustomCommandLine("--session-id")>] SessionId of string
    | [<CustomCommandLine("--log-dir")>] LogDir of string
    | [<CustomCommandLine("--op-log-dir")>] OpLogDir of string
    | [<CustomCommandLine("--notes-dir")>] NotesDir of string
    | [<CustomCommandLine("--repo")>] Repo of string
    | [<CustomCommandLine("--sa")>] Sa of string
    | [<CustomCommandLine("--sd")>] Sd of string
    | [<CustomCommandLine("--wbs")>] Wbs of string
    | [<CustomCommandLine("--test")>] Test of string
    | [<CustomCommandLine("--devlog")>] Devlog of string
    | [<CustomCommandLine("--policy")>] Policy of string
    | [<CustomCommandLine("--action")>] Action of string
    | [<CustomCommandLine("--architecture")>] Architecture of string
    | [<CustomCommandLine("--requirement")>] Requirement of string
    | [<CustomCommandLine("--ba")>] Ba of string
    | [<CustomCommandLine("--qa")>] Qa of string
    | [<CustomCommandLine("--regression")>] Regression of string
    | [<CustomCommandLine("--deployment")>] Deployment of string
    | [<CustomCommandLine("--runbook")>] Runbook of string
    | [<CustomCommandLine("--km")>] Km of string
    | [<CustomCommandLine("--watch")>] Watch of string
    | [<CustomCommandLine("--require-external-tracking")>] RequireExternalTracking
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | SessionId _ -> "覆蓋預設的 CODEX_THREAD_ID"
            | LogDir _ -> "log 目錄路徑，必填"
            | OpLogDir _ -> ".op_log 目錄，預設同 --log-dir"
            | NotesDir _ -> "額外檢查 readonly 的 notes 目錄"
            | Repo _ -> "Git repo 路徑，檢查 branch/commit 與 dirty 狀態"
            | Sa _ -> "doc/SA.md"
            | Sd _ -> "doc/SD.md"
            | Wbs _ -> "doc/WBS.md"
            | Test _ -> "doc/Test.md"
            | Devlog _ -> "doc/DevLog.md，會加做 append-only 檢查"
            | Policy _ -> "doc/Policy.md"
            | Action _ -> "doc/Action.md"
            | Architecture _ -> "doc/Architecture.md"
            | Requirement _ -> "doc/Requirement.md"
            | Ba _ -> "doc/BA.md"
            | Qa _ -> "doc/QA.md"
            | Regression _ -> "doc/Regression.md"
            | Deployment _ -> "doc/Deployment.md"
            | Runbook _ -> "doc/Runbook.md"
            | Km _ -> "MCP.KM.md"
            | Watch _ -> "自訂額外檢查文件，格式 name=path，可重複"
            | RequireExternalTracking -> "強制檢查 Jira/GitHub/Confluence 回鏈"

let emptyFileSnapshot path =
    { Path = path
      Exists = false
      LastWriteUtc = ""
      Size = -1L
      Sha256 = "" }

let emptyGitSnapshot repoPath =
    { RepoPath = repoPath
      IsRepo = false
      Branch = ""
      Head = ""
      HeadSummary = ""
      Dirty = false
      LocalBranches = []
      RecentCommits = []
      BranchNameValid = false }

let toIsoUtc (dt: DateTime) =
    dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

let normalizePath (path: string) =
    Path.GetFullPath(path)

let sha256Hex (bytes: byte array) =
    bytes |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""

let computeSha256ForStream (stream: Stream) (maxBytes: int64 option) =
    use sha = SHA256.Create()
    let buffer = Array.zeroCreate<byte> 81920
    let mutable remaining = defaultArg maxBytes Int64.MaxValue
    let mutable finished = false

    while not finished do
        let requested =
            if remaining = Int64.MaxValue then
                buffer.Length
            else
                int (min remaining (int64 buffer.Length))

        if requested <= 0 then
            finished <- true
        else
            let read = stream.Read(buffer, 0, requested)
            if read <= 0 then
                finished <- true
            else
                sha.TransformBlock(buffer, 0, read, null, 0) |> ignore
                if remaining <> Int64.MaxValue then
                    remaining <- remaining - int64 read

    sha.TransformFinalBlock(Array.empty, 0, 0) |> ignore
    sha256Hex sha.Hash

let computeFileSha256 (path: string) =
    use stream = File.OpenRead(path)
    computeSha256ForStream stream None

let computeFilePrefixSha256 (path: string) (byteCount: int64) =
    use stream = File.OpenRead(path)
    computeSha256ForStream stream (Some byteCount)

let snapshotFile (path: string) =
    let fullPath = normalizePath path
    if File.Exists(fullPath) then
        let fileInfo = FileInfo(fullPath)
        { Path = fullPath
          Exists = true
          LastWriteUtc = toIsoUtc fileInfo.LastWriteTimeUtc
          Size = fileInfo.Length
          Sha256 = computeFileSha256 fullPath }
    else
        emptyFileSnapshot fullPath

let snapshotDirectory (name: string) (path: string) =
    let fullPath = normalizePath path
    if Directory.Exists(fullPath) then
        let entries =
            Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
            |> Seq.sort
            |> Seq.map (fun filePath ->
                let fileInfo = FileInfo(filePath)
                { RelativePath = Path.GetRelativePath(fullPath, filePath).Replace('\\', '/')
                  LastWriteUtc = toIsoUtc fileInfo.LastWriteTimeUtc
                  Size = fileInfo.Length
                  Sha256 = computeFileSha256 filePath })
            |> Seq.toList

        { Name = name
          Path = fullPath
          Exists = true
          Entries = entries }
    else
        { Name = name
          Path = fullPath
          Exists = false
          Entries = [] }

let tryReadFile (path: string) =
    if File.Exists(path) then
        File.ReadAllText(path)
    else
        ""

let containsAny (content: string) (keywords: string list) =
    keywords |> List.exists content.Contains

let latestFileByPattern (path: string) (pattern: string) =
    if Directory.Exists(path) then
        Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)
        |> Seq.map (fun filePath -> FileInfo(filePath))
        |> Seq.sortByDescending (fun fileInfo -> fileInfo.LastWriteTimeUtc)
        |> Seq.tryHead
        |> Option.map (fun fileInfo -> fileInfo.FullName)
    else
        None

let logFileRegex = Regex(@"^\d{14}\.[^.]{1,50}\.\d{5}\.\d{5}\.log$", RegexOptions.Compiled)
let opLogFileRegex = Regex(@"^\d{14}\.[^.]{1,50}\.op_log$", RegexOptions.Compiled)
let externalRefRegex =
    Regex(@"(\b[A-Z][A-Z0-9]+-\d+\b)|(#\d+)|((github|atlassian)\.com)|(/pull/)|(/issues/)", RegexOptions.Compiled)
let branchNameRegex = Regex(@"^\d{8}_\d{3}\.[A-Za-z0-9._-]+$", RegexOptions.Compiled)

let isDefaultBranchName (branch: string) =
    let value = branch.Trim()
    value = "main"
    || value = "master"
    || value = "develop"
    || value = "dev"
    || value = "trunk"
    || value.StartsWith("release/", StringComparison.OrdinalIgnoreCase)
    || value.StartsWith("hotfix/", StringComparison.OrdinalIgnoreCase)

let validateLogContent (content: string) =
    let requiredFields =
        [ ("背景", [ "背景" ])
          ("目標", [ "目標" ])
          ("計畫步驟", [ "計畫步驟"; "planned steps" ])
          ("執行命令/參數", [ "執行命令"; "參數" ])
          ("結果", [ "結果" ])
          ("根因判讀", [ "根因判讀" ])
          ("下一步", [ "下一步" ])
          ("關聯文件/工單/PR", [ "關聯文件"; "工單"; "PR" ])
          ("prompt/上下文摘要", [ "prompt"; "上下文"; "摘要" ]) ]

    let missing =
        requiredFields
        |> List.choose (fun (name, keywords) ->
            if containsAny content keywords then None else Some name)

    let hasPlan = containsAny content [ "計畫步驟"; "planned steps"; "plan" ]
    hasPlan, missing

let runProcess (workingDir: string) (fileName: string) (arguments: string) =
    let psi = ProcessStartInfo()
    psi.FileName <- fileName
    psi.Arguments <- arguments
    psi.WorkingDirectory <- workingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    use proc = new Process()
    proc.StartInfo <- psi
    proc.Start() |> ignore
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, stdout.Trim(), stderr.Trim()

let gitOutput (repoPath: string) (args: string) =
    runProcess "/" "git" ("-C \"" + repoPath + "\" " + args)

let captureGitSnapshot (repoPath: string) =
    let fullPath = normalizePath repoPath
    let baseSnapshot = emptyGitSnapshot fullPath
    let exitCode, stdout, _ = gitOutput fullPath "rev-parse --is-inside-work-tree"

    if exitCode <> 0 || stdout <> "true" then
        baseSnapshot
    else
        let _, branch, _ = gitOutput fullPath "rev-parse --abbrev-ref HEAD"
        let _, head, _ = gitOutput fullPath "rev-parse HEAD"
        let _, headSummary, _ = gitOutput fullPath "log -1 --pretty=format:%H%x20%cI%x20%s"
        let _, dirtyOutput, _ = gitOutput fullPath "status --porcelain"
        let _, branchesOutput, _ = gitOutput fullPath "for-each-ref --format=%(refname:short) refs/heads"
        let _, commitsOutput, _ = gitOutput fullPath "log -5 --pretty=format:%H%x20%cI%x20%s"

        let localBranches =
            branchesOutput.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun value -> value.Trim())
            |> Array.filter (fun value -> value <> "")
            |> Array.toList

        let recentCommits =
            commitsOutput.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun value -> value.Trim())
            |> Array.filter (fun value -> value <> "")
            |> Array.toList

        let branchNameValid =
            branch = "HEAD" || isDefaultBranchName branch || branchNameRegex.IsMatch(branch)

        { RepoPath = fullPath
          IsRepo = true
          Branch = branch
          Head = head
          HeadSummary = headSummary
          Dirty = not (String.IsNullOrWhiteSpace dirtyOutput)
          LocalBranches = localBranches
          RecentCommits = recentCommits
          BranchNameValid = branchNameValid }

let writeFinding severity code message findings =
    { Severity = severity; Code = code; Message = message } :: findings

let tryLoadState (path: string) =
    if File.Exists(path) then
        try
            let json = File.ReadAllText(path)
            let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
            Some (JsonSerializer.Deserialize<CheckState>(json, options))
        with _ ->
            None
    else
        None

let saveState (path: string) (state: CheckState) =
    let options = JsonSerializerOptions(WriteIndented = true)
    let json = JsonSerializer.Serialize(state, options)
    File.WriteAllText(path, json, Encoding.UTF8)

let tryFindWatchedFile name (state: CheckState) =
    state.WatchedFiles
    |> List.tryFind (fun item -> String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))

let tryFindDirectorySnapshot name (state: CheckState) =
    state.WatchedDirectories
    |> List.tryFind (fun item -> String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))

let compareReadonlyDirectory (name: string) (previousState: CheckState) (currentState: CheckState) findings =
    match tryFindDirectorySnapshot name previousState, tryFindDirectorySnapshot name currentState with
    | Some previousDir, Some currentDir when previousDir.Exists && currentDir.Exists ->
        let currentMap =
            currentDir.Entries
            |> Seq.map (fun entry -> entry.RelativePath, entry)
            |> Map.ofSeq

        let mutable updatedFindings = findings

        for previousEntry in previousDir.Entries do
            match currentMap.TryFind(previousEntry.RelativePath) with
            | None ->
                updatedFindings <-
                    writeFinding
                        "FAIL"
                        (name + "_readonly_delete")
                        (sprintf "%s 目錄中的既有檔案被刪除：%s" name previousEntry.RelativePath)
                        updatedFindings
            | Some currentEntry ->
                if currentEntry.Size <> previousEntry.Size || currentEntry.Sha256 <> previousEntry.Sha256 then
                    updatedFindings <-
                        writeFinding
                            "FAIL"
                            (name + "_readonly_modify")
                            (sprintf "%s 目錄中的既有檔案被修改：%s" name previousEntry.RelativePath)
                            updatedFindings

        updatedFindings
    | _ -> findings

let compareAppendOnlyFile (name: string) (previousState: CheckState) (currentState: CheckState) findings =
    match tryFindWatchedFile name previousState, tryFindWatchedFile name currentState with
    | Some previousFile, Some currentFile when previousFile.Snapshot.Exists && currentFile.Snapshot.Exists ->
        let prevSnap = previousFile.Snapshot
        let currSnap = currentFile.Snapshot

        if prevSnap.Sha256 = currSnap.Sha256 then
            findings
        elif currSnap.Size < prevSnap.Size then
            writeFinding
                "FAIL"
                (name + "_append_only_shrink")
                (sprintf "%s 變小，疑似違反 append-only：%s" name currSnap.Path)
                findings
        else
            let prefixHash = computeFilePrefixSha256 currSnap.Path prevSnap.Size
            if prefixHash = prevSnap.Sha256 then
                writeFinding
                    "PASS"
                    (name + "_append_only")
                    (sprintf "%s 以 append-only 方式追加" name)
                    findings
            else
                writeFinding
                    "FAIL"
                    (name + "_append_only_overwrite")
                    (sprintf "%s 的既有內容被改寫，違反 append-only：%s" name currSnap.Path)
                    findings
    | _ -> findings

let describeFileChange (current: NamedFileSnapshot) (previousState: CheckState option) findings =
    let snapshot = current.Snapshot
    let initialFindings =
        if snapshot.Exists then
            writeFinding "PASS" (current.Name + "_exists") (sprintf "%s 存在：%s" current.Name snapshot.Path) findings
        else
            writeFinding "FAIL" (current.Name + "_missing") (sprintf "%s 不存在：%s" current.Name snapshot.Path) findings

    match previousState with
    | Some previous ->
        match tryFindWatchedFile current.Name previous with
        | Some previousFile when previousFile.Snapshot.Exists && snapshot.Exists ->
            if previousFile.Snapshot.Sha256 = snapshot.Sha256 then
                writeFinding "WARN" (current.Name + "_unchanged") (sprintf "%s 自上次檢查後未更新" current.Name) initialFindings
            else
                writeFinding
                    "PASS"
                    (current.Name + "_changed")
                    (sprintf "%s 自上次檢查後已更新，mtime=%s" current.Name snapshot.LastWriteUtc)
                    initialFindings
        | Some previousFile when previousFile.Snapshot.Exists && not snapshot.Exists ->
            writeFinding "FAIL" (current.Name + "_deleted") (sprintf "%s 自上次檢查後消失" current.Name) initialFindings
        | _ ->
            initialFindings
    | None -> initialFindings

let checkExternalTracking requireExternalTracking (watchedFiles: NamedFileSnapshot list) findings =
    if not requireExternalTracking then
        findings
    else
        let anyReference =
            watchedFiles
            |> List.filter (fun item -> item.Snapshot.Exists)
            |> List.exists (fun item ->
                let content = tryReadFile item.Snapshot.Path
                externalRefRegex.IsMatch(content))

        if anyReference then
            writeFinding "PASS" "external_tracking" "至少一份本機文件含有 Jira/GitHub/Confluence 追溯資訊" findings
        else
            writeFinding "FAIL" "external_tracking_missing" "找不到 Jira/GitHub/Confluence 追溯資訊" findings

let parseWatchPair (value: string) =
    let index = value.IndexOf('=')
    if index <= 0 || index = value.Length - 1 then
        failwith " --watch 參數格式必須為 name=/absolute/or/relative/path "
    value.Substring(0, index).Trim(), value.Substring(index + 1).Trim()

let dedupeWatches (watches: (string * string) list) =
    watches
    |> List.rev
    |> Seq.distinctBy (fun (name, _) -> name.ToLowerInvariant())
    |> Seq.rev
    |> Seq.toList

let args = fsi.CommandLineArgs |> Array.skip 1

let defaultSessionId =
    match Environment.GetEnvironmentVariable("CODEX_THREAD_ID") with
    | null
    | "" -> "unknown-session"
    | value -> value

let defaultConfig =
    { SessionId = defaultSessionId
      LogDir = ""
      OpLogDir = ""
      NotesDir = ""
      RepoPath = ""
      RequireExternalTracking = false
      Watches = [] }

let parser = ArgumentParser.Create<CheckCliArgs>(programName = "dotnet fsi --exec /workspace/home/codex/check.fsx --")
if args |> Array.exists (fun value -> value = "--help" || value = "-h") then
    printfn "%s" (parser.PrintUsage())
    Environment.Exit(0)

let results = parser.Parse(args, raiseOnUsage = true)

let addWatch name path config =
    { config with Watches = (name, path) :: config.Watches }

let parsedConfig =
    let withNamedWatch watchName pathOption config =
        match pathOption with
        | Some path -> addWatch watchName path config
        | None -> config

    let initial =
        { defaultConfig with
            SessionId = results.GetResult(<@ SessionId @>, defaultValue = defaultSessionId)
            LogDir = results.GetResult(<@ LogDir @>, defaultValue = "")
            OpLogDir = results.GetResult(<@ OpLogDir @>, defaultValue = "")
            NotesDir = results.GetResult(<@ NotesDir @>, defaultValue = "")
            RepoPath = results.GetResult(<@ Repo @>, defaultValue = "")
            RequireExternalTracking = results.Contains(<@ RequireExternalTracking @>) }

    initial
    |> withNamedWatch "sa" (results.TryGetResult(<@ Sa @>))
    |> withNamedWatch "sd" (results.TryGetResult(<@ Sd @>))
    |> withNamedWatch "wbs" (results.TryGetResult(<@ Wbs @>))
    |> withNamedWatch "test" (results.TryGetResult(<@ Test @>))
    |> withNamedWatch "devlog" (results.TryGetResult(<@ Devlog @>))
    |> withNamedWatch "policy" (results.TryGetResult(<@ Policy @>))
    |> withNamedWatch "action" (results.TryGetResult(<@ Action @>))
    |> withNamedWatch "architecture" (results.TryGetResult(<@ Architecture @>))
    |> withNamedWatch "requirement" (results.TryGetResult(<@ Requirement @>))
    |> withNamedWatch "ba" (results.TryGetResult(<@ Ba @>))
    |> withNamedWatch "qa" (results.TryGetResult(<@ Qa @>))
    |> withNamedWatch "regression" (results.TryGetResult(<@ Regression @>))
    |> withNamedWatch "deployment" (results.TryGetResult(<@ Deployment @>))
    |> withNamedWatch "runbook" (results.TryGetResult(<@ Runbook @>))
    |> withNamedWatch "km" (results.TryGetResult(<@ Km @>))
    |> fun cfg ->
        results.GetResults(<@ Watch @>)
        |> List.fold (fun acc value ->
            let name, path = parseWatchPair value
            addWatch name path acc) cfg

if String.IsNullOrWhiteSpace parsedConfig.LogDir then
    printfn "%s" (parser.PrintUsage())
    failwith "必須提供 --log-dir"

let config =
    { parsedConfig with
        LogDir = normalizePath parsedConfig.LogDir
        OpLogDir =
            if String.IsNullOrWhiteSpace parsedConfig.OpLogDir then
                normalizePath parsedConfig.LogDir
            else
                normalizePath parsedConfig.OpLogDir
        NotesDir =
            if String.IsNullOrWhiteSpace parsedConfig.NotesDir then
                ""
            else
                normalizePath parsedConfig.NotesDir
        RepoPath =
            if String.IsNullOrWhiteSpace parsedConfig.RepoPath then
                ""
            else
                normalizePath parsedConfig.RepoPath
        Watches = dedupeWatches parsedConfig.Watches }

let stateFilePath = "/workspace/home/codex/" + config.SessionId + ".state.json"
let previousState = tryLoadState stateFilePath

let watchedFiles =
    config.Watches
    |> List.map (fun (name, path) ->
        { Name = name
          Snapshot = snapshotFile path })

let watchedDirectories =
    [ snapshotDirectory "log" config.LogDir
      if config.NotesDir <> "" then
          snapshotDirectory "notes" config.NotesDir ]

let latestLogPath = latestFileByPattern config.LogDir "*.log"
let latestOpLogPath = latestFileByPattern config.OpLogDir "*.op_log"

let latestLogSnapshot =
    latestLogPath
    |> Option.map snapshotFile
    |> Option.defaultValue (emptyFileSnapshot "")

let latestOpLogSnapshot =
    latestOpLogPath
    |> Option.map snapshotFile
    |> Option.defaultValue (emptyFileSnapshot "")

let latestLogContent =
    if latestLogSnapshot.Exists then
        tryReadFile latestLogSnapshot.Path
    else
        ""

let latestLogHasPlan, latestLogMissingFields =
    if latestLogSnapshot.Exists then
        validateLogContent latestLogContent
    else
        false, [ "背景"; "目標"; "計畫步驟"; "執行命令/參數"; "結果"; "根因判讀"; "下一步"; "關聯文件/工單/PR"; "prompt/上下文摘要" ]

let invalidLogNames =
    if Directory.Exists(config.LogDir) then
        Directory.EnumerateFiles(config.LogDir, "*.log", SearchOption.AllDirectories)
        |> Seq.filter (fun filePath -> not (logFileRegex.IsMatch(Path.GetFileName(filePath))))
        |> Seq.map Path.GetFileName
        |> Seq.toList
    else
        [ "(log dir missing)" ]

let invalidOpLogNames =
    if Directory.Exists(config.OpLogDir) then
        Directory.EnumerateFiles(config.OpLogDir, "*.op_log", SearchOption.AllDirectories)
        |> Seq.filter (fun filePath -> not (opLogFileRegex.IsMatch(Path.GetFileName(filePath))))
        |> Seq.map Path.GetFileName
        |> Seq.toList
    else
        [ "(op_log dir missing)" ]

let latestLogNameValid =
    latestLogSnapshot.Exists
    && logFileRegex.IsMatch(Path.GetFileName(latestLogSnapshot.Path))

let latestOpLogNameValid =
    latestOpLogSnapshot.Exists
    && opLogFileRegex.IsMatch(Path.GetFileName(latestOpLogSnapshot.Path))

let latestOpLogCoversLatestLog =
    latestLogSnapshot.Exists
    && latestOpLogSnapshot.Exists
    && latestOpLogSnapshot.LastWriteUtc >= latestLogSnapshot.LastWriteUtc

let logState =
    { HasLatestLog = latestLogSnapshot.Exists
      LatestLog = latestLogSnapshot
      HasLatestOpLog = latestOpLogSnapshot.Exists
      LatestOpLog = latestOpLogSnapshot
      LatestLogHasPlan = latestLogHasPlan
      LatestLogMissingFields = latestLogMissingFields
      LatestLogNameValid = latestLogNameValid
      LatestOpLogNameValid = latestOpLogNameValid
      LatestOpLogCoversLatestLog = latestOpLogCoversLatestLog
      InvalidLogNames = invalidLogNames
      InvalidOpLogNames = invalidOpLogNames }

let gitState =
    if config.RepoPath <> "" then
        captureGitSnapshot config.RepoPath
    else
        emptyGitSnapshot ""

let mutable findings: Finding list = []

if Path.GetFileName(config.LogDir) <> "log" then
    findings <- writeFinding "WARN" "log_dir_name" (sprintf "--log-dir 不是名為 log 的目錄：%s" config.LogDir) findings

if not (Directory.Exists(config.LogDir)) then
    findings <- writeFinding "FAIL" "log_dir_missing" (sprintf "log 目錄不存在：%s" config.LogDir) findings
else
    findings <- writeFinding "PASS" "log_dir_exists" (sprintf "log 目錄存在：%s" config.LogDir) findings

if config.OpLogDir = config.LogDir then
    findings <- writeFinding "PASS" "op_log_dir_default" ".op_log 檢查目錄使用 --log-dir" findings
elif Directory.Exists(config.OpLogDir) then
    findings <- writeFinding "PASS" "op_log_dir_exists" (sprintf ".op_log 目錄存在：%s" config.OpLogDir) findings
else
    findings <- writeFinding "FAIL" "op_log_dir_missing" (sprintf ".op_log 目錄不存在：%s" config.OpLogDir) findings

if logState.HasLatestLog then
    findings <- writeFinding "PASS" "latest_log_found" (sprintf "最新 log：%s @ %s" logState.LatestLog.Path logState.LatestLog.LastWriteUtc) findings
else
    findings <- writeFinding "FAIL" "latest_log_missing" "找不到任何 .log 檔" findings

if logState.HasLatestOpLog then
    findings <- writeFinding "PASS" "latest_op_log_found" (sprintf "最新 op_log：%s @ %s" logState.LatestOpLog.Path logState.LatestOpLog.LastWriteUtc) findings
else
    findings <- writeFinding "FAIL" "latest_op_log_missing" "找不到任何 .op_log 檔" findings

if logState.HasLatestLog then
    if logState.LatestLogNameValid then
        findings <- writeFinding "PASS" "latest_log_name" "最新 log 檔名符合規範" findings
    else
        findings <- writeFinding "FAIL" "latest_log_name" "最新 log 檔名不符合 YYYYMMDDHHmmss.<摘要>.<主編號>.<子編號>.log" findings

if logState.HasLatestOpLog then
    if logState.LatestOpLogNameValid then
        findings <- writeFinding "PASS" "latest_op_log_name" "最新 op_log 檔名符合規範" findings
    else
        findings <- writeFinding "FAIL" "latest_op_log_name" "最新 op_log 檔名不符合 YYYYMMDDHHmmss.<摘要>.op_log" findings

if logState.InvalidLogNames.Length > 0 then
    findings <-
        writeFinding
            "WARN"
            "invalid_log_names"
            (sprintf "發現 %d 個不符合命名規範的 .log：%s" logState.InvalidLogNames.Length (String.concat ", " (logState.InvalidLogNames |> List.truncate 5)))
            findings

if logState.InvalidOpLogNames.Length > 0 then
    findings <-
        writeFinding
            "WARN"
            "invalid_op_log_names"
            (sprintf "發現 %d 個不符合命名規範的 .op_log：%s" logState.InvalidOpLogNames.Length (String.concat ", " (logState.InvalidOpLogNames |> List.truncate 5)))
            findings

if logState.HasLatestLog then
    if logState.LatestLogHasPlan then
        findings <- writeFinding "PASS" "latest_log_has_plan" "最新 log 內含 planned steps/計畫步驟" findings
    else
        findings <- writeFinding "FAIL" "latest_log_has_plan" "最新 log 找不到 planned steps/計畫步驟" findings

    if logState.LatestLogMissingFields.IsEmpty then
        findings <- writeFinding "PASS" "latest_log_required_fields" "最新 log 已覆蓋最低欄位" findings
    else
        findings <-
            writeFinding
                "WARN"
                "latest_log_required_fields"
                (sprintf "最新 log 缺少欄位：%s" (String.concat ", " logState.LatestLogMissingFields))
                findings

if logState.HasLatestLog && logState.HasLatestOpLog then
    if logState.LatestOpLogCoversLatestLog then
        findings <- writeFinding "PASS" "op_log_after_log" "最新 op_log 時間不早於最新 log，可視為有收尾操作證據" findings
    else
        findings <- writeFinding "FAIL" "op_log_after_log" "最新 op_log 時間早於最新 log，疑似未完成操作收尾" findings

for watchedFile in watchedFiles do
    findings <- describeFileChange watchedFile previousState findings

match previousState with
| Some previous ->
    findings <- compareReadonlyDirectory "log" previous { SchemaVersion = 0; SessionId = ""; CreatedUtc = ""; LogDir = ""; OpLogDir = ""; NotesDir = ""; WatchedFiles = watchedFiles; WatchedDirectories = watchedDirectories; Logs = logState; Git = gitState; Findings = [] } findings
    if config.NotesDir <> "" then
        findings <- compareReadonlyDirectory "notes" previous { SchemaVersion = 0; SessionId = ""; CreatedUtc = ""; LogDir = ""; OpLogDir = ""; NotesDir = ""; WatchedFiles = watchedFiles; WatchedDirectories = watchedDirectories; Logs = logState; Git = gitState; Findings = [] } findings

    match tryFindWatchedFile "devlog" previous, watchedFiles |> List.tryFind (fun item -> item.Name.Equals("devlog", StringComparison.OrdinalIgnoreCase)) with
    | Some _, Some _ ->
        findings <- compareAppendOnlyFile "devlog" previous { SchemaVersion = 0; SessionId = ""; CreatedUtc = ""; LogDir = ""; OpLogDir = ""; NotesDir = ""; WatchedFiles = watchedFiles; WatchedDirectories = watchedDirectories; Logs = logState; Git = gitState; Findings = [] } findings
    | _ -> ()

    if previous.Logs.HasLatestLog then
        if previous.Logs.LatestLog.Sha256 = logState.LatestLog.Sha256 && previous.Logs.LatestLog.Path = logState.LatestLog.Path then
            findings <- writeFinding "FAIL" "latest_log_not_advanced" "與上次檢查相比，最新 log 沒有前進" findings
        else
            findings <- writeFinding "PASS" "latest_log_advanced" "與上次檢查相比，最新 log 已前進" findings

    if previous.Logs.HasLatestOpLog then
        if previous.Logs.LatestOpLog.Sha256 = logState.LatestOpLog.Sha256 && previous.Logs.LatestOpLog.Path = logState.LatestOpLog.Path then
            findings <- writeFinding "FAIL" "latest_op_log_not_advanced" "與上次檢查相比，最新 op_log 沒有前進" findings
        else
            findings <- writeFinding "PASS" "latest_op_log_advanced" "與上次檢查相比，最新 op_log 已前進" findings

    if gitState.IsRepo && previous.Git.IsRepo then
        if gitState.Head <> "" && gitState.Head <> previous.Git.Head then
            findings <- writeFinding "PASS" "git_new_commit" (sprintf "HEAD 已更新：%s" gitState.HeadSummary) findings
        elif gitState.Dirty then
            findings <- writeFinding "WARN" "git_dirty_no_commit" "repo 仍有未提交變更" findings
        else
            findings <- writeFinding "WARN" "git_no_new_commit" "與上次檢查相比沒有新 commit" findings

        let newBranches =
            gitState.LocalBranches
            |> List.filter (fun branch -> not (previous.Git.LocalBranches |> List.contains branch))

        if newBranches.Length > 0 then
            findings <- writeFinding "PASS" "git_new_branch" (sprintf "發現新 branch：%s" (String.concat ", " newBranches)) findings

        if previous.Git.Branch <> "" && gitState.Branch <> previous.Git.Branch then
            findings <- writeFinding "PASS" "git_branch_switched" (sprintf "目前 branch 已切換：%s -> %s" previous.Git.Branch gitState.Branch) findings
    elif gitState.IsRepo then
        findings <- writeFinding "PASS" "git_baseline" "第一次記錄 git 基線" findings
| None ->
    findings <- writeFinding "PASS" "baseline_created" "第一次檢查，已建立 baseline state" findings

if gitState.IsRepo then
    findings <- writeFinding "PASS" "git_repo" (sprintf "Git repo：%s" gitState.RepoPath) findings
    findings <- writeFinding "PASS" "git_branch" (sprintf "目前 branch：%s" gitState.Branch) findings
    findings <- writeFinding "PASS" "git_head" (sprintf "目前 HEAD：%s" gitState.HeadSummary) findings

    if gitState.Dirty then
        findings <- writeFinding "WARN" "git_dirty" "repo 有未提交變更" findings
    else
        findings <- writeFinding "PASS" "git_clean" "repo 工作樹乾淨" findings

    if gitState.BranchNameValid then
        findings <- writeFinding "PASS" "git_branch_name" "目前 branch 命名可接受" findings
    else
        findings <- writeFinding "WARN" "git_branch_name" "目前 branch 命名不符合 YYYYMMDD_001.topic 規範" findings
elif config.RepoPath <> "" then
    findings <- writeFinding "FAIL" "git_not_repo" (sprintf "指定的 --repo 不是 git repo：%s" config.RepoPath) findings

findings <- checkExternalTracking config.RequireExternalTracking watchedFiles findings

match previousState with
| Some previous ->
    let changedNames =
        watchedFiles
        |> List.choose (fun current ->
            match tryFindWatchedFile current.Name previous with
            | Some previousFile when previousFile.Snapshot.Sha256 <> current.Snapshot.Sha256 -> Some (current.Name.ToLowerInvariant())
            | None when current.Snapshot.Exists -> Some (current.Name.ToLowerInvariant())
            | _ -> None)
        |> Set.ofList

    let policyChanged = changedNames.Contains("policy")
    let actionChanged = changedNames.Contains("action")

    if policyChanged || actionChanged then
        let linkedDocs = [ "sa"; "sd"; "wbs"; "test"; "devlog" ]
        let anyLinkedDocChanged = linkedDocs |> List.exists changedNames.Contains
        if anyLinkedDocChanged then
            findings <- writeFinding "PASS" "policy_action_linked_updates" "Policy/Action 變動後，關聯 SA/SD/WBS/Test/DevLog 也有更新" findings
        else
            findings <- writeFinding "FAIL" "policy_action_linked_updates" "Policy/Action 已變動，但 SA/SD/WBS/Test/DevLog 沒有看到同步更新" findings
| None -> ()

let orderedFindings =
    findings
    |> List.rev

let currentState =
    { SchemaVersion = 1
      SessionId = config.SessionId
      CreatedUtc = toIsoUtc DateTime.UtcNow
      LogDir = config.LogDir
      OpLogDir = config.OpLogDir
      NotesDir = config.NotesDir
      WatchedFiles = watchedFiles
      WatchedDirectories = watchedDirectories
      Logs = logState
      Git = gitState
      Findings = orderedFindings }

Directory.CreateDirectory("/workspace/home/codex") |> ignore
saveState stateFilePath currentState

let failCount = orderedFindings |> List.filter (fun item -> item.Severity = "FAIL") |> List.length
let warnCount = orderedFindings |> List.filter (fun item -> item.Severity = "WARN") |> List.length
let passCount = orderedFindings |> List.filter (fun item -> item.Severity = "PASS") |> List.length

printfn "SessionId   : %s" config.SessionId
printfn "State file  : %s" stateFilePath
printfn "Summary     : PASS=%d WARN=%d FAIL=%d" passCount warnCount failCount
printfn ""

for item in orderedFindings do
    printfn "[%s] %s - %s" item.Severity item.Code item.Message

if failCount > 0 then
    Environment.ExitCode <- 1
