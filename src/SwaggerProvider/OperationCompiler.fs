﻿namespace SwaggerProvider.Internal.Compilers

open ProviderImplementation.ProvidedTypes
open FSharp.Data.Runtime.NameUtils
open SwaggerProvider.Internal.Schema

open System
open FSharp.Data

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.ExprShape
open System.Text.RegularExpressions

/// Object for compiling operations.
type OperationCompiler (schema:SwaggerObject, defCompiler:DefinitionCompiler, headers : seq<string*string>) =

    let operationGroups =
        schema.Paths
        |> Seq.groupBy (fun op->
            if op.Tags.Length > 0
            then op.Tags.[0] else "Root")
        |> Seq.toList

    let compileOperation (tag:string) (op:OperationObject) =
        let parameters =
            [let required, optional = op.Parameters |> Array.partition (fun x->x.Required)
             for x in Array.append required optional ->
                ProvidedParameter(niceCamelName x.Name, defCompiler.CompileTy x.Type x.Required)]
        let retTy =
            let okResponse = // BUG :  wrong selector
                op.Responses |> Array.tryFind (fun (code, resp)->
                    (code.IsSome && code.Value = 200) || code.IsNone)
            match okResponse with
            | Some (_,resp) ->
                match resp.Schema with
                | None -> typeof<unit>
                | Some ty -> defCompiler.CompileTy ty true
            | None -> typeof<unit>

        let methodName =
            let prefix = tag.TrimStart('/') + "_"
            if op.OperationId.StartsWith(prefix) // Beatify names for Swashbuckle generated schemas
                then nicePascalName <| op.OperationId.Substring(prefix.Length)
                else nicePascalName <| op.OperationId

        let m = ProvidedMethod(methodName, parameters, retTy, IsStaticMethod = true)
        if not <| String.IsNullOrEmpty(op.Summary)
            then m.AddXmlDoc(op.Summary)
        if op.Deprecated
            then m.AddObsoleteAttribute("Operation is deprecated", false)

        m.InvokeCode <- fun args ->
            let basePath =
                let scheme =
                    match schema.Schemes with
                    | [||]  -> "http" // Should use the scheme used to access the Swagger definition itself.
                    | array -> array.[0]
                scheme + "://" + schema.Host + schema.BasePath

            // Fit headers into quotation
            let headers =
                let headerPairs =
                    seq {
                        yield! headers
                        if (headers |> Seq.exists (fun (h,_)->h="Content-Type") |> not) then
                            if (op.Consumes |> Seq.exists (fun mt -> mt="application/json")) then
                                yield "Content-Type","application/json"
                    }
                    |> List.ofSeq
                    |> List.map (fun (h1,h2) -> Expr.NewTuple [Expr.Value(h1);Expr.Value(h2)])
                Expr.NewArray (typeof<Tuple<string,string>>, headerPairs)

            // Locates parameters matching the arguments
            let parameters =
                args
                |> List.map (function
                    | ShapeVar sVar as expr ->
                        let param =
                            op.Parameters
                            |> Array.find (fun x -> niceCamelName x.Name = sVar.Name) // ???
                        param, expr
                    | _  ->
                        failwithf "Function '%s' does not support functions as arguments." m.Name
                    )


            // Makes argument a string // TODO: Make body an exception
            let coerceString defType (format : CollectionFormat) exp =
                let obj = Expr.Coerce(exp, typeof<obj>)
                <@ (%%obj : obj).ToString() @>

            let rec corceQueryString name expr =
                let obj = Expr.Coerce(expr, typeof<obj>)
                <@ let o = (%%obj : obj)
                   SwaggerProvider.Internal.RuntimeHelpers.toQueryParams name o @>

            let replacePathTemplate path name (exp : Expr) =
                let template = "{" + name + "}"
                <@@ Regex.Replace(%%path, template, string (%%exp : string)) @@>

            let addPayload load (param : ParameterObject) (exp : Expr) =
                let name = param.Name
                let var = coerceString param.Type param.CollectionFormat exp
                match load with
                | Some (FormData, b) -> Some (FormData, <@@ Seq.append %%b [name, (%var : string)] @@>)
                | None               -> match param.In with
                                        | Body -> Some (Body, Expr.Coerce (exp, typeof<obj>))
                                        | _    -> Some (FormData, <@@ (seq [name, (%var : string)]) @@>)
                | _                  -> failwith ("Can only contain one payload")

            let addQuery quer name (exp : Expr) =
                let listValues = corceQueryString name exp
                <@@ List.append
                        (%%quer : (string*string) list)
                        (%listValues : (string*string) list) @@>

            let addHeader head name (exp : Expr) =
                <@@ Array.append (%%head : (string*string) []) ([|name, (%%exp : string)|]) @@>

            // Partitions arguments based on their locations
            let (path, payload, queries, heads) =
                let mPath = op.Path
                parameters
                |> List.fold (
                    fun (path, load, quer, head) (param : ParameterObject, exp) ->
                        let name = param.Name
                        let value = coerceString param.Type param.CollectionFormat exp
                        match param.In with
                        | Path   -> (replacePathTemplate path name value, load, quer, head)
                        | FormData
                        | Body   -> (path, addPayload load param exp, quer, head)
                        | Query  -> (path, load, addQuery quer name exp, head)
                        | Header -> (path, load, quer, addHeader head name value)
                    )
                    (<@@ mPath @@>, None, <@@ ([] : (string*string) list)  @@>, headers)


            let address = <@@ basePath + %%path @@>
            let restCall = op.Type.ToString()

            let customizeHttpRequest =
                <@@ fun (request:Net.HttpWebRequest) ->
                        if restCall = "Post"
                            then request.ContentLength <- 0L
                        request @@>

            // Make HTTP call
            let result =
                match payload with
                | None ->
                    <@@ Http.RequestString(%%address,
                            httpMethod = restCall,
                            headers = (%%heads : array<string*string>),
                            query = (%%queries : (string * string) list),
                            customizeHttpRequest = (%%customizeHttpRequest : Net.HttpWebRequest -> Net.HttpWebRequest)) @@>
                | Some (FormData, b) ->
                    <@@ Http.RequestString(%%address,
                            httpMethod = restCall,
                            headers = (%%heads : array<string*string>),
                            body = HttpRequestBody.FormValues (%%b : seq<string * string>),
                            query = (%%queries : (string * string) list),
                            customizeHttpRequest = (%%customizeHttpRequest : Net.HttpWebRequest -> Net.HttpWebRequest)) @@>
                | Some (Body, b)     ->
                    <@@ let body = SwaggerProvider.Internal.RuntimeHelpers.serialize (%%b : obj)
                        Http.RequestString(%%address,
                            httpMethod = restCall,
                            headers = (%%heads : array<string*string>),
                            body = HttpRequestBody.TextRequest body,
                            query = (%%queries : (string * string) list),
                            customizeHttpRequest = (%%customizeHttpRequest : Net.HttpWebRequest -> Net.HttpWebRequest))
                    @@>
                | Some (x, _) -> failwith ("Payload should not be able to have type: " + string x)

            // Return deserialized object
            let value = <@@ SwaggerProvider.Internal.RuntimeHelpers.deserialize
                                (%%result : string) retTy @@>
            Expr.Coerce(value, retTy)

        m

    /// Compiles the operation.
    member __.Compile() =
        operationGroups
        |> List.map (fun (tag, operations) ->
            let ty = ProvidedTypeDefinition(nicePascalName tag, Some typeof<obj>, IsErased = false)
            operations
            |> Seq.map (compileOperation tag)
            |> Seq.toList
            |> ty.AddMembers
            ty
        )
