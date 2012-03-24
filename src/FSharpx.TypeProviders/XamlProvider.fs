﻿module FSharpx.TypeProviders.XamlProvider

open Samples.FSharp.ProvidedTypes
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Windows
open System.Windows.Markup
open System.Xml
open System.Linq.Expressions
open FSharpx
open FSharpx.TypeProviders.DSL
open FSharpx.TypeProviders.Settings

let wpfAssembly = typeof<System.Windows.Controls.Button>.Assembly

/// Simple type wrapping Xaml file
type XamlFile(root:FrameworkElement) =
    let dict = new Dictionary<_,_>()

    member this.GetChild name = 
        match dict.TryGetValue name with
        | true,element -> element
        | false,element -> 
            let element = root.FindName name
            dict.[name] <- element
            element

    member this.Root = root

type XamlNode =
    { Position: FilePosition
      IsRoot: bool
      Name: string
      NodeType : Type }

let posOfReader filename (xaml:XmlReader) = 
    let lineInfo = xaml :> obj :?> IXmlLineInfo
    { Line = lineInfo.LineNumber
      Column = lineInfo.LinePosition
      FileName = filename }

let createXamlNode filename isRoot (xaml:XmlReader) =
    let pos = posOfReader filename xaml
    try
        let typeName = xaml.Name
        let name =                        
            match xaml.GetAttribute("Name") with
            | name when name <> null -> Some name
            | _ ->
                match xaml.GetAttribute("x:Name") with
                | name when name <> null -> Some name
                | _ -> None

        let propertyName =
            match name with
            | Some name -> name
            | None -> 
                if isRoot then "Root" else
                failwith "Cannot create a nested type without a name" // TODO: Generate one

        let propertyType =
            match typeName with
            | "Window" -> typeof<Window>
            | other ->
                match wpfAssembly.GetType(sprintf "System.Windows.Controls.%s" other) with
                | null -> typeof<obj>
                | st -> st

        { Position = pos
          IsRoot = isRoot
          Name = propertyName
          NodeType = propertyType }
    with
    | :? XmlException -> failwithf "Error near %A" pos

let readXamlFile filename (xaml:XmlReader) =
    seq {
        let isRoot = ref true
        while xaml.Read() do
            match xaml.NodeType with
            | XmlNodeType.Element ->               
                yield createXamlNode filename (!isRoot) xaml
                isRoot := false
            | XmlNodeType.EndElement | XmlNodeType.Comment | XmlNodeType.Text -> ()
            | unexpected -> failwithf "Unexpected node type %A at %A" unexpected (posOfReader filename xaml) }

let createXmlReader(textReader:TextReader) =
    XmlReader.Create(textReader, XmlReaderSettings(IgnoreProcessingInstructions = true, IgnoreWhitespace = true))

let createTypeFromReader typeName fileName (reader: TextReader) =
    let elements = 
        reader
        |> createXmlReader 
        |> readXamlFile fileName
        |> Seq.toList

    let root = List.head elements

    let accessExpr node (args:Expr list) =
        let name = node.Name
        let expr = if node.IsRoot then <@@ (%%args.[0] :> XamlFile).Root @@> else <@@ (%%args.[0] :> XamlFile).GetChild name @@>
        Expr.Coerce(expr,node.NodeType)

    erasedType<XamlFile> thisAssembly rootNamespace typeName
        |> addDefinitionLocation root.Position
        |+!> (provideConstructor
                [] 
                (fun args -> <@@ XamlFile(XamlReader.Parse(File.ReadAllText fileName) :?> FrameworkElement) @@>)
                |> addXmlDoc (sprintf "Initializes typed access to %s" fileName)
                |> addDefinitionLocation root.Position)
    |++!> (
        elements
        |> Seq.map (fun node ->
             provideProperty node.Name node.NodeType (accessExpr node)
             |> addXmlDoc (sprintf "Gets the %s element" node.Name)
             |> addDefinitionLocation node.Position))   

/// Infer schema from the loaded data and generate type with properties     
let xamlType (ownerType:TypeProviderForNamespaces)  (cfg:TypeProviderConfig) =
    erasedType<obj> thisAssembly rootNamespace "XAML"
      |> staticParameter "FileName" (fun typeName configFileName -> 
            let fileName = findConfigFile cfg.ResolutionFolder configFileName

            if File.Exists fileName |> not then
                failwithf "The file '%s' does not exist" fileName
            
            watchForChanges ownerType fileName
            
            use reader = new StreamReader(fileName)
            createTypeFromReader typeName fileName reader)