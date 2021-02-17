module AstBuilder

open AstOperations
open FSharp.Data
open AstInstance
open AstHelpers
open AstMember
open AstYield
open AstRun
open FsAst
open Core

open System.Text.RegularExpressions
open FSharp.Text.RegexProvider

// "azure:compute/virtualMachine:VirtualMachine"
// CloudProvider - Always the same for each schema (azure here)
type ResourceInfoProvider =
    Regex<"(?<CloudProvider>\w+):(?<ResourceProviderNamespace>[A-Za-z0-9.]+)/(?<SubNamespace>\w+):(?<ResourceType>\w+)">

type TypeInfoProvider =
    Regex<"(?<CloudProvider>\w+):(?<ResourceProviderNamespace>[A-Za-z0-9.]+)/(?<SubNamespace>\w+):(?<ResourceType>\w+)">

let resourceInfo =
    ResourceInfoProvider(RegexOptions.Compiled)

let typeInfo =
    TypeInfoProvider(RegexOptions.Compiled)
    
type BuilderType =
    | Type of TypeInfoProvider.MatchType
    | Resource of ResourceInfoProvider.MatchType

let private argIdent =
    Pat.ident("arg")
    
let private argToInput =
    Expr.func("input", "arg")
    
let private args =
    Expr.ident("args")
    
let private funcIdent =
    Expr.ident("func")
    
let private yieldReturnExpr =
    Expr.list([ Expr.ident("id") ])

