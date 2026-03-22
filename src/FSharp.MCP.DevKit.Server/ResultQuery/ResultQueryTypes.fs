namespace FSharp.MCP.DevKit.Server.ResultQuery

type ResultQueryLanguage =
    | BuiltIn
    | FSharpCode

type ResultQueryKind =
    | Filter
    | Map
    | Exists
    | ForAll
    | Zip
    | Diff
    | GroupBy

type ResultMaterialization =
    | NoMaterialization
    | SyntheticResult

type ResultQueryRequest =
    { QueryId: string
      AgentId: string
      PrimaryResultIds: string list
      SecondaryResultIds: string list
      Language: ResultQueryLanguage
      Kind: ResultQueryKind
      QueryText: string
      Materialization: ResultMaterialization }

type ResultQueryResponse =
    { QueryId: string
      IsSuccess: bool
      Output: string
      Errors: string
      ProducedResultIds: string list
      MaterializedJson: string option }
