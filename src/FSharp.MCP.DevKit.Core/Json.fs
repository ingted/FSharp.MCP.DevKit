namespace FSharp.MCP.DevKit.Core

open System.Text.Json
open System.Text.Json.Serialization

[<RequireQualifiedAccess>]
module FSharpJson =

    let private serializerOptions =
        lazy
            (
                let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
                options.Converters.Add(JsonFSharpConverter())
                options
            )

    let options = serializerOptions.Value

    let serialize<'T> (value: 'T) = JsonSerializer.Serialize(value, options)

    let serializeObject (value: obj) =
        if isNull value then
            "null"
        else
            JsonSerializer.Serialize(value, value.GetType(), options)

    let deserialize<'T> (json: string) = JsonSerializer.Deserialize<'T>(json, options)