let private matchExpr =
    Expr.paren(
        Expr.match'(Expr.tuple(Expr.ident("lName"), Expr.ident("rName")), [
            Match.clause(Pat.tuple(Pat.null', Pat.null'), Expr.null')
            Match.clause(Pat.tuple(Pat.null', Pat.ident("name")), Expr.ident("name"))
            Match.clause(Pat.tuple(Pat.ident("name"), Pat.null'), Expr.ident("name"))
            Match.clause(Pat.wild, Expr.failwith("Duplicate name"))
        ]))

let private combineExpr =
    Expr.tuple(matchExpr,
               Expr.paren(Expr.func("List.concat", (Expr.list [ "lArgs"; "rArgs" ]))))

let private combineArgs =
    Pat.paren (Pat.tuple ((Pat.paren (Pat.tuple ("lName", "lArgs"))),
                          (Pat.paren (Pat.tuple ("rName", "rArgs")))))
    
let private combineMember =
    createMember' "this" "Combine" [combineArgs.ToRcd] [] combineExpr
    
let private forArgs =
    Pat.paren (Pat.tuple ("args", "delayedArgs"))

let private forExpr =
    Expr.methodCall("this.Combine",
                    [ Expr.ident("args")
                      Expr.func("delayedArgs", Expr.unit) ])

let private forMember =
    createMember' "this" "For" [forArgs.ToRcd] [] forExpr
 
let private delayMember =
    createMember "Delay" [Pat.ident("f").ToRcd] [] (Expr.func("f"))

let private zeroMember =
    createMember "Zero" [Pat.wild.ToRcd] [] (Expr.unit)
    
let private yieldMember =
    createYield yieldReturnExpr
    
let private newNameExpr =
    Expr.tuple(Expr.ident("newName"),
               Expr.ident("args"))

let private nameMember =
    createNameOperation newNameExpr
    
let private identArgExpr =
    Expr.ident("arg")
    
let createBuilderClass allTypes isType name properties =
    let argsType =
        name + "Args"

    let apply =
        Expr.func("List.fold", [
            Expr.ident("func")
            Expr.paren(createInstance argsType Expr.unit)
            Expr.ident("args")
        ])
       
    let runArgs =
        if isType then
            apply
        else
            Expr.paren(
                Expr.tuple(
                    Expr.ident("name"),
                    Expr.paren(apply)
                ))
        
    let createOperations propName (propType : string) =
        match propType with
        | "string"
        | "integer"
        | "number"
        | "boolean"
        | "array"
        | "union"
        | "json"
        | "complexD"
        | "object" ->
            createOperationsFor' isType propName propType argsType
        | _ -> // "complex:XXXX"
            let setExpr =
                Expr.sequential([
                    Expr.set("args." + propName, argToInput)
                    args
                ])
            
            let expr =
                Expr.list([
                    Expr.paren(
                        Expr.sequential([
                            Expr.let'("func", [Pat.typed("args", argsType)], setExpr)
                            funcIdent
                        ])
                    )
                ])
            
            [ createYield' argIdent expr ]
            // When creating a Yield, we could also create a
            // let storageOsDisk = virtualMachineStorageOsDisk
            // to simplify the name

    let nameAndType name (properties : (string * JsonValue) []) =
        let tName =
            match properties |>
                  Array.tryFind (fun (p, _) -> p = "language") |>
                  Option.bind (fun (_, v) -> v.Properties() |>
                                             Array.tryFind (fun (p, _) -> p = "csharp") |>
                                             Option.map snd) |>
                  Option.map (fun v -> v.GetProperty("name").AsString()) with
            | Some n -> n
            | None   -> name |> toPascalCase
        
        let (|SameType|_|) (jvs : JsonValue []) =
            List.distinct >>
            function
            | [type'] -> Some type'
            | _       -> None
            <| [ for jv in jvs do
                     
                     match jv.TryGetProperty("type") |>
                           Option.map (fun x -> x.AsString()) with
                     | Some x -> yield x
                     | _      -> ()
                                          
                     match jv.TryGetProperty("$ref") |>
                           Option.map (fun x -> x.AsString()) with
                     | Some x when allTypes |> Array.contains (x.Substring(8)) -> yield x
                     | _                                                       -> () ]
        
        let pType =
            properties |>
            Array.choose (function
                          | "$ref", v when v.AsString() = "pulumi.json#/Json" -> Some "json"
                          | "type", v  -> v.AsString() |> Some // Array type has also "items"
                          | "$ref", v  -> let t = v.AsString()
                                          // GET RID OF THIS STRING-BASED TYPE MATCHING IMMEDIATELY!
                                          if t.StartsWith("pulumi.json#/") then
                                              "complex"
                                          else
                                              "complex:" + v.AsString().Substring(8)
                                          |> Some
                          | "oneOf", v -> match v with
                                          | JsonValue.Array(SameType(type')) -> Some type'
                                          | _                                -> Some "union"
                          (*| "description"*)
                          | _          -> None) |>
            Array.sortBy (function | "union" -> 0 | "json" -> 1 | _ -> 2) |>
            Array.head
        
        (tName, pType)
    
    let nameAndTypes =
        properties |>
        Array.map (fun (x, y : JsonValue) -> nameAndType x (y.Properties()))
        
    let (propOfSameComplexType, otherProperties) =
        nameAndTypes |>
        Array.groupBy snd |>
        Array.partition (fun (t, l) -> t.StartsWith("complex:") && Array.length l > 1) |>
        (fun (l, r) -> (l |> Array.collect snd,
                        r |> Array.collect snd))
        
    let propOfSameComplexTypeIgnoreComplex =
        propOfSameComplexType |>
        Array.map (fun (n, _) -> (n, "complexD"))
        
    let order =
        nameAndTypes |> Array.map fst
        
    let operations =
        Array.append propOfSameComplexTypeIgnoreComplex otherProperties |>
        Array.sortBy (fun (n, _) -> order |> Array.findIndex ((=)n)) |>
        Seq.collect (fun (propName, propType) -> createOperations propName propType)
        
    let runReturnExpr =
        Expr.sequential([
            Expr.let'("func", [ "args"; "f" ], Expr.func("f", "args"))
            if isType then runArgs else createInstance name runArgs
        ])
    
    Module.type'(name + "Builder", [
        Type.ctor()
        
        yieldMember
        createRun (if isType then null else "name") runReturnExpr
        combineMember
        forMember
        delayMember
        zeroMember
        
        yield! if isType then [] else [ nameMember ]
        
        yield! operations
    ])