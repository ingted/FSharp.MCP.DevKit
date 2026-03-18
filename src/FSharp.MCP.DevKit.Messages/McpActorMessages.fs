namespace FSharp.MCP.DevKit.Core

open System
open FSharp.Compiler.Diagnostics

type FsiResult =
    { Output: string
      Errors: string
      IsSuccess: bool
      Value: obj option
      ExecutionTime: TimeSpan option
      Diagnostics: FSharpDiagnostic[] }

namespace FSharp.MCP.DevKit.Core.Actors

open System
open FSharp.MCP.DevKit.Core

type FsiEvalRequest = {
    RequestId: string
    Code: string
    IsExpression: bool
}

type FsiEvalResponse = {
    RequestId: string
    Result: FsiResult
}
