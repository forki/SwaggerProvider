﻿namespace SwaggerProvider.Internal.Schema.Parsers

open FSharp.Data
open FSharp.Data.JsonExtensions
open System

open SwaggerProvider.Internal.Schema

/// Helper functions for optional swagger values of types string and string array.
[<AutoOpen>]
module Extensions =
    type JsonValue with
        /// Get field that is `Required` in Swagger specification
        member this.GetRequiredField (fieldName, spec) =
            match this.TryGetProperty(fieldName) with
            | Some(value) -> value
            | None -> raise <| FieldNotFoundException(this, fieldName, spec)

        /// Gets the string value of the property if it exists. Empty string otherwise.
        member this.GetStringSafe(propertyName) =
            match this.TryGetProperty(propertyName) with
            | Some(value) -> value.AsString()
            | None -> String.Empty

        /// Gets the string array for the property if it exists. Empty array otherwise.
        member this.GetStringArraySafe(propertyName) =
            match this.TryGetProperty(propertyName) with
            | Some(value) -> value.AsArray() |> Array.map (fun x->x.AsString())
            | None -> [||]

module JsonParser =

    // Type that hold parsing context to resolve `$ref`s
    type ParserContext =
        {
            /// An object to hold parameters that can be used across operations
            Parameters: Map<string, ParameterObject>
            /// An object to hold responses that can be used across operations.
            Responses: Map<string, ResponseObject>
            /// A list of parameters that are applicable for all the operations described under this path.
            /// These parameters can be overridden at the operation level, but cannot be removed there.
            /// The list MUST NOT include duplicated parameters. A unique parameter is defined by a combination of
            /// a name and location. The list can use the Reference Object to link to parameters that are defined
            /// at the Swagger Object's parameters. There can be one "body" parameter at most.
            ApplicableParameters : ParameterObject[]
        }

        /// Resolve ParameterObject by `$ref` if such field exists
        member this.ResolveParameterObject (obj:JsonValue) =
            obj.TryGetProperty("$ref")
            |> Option.map (fun refObj ->
                let ref = refObj.AsString()
                match this.Parameters.TryFind(ref) with
                | Some(param) ->
                    match obj.TryGetProperty("required") with
                    | Some(req) -> {param with Required = req.AsBoolean()}
                    | _ -> param
                | None -> raise <| UnknownSwaggerReferenceException(ref))

        // Resolve ResponseObject by `$ref` if such field exists
        member this.ResolveResponseObject (obj:JsonValue) =
            obj.TryGetProperty("$ref")
            |> Option.map (fun refObj ->
                let ref = refObj.AsString()
                match this.Responses.TryFind(ref) with
                | Some(response) -> response
                | None -> raise <| UnknownSwaggerReferenceException(ref))

        /// Default empty context
        static member Empty =
            {
                Parameters = Map.empty<_,_>
                Responses = Map.empty<_,_>
                ApplicableParameters = [||]
            }


    /// Verify if name follows Swagger Schema Extension name pattern
    let isSwaggerSchemaExtensionName (name:string) =
        name.StartsWith("x-")

    // TODO: ...
    /// Parses the JsonValue as a SchemaObject
    let rec parseSchemaObject (parsedTys:Map<string,SchemaObject>) (obj:JsonValue) : SchemaObject =
        let spec = "http://swagger.io/specification/#schemaObject"
        let parsers : (JsonValue->Option<SchemaObject>)[] =
            [|
                (fun obj -> // Parse `enum` - http://json-schema.org/latest/json-schema-validation.html#anchor76
                    obj.TryGetProperty("enum")
                    |> Option.map (fun enum -> Enum (enum.AsArray() |> Array.map (fun x->x.AsString())))
                )
                (fun obj -> // Parse `$refs`
                    obj.TryGetProperty("$ref")
                    |> Option.map (fun ref -> Reference (ref.AsString()) )
                )
                (fun obj -> // Parse Arrays - http://json-schema.org/latest/json-schema-validation.html#anchor36
                            // TODO: `items` may be an array, `additionalItems` may be filled
                    obj.TryGetProperty("type")
                    |> Option.bind (fun ty ->
                        if ty.AsString() <> "array" then None
                        else obj.TryGetProperty("items")
                       )
                    |> Option.map ((parseSchemaObject parsedTys) >> Array)
                )
                (fun obj -> // Parse primitive types
                     obj.TryGetProperty("type")
                     |> Option.bind (fun ty ->
                        let format = obj.GetStringSafe("format")
                        match ty.AsString() with
                        | "boolean" -> Some Boolean
                        | "integer" when format = "int32" -> Some Int32
                        | "integer" -> Some Int64
                        | "number" when format = "float" -> Some Float
                        | "number" when format = "int32" -> Some Int32
                        | "number" when format = "int64" -> Some Int64
                        | "number" -> Some Double
                        | "string" when format = "date" -> Some Date
                        | "string" when format = "date-time" -> Some DateTime
                        | "string" -> Some String
                        | "file" -> Some File
                        | _ -> None
                    )
                )
                (fun obj -> // TODO: Parse Objects
                    obj.TryGetProperty("properties")
                    |> Option.bind (fun properties ->
                        let requiredProperties =
                          match obj.TryGetProperty("required") with
                          | None -> Set.empty<_>
                          | Some(req) ->
                              req.AsArray()
                              |> Array.map (fun x-> x.AsString())
                              |> Set.ofArray
                        let properties =
                          properties.Properties
                          |> Array.map (fun (name,obj) ->
                              parseDefinitionProperty (name,obj, requiredProperties.Contains name))

                        Some <| Object properties
                      )
                )
                (fun obj -> // Parse Object that represent Dictionary
                    match obj.TryGetProperty("type") with
                    | Some(ty) when ty.AsString() = "object" ->
                        obj.TryGetProperty("additionalProperties")
                        |> Option.map ((parseSchemaObject parsedTys) >> Dictionary)
                    | _ -> None
                )
                (fun obj -> // Models with Composition
                    match obj.TryGetProperty("allOf") with
                    | Some(allOf) ->
                        let props =
                            allOf.AsArray()
                            |> Array.map (parseSchemaObject parsedTys)
                            |> Array.map (function
                                | Object props -> props
                                | Reference path ->
                                    match parsedTys.TryFind path with
                                    | Some(obj) ->
                                        match obj with
                                        | Object props -> props
                                        | _ -> failwithf "Could not compose %O" obj
                                    | None -> failwithf "Reference to unknown type %s" path
                                | obj -> failwithf "Could not compose %O" obj)
                            |> Array.concat
                        Some <| Object props
                    | None -> None
                )
                (fun obj -> // Models with Polymorphism Support
                    match obj.TryGetProperty("discriminator") with
                    | Some(discriminator) ->
                        failwith "Models with Polymorphism Support is not supported yet. If you see this error plrease report it on GitHub (https://github.com/sergey-tihon/SwaggerProvider/issues) with schema example."
                    | None -> None
                )
            |]

        let result = Array.tryPick (fun f -> f obj) parsers
        match result with
        | Some(schemaObj) -> schemaObj
        | None -> Object [||] // Default type when parsers could not determine the type based ob schema.
                              // Example of schema : {}
                  //failwithf "Unable to parse SchemaObject: %A" obj


    /// Parses DefinitionProperty
    and parseDefinitionProperty (name, obj, required) : DefinitionProperty =
        {
            Name = name;
            Type = parseSchemaObject Map.empty obj
            IsRequired = required
            Description = obj.GetStringSafe("description")
        }

    /// Parses string as a ParameterObjectLocation.
    let parseOperationParameterLocation obj (location:string) : ParameterObjectLocation =
        let spec = "http://swagger.io/specification/#parameterObject"
        match location with
        | "query"    -> Query
        | "header"   -> Header
        | "path"     -> Path
        | "formData" -> FormData
        | "body"     -> Body
        | _ -> raise <| UnknownFieldValueException(obj, location, "in", spec)

    /// Parses the JsonValue as a ParameterObject.
    let parseParameterObject (obj:JsonValue) : ParameterObject =
        let spec = "http://swagger.io/specification/#parameterObject"
        let location =
            obj.GetRequiredField("in", spec).AsString()
            |> (parseOperationParameterLocation obj)
        {
            Name = obj.GetRequiredField("name", spec).AsString()
            In = location
            Description = obj.GetStringSafe("description")
            Required = match obj.TryGetProperty("required") with
                       | Some(x) -> x.AsBoolean() | None -> false
            Type =
                match location with
                | Body -> obj.GetRequiredField("schema", spec) |> parseSchemaObject Map.empty
                | _    -> obj |> parseSchemaObject Map.empty // TODO: Restrict parser
                          // The `type` value MUST be one of "string", "number", "integer", "boolean", "array" or "file"
            CollectionFormat =
                match location, obj.TryGetProperty("collectionFormat") with
                | Body,     Some _                             -> failwith "The field collectionFormat is not applicable for parameters of type body"
                | _,        Some x when x.AsString() = "csv"   -> Csv
                | _,        Some x when x.AsString() = "ssv"   -> Ssv
                | _,        Some x when x.AsString() = "tsv"   -> Tsv
                | _,        Some x when x.AsString() = "pipes" -> Pipes
                | FormData, Some x when x.AsString() = "multi" -> Multi
                | Query,    Some x when x.AsString() = "multi" -> Multi
                | _,        Some x when x.AsString() = "multi" -> failwith "Format `multi` is only supported by Query and FormData"
                | _,        Some x                             -> failwithf "Format `%s` is not supported" (x.AsString())
                | _,        None                               -> Csv // Default value
        }

    /// Parse the JsonValue as a Parameters Definition Object
    let parseParametersDefinition (obj:JsonValue) : Map<string, ParameterObject> =
        obj.Properties
        |> Array.map (fun (name, obj) ->
            "#/parameters/"+name, parseParameterObject obj)
        |> Map.ofArray

    /// Parses the JsonValue as a ResponseObject.
    let parseResponseObject (context:ParserContext) (obj:JsonValue) : ResponseObject =
        let spec = "http://swagger.io/specification/#responseObject"
        match context.ResolveResponseObject obj with
        | Some(response) -> response
        | None ->
            {
                Description = obj.GetRequiredField("description", spec).AsString()
                Schema =
                    obj.TryGetProperty("schema")
                    |> Option.map (parseSchemaObject Map.empty)
            }

    /// Parses the JsonValue as a Responses  Definition Object
    let parseResponsesDefinition (obj:JsonValue) : Map<string, ResponseObject> =
        obj.Properties
        |> Array.map (fun (name, obj) ->
                "#/responses/"+name, parseResponseObject (ParserContext.Empty) obj)
        |> Map.ofSeq

    /// Parses the JsonValue as a ResponseObject[].
    let parseResponsesObject (context:ParserContext) (obj:JsonValue) : (Option<int>*ResponseObject)[] =
        let spec = "http://swagger.io/specification/#httpCodes"
        obj.Properties
        |> Array.filter (fun (property,_) -> not <| isSwaggerSchemaExtensionName property)
        |> Array.map (fun (property, objValue) ->
            let code =
                if property = "default" then None
                else
                    match Int32.TryParse(property) with
                    | true, value -> Some value
                    | false, _ -> raise <| UnknownFieldValueException(obj, property, "HTTP Status Code", spec)
            code, parseResponseObject context objValue)

    /// Parses the JsonValue as an OperationObject.
    let parseOperationObject (context:ParserContext) path opType (obj:JsonValue) : OperationObject =
        let spec = "http://swagger.io/specification/#operationObject"
        let mergeParameters (specified:ParameterObject[]) (inherited:ParameterObject[]) =
            Array.append specified inherited
            |> Array.fold (fun (cache,result) param ->
                let key = (param.Name, param.In)
                if Set.contains key cache
                    then (cache, result)
                    else (Set.add key cache, param::result)
                ) (Set.empty<_>,[])
            |> snd |> List.rev |> Array.ofList

        {
            Path = path
            Type = opType
            Tags = obj.GetStringArraySafe("tags")
            Summary = obj.GetStringSafe("summary")
            Description = obj.GetStringSafe("description")
            OperationId = obj.GetStringSafe("operationId")
            Consumes = obj.GetStringArraySafe("consumes")
            Produces = obj.GetStringArraySafe("produces")
            Deprecated = match obj.TryGetProperty("deprecated") with
                         | Some(value) -> value.AsBoolean()
                         | None -> false
            Responses =
                obj.GetRequiredField("responses", spec)
                |> (parseResponsesObject context)
            Parameters =
                mergeParameters
                   (match obj.TryGetProperty("parameters") with
                        | Some(parameters) ->
                            parameters.AsArray()
                            |> Array.map (fun obj ->
                                match context.ResolveParameterObject obj with
                                | Some(param) -> param
                                | None -> parseParameterObject obj)
                        | None -> [||])
                   context.ApplicableParameters
        }

    /// Parse Paths Object as a PathItemObject[]
    let parsePathsObject (context:ParserContext) (obj:JsonValue) : OperationObject[] =
        let parsePathItemObject (context:ParserContext) path (field, obj) =
            match field with
            | "get"     -> Some <| parseOperationObject context path Get obj
            | "put"     -> Some <| parseOperationObject context path Put obj
            | "post"    -> Some <| parseOperationObject context path Post obj
            | "delete"  -> Some <| parseOperationObject context path Delete obj
            | "options" -> Some <| parseOperationObject context path Options obj
            | "head"    -> Some <| parseOperationObject context path Head obj
            | "patch"   -> Some <| parseOperationObject context path Patch obj
            | "$ref"       -> failwith "External definition of this path item is not supported yet"
            | _ -> None
        let updateContext (pathItemObj:JsonValue) =
            match pathItemObj.TryGetProperty("parameters") with
            | None -> context
            | Some(parameters) ->
                {
                context with
                    ApplicableParameters =
                        parameters.AsArray()
                        |> Array.map (fun paramObj ->
                            match context.ResolveParameterObject paramObj with
                            | Some(param) -> param
                            | None -> parseParameterObject paramObj
                        )
                }

        obj.Properties
        |> Array.filter(fun (path,_) -> not <| isSwaggerSchemaExtensionName path)
        |> Array.map (fun (path, pathItemObj) ->
            let newContext = updateContext pathItemObj
            pathItemObj.Properties
            |> Array.choose (parsePathItemObject newContext path)
           )
        |> Array.concat

    /// Parse Definitions Object as a SchemaObject[]
    let parseDefinitionsObject (obj:JsonValue) : (string*SchemaObject)[] =
        obj.Properties
        |> Array.fold (fun tys (name, schemaObj) ->
            Map.add
                ("#/definitions/"+name)
                (parseSchemaObject tys schemaObj)
                tys
            ) Map.empty<_,_>
        |> Map.toArray

    /// Parses the JsonValue as an InfoObject.
    let parseInfoObject (obj:JsonValue) : InfoObject =
        let spec = "http://swagger.io/specification/#infoObject"
        {
            Title = obj.GetRequiredField("title", spec).AsString()
            Description = obj.GetStringSafe("description")
            Version = obj.GetRequiredField("version", spec).AsString()
        }

    /// Parses the JsonValue as a TagObject.
    let parseTagObject (obj:JsonValue) : TagObject =
        let spec = "http://swagger.io/specification/#tagObject"
        {
            Name = obj.GetRequiredField("name", spec).AsString()
            Description = obj.GetStringSafe("description")
        }

    /// Parses the JsonValue as a SwaggerSchema.
    let parseSwaggerObject (obj:JsonValue) : SwaggerObject =
        let spec = "http://swagger.io/specification/#swaggerObject"

        let swaggerVersion = obj.GetRequiredField("swagger", spec).AsString()
        if swaggerVersion <> "2.0" then
            raise <| UnsupportedSwaggerVersionException(swaggerVersion)

        // Context holds parameters and responses that could be referenced from path definitions
        let context =
            {
                ParserContext.Empty with
                    Parameters =
                        match obj.TryGetProperty("parameters") with
                        | None -> Map.empty<_,_>
                        | Some(parameters) -> parseParametersDefinition parameters
                    Responses =
                        match obj.TryGetProperty("responses") with
                        | None -> Map.empty<_,_>
                        | Some(responses) -> parseResponsesDefinition responses
            }

        {
            Info = parseInfoObject(obj.GetRequiredField("info", spec))
            Host = obj.GetStringSafe("host")
            BasePath = obj.GetStringSafe("basePath")
            Schemes = obj.GetStringArraySafe("schemes")
            Tags =
                match obj.TryGetProperty("tags") with
                | None -> [||]
                | Some(tags) ->
                    tags.AsArray() |> Array.map parseTagObject
            Paths =
                obj.GetRequiredField("paths", spec)
                |> (parsePathsObject context)
            Definitions =
                match obj.TryGetProperty("definitions") with
                | None -> [||]
                | Some(definitions) -> parseDefinitionsObject definitions
        }
