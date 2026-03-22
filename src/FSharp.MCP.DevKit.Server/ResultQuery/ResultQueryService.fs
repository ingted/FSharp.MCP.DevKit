namespace FSharp.MCP.DevKit.Server.ResultQuery

open System
open System.Text.Json
open FSharp.MCP.DevKit.Core

type private ResultSummary =
    { ResultId: string
      RequestId: string
      AgentId: string
      BackendKind: string
      HostId: string
      SessionId: string
      OperationKind: string
      IsSuccess: bool
      Value: string option
      Output: string
      Errors: string
      RawErrorType: string option }

type private ResultZipRow =
    { Index: int
      LeftResultId: string option
      RightResultId: string option
      LeftValue: string option
      RightValue: string option
      LeftIsSuccess: bool option
      RightIsSuccess: bool option
      AreEqual: bool option }

type private ResultGroupBucket =
    { Key: string
      ResultIds: string list }

type ResultQueryService() =

    let serialize value = JsonSerializer.Serialize(value)

    let trySerializeObject (value: obj option) =
        value
        |> Option.map (fun resolved ->
            if isNull resolved then
                "null"
            else
                JsonSerializer.Serialize(resolved, resolved.GetType()))

    let withQuerySession (work: FsiService -> 'T) =
        let config =
            { FsiConfig.defaultConfig with
                CaptureTimings = true }

        let service = new FsiService(config)

        try
            service.Start()
            work service
        finally
            service.Stop()

    let requireSuccess (operationName: string) (result: FsiResult) =
        if not result.IsSuccess then
            let details =
                [ if not (String.IsNullOrWhiteSpace result.Errors) then
                      yield result.Errors
                  let diagnosticMessages =
                      result.Diagnostics
                      |> Array.map (fun diagnostic -> diagnostic.Message)
                      |> Array.filter (fun message -> not (String.IsNullOrWhiteSpace message))

                  if diagnosticMessages.Length > 0 then
                      yield String.Join(Environment.NewLine, diagnosticMessages) ]
                |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                |> function
                    | [] -> $"'{operationName}' failed without error details."
                    | values -> String.Join(Environment.NewLine, values)

            invalidOp $"FSharpCode query setup step '{operationName}' failed: {details}"

    let normalizeQueryExpression (queryText: string) =
        let trimmed = queryText.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            invalidOp "FSharpCode query requires a non-empty queryText."
        elif trimmed.StartsWith("fun ", StringComparison.Ordinal)
             || trimmed.StartsWith("(fun ", StringComparison.Ordinal) then
            $"({trimmed}) records1 records2"
        else
            trimmed

    let runFSharpCodeQuery (request: ResultQueryRequest) (primaryRecords: FsiExecutionRecord list) (secondaryRecords: FsiExecutionRecord list) =
        let queryExpression = normalizeQueryExpression request.QueryText

        withQuerySession (fun service ->
            requireSuccess "reference-core" (service.ReferenceAssembly(typeof<FsiExecutionRecord>.Assembly.Location))
            requireSuccess "open-core" (service.ExecuteInteraction("open System\nopen FSharp.MCP.DevKit.Core"))
            requireSuccess "bind-records1" (service.AddBoundValue("records1", box primaryRecords))
            requireSuccess "bind-records2" (service.AddBoundValue("records2", box secondaryRecords))
            requireSuccess "bind-primaryRecords" (service.AddBoundValue("primaryRecords", box primaryRecords))
            requireSuccess "bind-secondaryRecords" (service.AddBoundValue("secondaryRecords", box secondaryRecords))

            let evaluation = service.EvaluateExpressionObject(queryExpression)
            let output =
                evaluation.Result.Value
                |> Option.orElseWith (fun () ->
                    if String.IsNullOrWhiteSpace evaluation.Result.Output then
                        None
                    else
                        Some evaluation.Result.Output)
                |> Option.defaultValue ""

            if evaluation.Result.IsSuccess then
                { QueryId = request.QueryId
                  IsSuccess = true
                  Output = output
                  Errors = evaluation.Result.Errors
                  ProducedResultIds = []
                  MaterializedJson =
                    trySerializeObject evaluation.ReflectionValue
                    |> Option.orElseWith (fun () ->
                        evaluation.Result.Value
                        |> Option.map JsonSerializer.Serialize) }
            else
                { QueryId = request.QueryId
                  IsSuccess = false
                  Output = output
                  Errors = evaluation.Result.Errors
                  ProducedResultIds = []
                  MaterializedJson = None })

    let preferredValue (record: FsiExecutionRecord) =
        record.Result.Value
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Option.defaultValue record.Result.Output

    let toSummary (record: FsiExecutionRecord) =
        { ResultId = record.ResultId
          RequestId = record.RequestId
          AgentId = record.AgentId
          BackendKind = string record.BackendKind
          HostId = record.HostId
          SessionId = record.SessionId
          OperationKind = string record.OperationKind
          IsSuccess = record.Result.IsSuccess
          Value = record.Result.Value
          Output = record.Result.Output
          Errors = record.Result.Errors
          RawErrorType = record.RawErrorType }

    let projectionValue (fieldName: string) (record: FsiExecutionRecord) =
        match (fieldName |> string |> fun value -> value.Trim().ToLowerInvariant()) with
        | ""
        | "value" -> preferredValue record
        | "resultid" -> record.ResultId
        | "requestid" -> record.RequestId
        | "agentid" -> record.AgentId
        | "backendkind" -> string record.BackendKind
        | "hostid" -> record.HostId
        | "sessionid" -> record.SessionId
        | "operationkind" -> string record.OperationKind
        | "output" -> record.Result.Output
        | "errors" -> record.Result.Errors
        | "issuccess" -> string record.Result.IsSuccess
        | "rawerrortype" -> record.RawErrorType |> Option.defaultValue ""
        | "executiontimems" ->
            record.Result.ExecutionTime
            |> Option.map (fun value -> value.TotalMilliseconds.ToString("0.###"))
            |> Option.defaultValue ""
        | other -> invalidOp $"Unsupported result projection field '{other}'."

    let matchPredicate (queryText: string) (record: FsiExecutionRecord) =
        let normalized = queryText.Trim()

        match normalized.ToLowerInvariant() with
        | ""
        | "issuccess" -> record.Result.IsSuccess
        | "isfailure" -> not record.Result.IsSuccess
        | "hasvalue" -> record.Result.Value |> Option.exists (fun value -> not (String.IsNullOrWhiteSpace value))
        | "haserrors" -> not (String.IsNullOrWhiteSpace record.Result.Errors)
        | value when value.StartsWith("backendkind:", StringComparison.Ordinal) ->
            let expected = normalized.Substring("backendkind:".Length)
            String.Equals(string record.BackendKind, expected, StringComparison.OrdinalIgnoreCase)
        | value when value.StartsWith("hostid:", StringComparison.Ordinal) ->
            let expected = normalized.Substring("hostid:".Length)
            String.Equals(record.HostId, expected, StringComparison.OrdinalIgnoreCase)
        | value when value.StartsWith("sessionid:", StringComparison.Ordinal) ->
            let expected = normalized.Substring("sessionid:".Length)
            String.Equals(record.SessionId, expected, StringComparison.OrdinalIgnoreCase)
        | value when value.StartsWith("rawerrortype:", StringComparison.Ordinal) ->
            let expected = normalized.Substring("rawerrortype:".Length)
            record.RawErrorType
            |> Option.exists (fun raw -> String.Equals(raw, expected, StringComparison.OrdinalIgnoreCase))
        | value when value.StartsWith("valuecontains:", StringComparison.Ordinal) ->
            let expected = normalized.Substring("valuecontains:".Length)
            (preferredValue record).IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0
        | value when value.StartsWith("outputcontains:", StringComparison.Ordinal) ->
            let expected = normalized.Substring("outputcontains:".Length)
            record.Result.Output.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0
        | value when value.StartsWith("errorscontains:", StringComparison.Ordinal) ->
            let expected = normalized.Substring("errorscontains:".Length)
            record.Result.Errors.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0
        | other -> invalidOp $"Unsupported result predicate '{other}'."

    let successResponse queryId output materializedJson =
        { QueryId = queryId
          IsSuccess = true
          Output = output
          Errors = ""
          ProducedResultIds = []
          MaterializedJson = materializedJson }

    let failureResponse queryId message =
        { QueryId = queryId
          IsSuccess = false
          Output = ""
          Errors = message
          ProducedResultIds = []
          MaterializedJson = None }

    member _.Run(request: ResultQueryRequest, primaryRecords: FsiExecutionRecord list, secondaryRecords: FsiExecutionRecord list) =
        try
            match request.Language with
            | FSharpCode ->
                runFSharpCodeQuery request primaryRecords secondaryRecords
            | BuiltIn ->
                match request.Kind with
                | Exists ->
                    let value = primaryRecords |> List.exists (matchPredicate request.QueryText)
                    let materializedJson = serialize value
                    successResponse request.QueryId (string value) (Some materializedJson)
                | ForAll ->
                    let value = primaryRecords |> List.forall (matchPredicate request.QueryText)
                    let materializedJson = serialize value
                    successResponse request.QueryId (string value) (Some materializedJson)
                | Map ->
                    let values = primaryRecords |> List.map (projectionValue request.QueryText)
                    let materializedJson = serialize values
                    successResponse request.QueryId $"Mapped {values.Length} result(s)." (Some materializedJson)
                | Filter ->
                    let values =
                        primaryRecords
                        |> List.filter (matchPredicate request.QueryText)
                        |> List.map toSummary

                    let materializedJson = serialize values
                    successResponse request.QueryId $"Filtered {values.Length} result(s)." (Some materializedJson)
                | Zip ->
                    let projectionField =
                        if String.IsNullOrWhiteSpace request.QueryText then
                            "value"
                        else
                            request.QueryText

                    let length = max primaryRecords.Length secondaryRecords.Length

                    let rows =
                        [ for index in 0 .. length - 1 do
                              let left = primaryRecords |> List.tryItem index
                              let right = secondaryRecords |> List.tryItem index
                              let leftValue = left |> Option.map (projectionValue projectionField)
                              let rightValue = right |> Option.map (projectionValue projectionField)

                              yield
                                  { Index = index
                                    LeftResultId = left |> Option.map (fun value -> value.ResultId)
                                    RightResultId = right |> Option.map (fun value -> value.ResultId)
                                    LeftValue = leftValue
                                    RightValue = rightValue
                                    LeftIsSuccess = left |> Option.map (fun value -> value.Result.IsSuccess)
                                    RightIsSuccess = right |> Option.map (fun value -> value.Result.IsSuccess)
                                    AreEqual =
                                        match leftValue, rightValue with
                                        | Some lhs, Some rhs -> Some(String.Equals(lhs, rhs, StringComparison.Ordinal))
                                        | _ -> None } ]

                    let materializedJson = serialize rows
                    successResponse request.QueryId $"Zipped {rows.Length} row(s)." (Some materializedJson)
                | Diff ->
                    let projectionField =
                        if String.IsNullOrWhiteSpace request.QueryText then
                            "value"
                        else
                            request.QueryText

                    let length = max primaryRecords.Length secondaryRecords.Length

                    let rows =
                        [ for index in 0 .. length - 1 do
                              let left = primaryRecords |> List.tryItem index
                              let right = secondaryRecords |> List.tryItem index
                              let leftValue = left |> Option.map (projectionValue projectionField)
                              let rightValue = right |> Option.map (projectionValue projectionField)
                              let areEqual =
                                  match leftValue, rightValue with
                                  | Some lhs, Some rhs -> Some(String.Equals(lhs, rhs, StringComparison.Ordinal))
                                  | None, None -> Some true
                                  | _ -> Some false

                              if areEqual <> Some true then
                                  yield
                                      { Index = index
                                        LeftResultId = left |> Option.map (fun value -> value.ResultId)
                                        RightResultId = right |> Option.map (fun value -> value.ResultId)
                                        LeftValue = leftValue
                                        RightValue = rightValue
                                        LeftIsSuccess = left |> Option.map (fun value -> value.Result.IsSuccess)
                                        RightIsSuccess = right |> Option.map (fun value -> value.Result.IsSuccess)
                                        AreEqual = areEqual } ]

                    let materializedJson = serialize rows
                    successResponse request.QueryId $"Diff produced {rows.Length} row(s)." (Some materializedJson)
                | GroupBy ->
                    let projectionField =
                        if String.IsNullOrWhiteSpace request.QueryText then
                            "hostId"
                        else
                            request.QueryText

                    let groups =
                        primaryRecords
                        |> List.groupBy (projectionValue projectionField)
                        |> List.map (fun (key, records) ->
                            { Key = key
                              ResultIds = records |> List.map (fun value -> value.ResultId) })

                    let materializedJson = serialize groups
                    successResponse request.QueryId $"Grouped into {groups.Length} bucket(s)." (Some materializedJson)
        with ex ->
            failureResponse request.QueryId ex.Message
