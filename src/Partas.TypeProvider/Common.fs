namespace Partas.TypeProvider.BuildHelper.DesignTime

open System
open System.IO
open ProviderImplementation.ProvidedTypes

[<AutoOpen>]
module internal Helpers =
    /// Materialises a lazy filesystem enumeration, yielding nothing for entries the
    /// compiler cannot read. `Directory.Enumerate*` defers its work, so the throw
    /// lands mid-iteration rather than at the call - a single unreadable folder
    /// anywhere in the tree would otherwise take down the entire provider.
    let tryEnumerate (enumerate: unit -> seq<'T>) =
        try
            enumerate () |> List.ofSeq
        with
        | :? UnauthorizedAccessException
        | :? Security.SecurityException
        | :? IOException -> []

    /// A constant baked into the consuming assembly. Only used for structure that
    /// is stable across builds - never for shas or working tree state.
    let constString name (value: string) doc =
        let prop =
            ProvidedProperty(name, typeof<string>, isStatic = true, getterCode = fun _ -> <@@ value @@>)

        prop.AddXmlDoc doc
        prop

    let constBool name (value: bool) doc =
        let prop =
            ProvidedProperty(name, typeof<bool>, isStatic = true, getterCode = fun _ -> <@@ value @@>)

        prop.AddXmlDoc doc
        prop

    /// Git ref names permit characters that cannot survive backtick quoting in F#.
    let isUsableMemberName (name: string) =
        name <> "" && not (name.Contains "`") && name |> Seq.forall (fun c -> not (Char.IsControl c))


    type XmlBuilderBase() =
        member inline _.Yield(s: string) = s
        member inline _.Yield(s: char) = string s
        member inline _.Yield(s: bool) = string s
        // member inline _.Zero() = ""
        member inline _.Yield(s: string list) = String.concat " " s
        member inline _.Combine(f1: string, f2: string) = f1 + " " + f2
        member inline _.Delay([<InlineIfLambda>] f: unit -> string) = f()

    type XmlDocs() =
        inherit XmlBuilderBase()
        member inline _.Run(s: string) = s
    type Wrapper(open', close) =
        inherit XmlBuilderBase()
        member inline _.Run(s: string) = open' + s + close
    type TagBuilder(tag) =
        inherit Wrapper($"<%s{tag}>", $"</%s{tag}>")
    type Parasite<^T when ^T:(member AddXmlDoc: string -> unit)>(this: ^T)=
        inherit XmlBuilderBase()
        member inline _.Run(s: string) =
            this.AddXmlDoc s
    type Symbiote<^T when ^T:(member AddXmlDoc: string -> unit)>(this: ^T)=
        inherit XmlBuilderBase()
        member inline _.Run(s: string) =
            this.AddXmlDoc s
            this


    [<Literal>]
    let br = "<br/>"
    [<Literal>]
    let rn = "\n"
    let param (name: string) = Wrapper($"<param name=\"{name}\">", "</param>")
    let summary = TagBuilder("summary")
    let remarks = TagBuilder("remarks")
    let example = TagBuilder("example")
    let code = TagBuilder("code")
    let c = TagBuilder("c")
    let i = TagBuilder("i")
    let b = TagBuilder("b")
    let squote = Wrapper("'", "'")
    let docs = XmlDocs()
    let inline parasite receiver = Parasite(receiver)
    let inline symbiote receiver = Symbiote(receiver)

    type ProvidedTypeDefinition with
        member inline this.setXmlDoc = parasite this
        member inline this.addXmlDoc = symbiote this
    type ProvidedProperty with
        member inline this.setXmlDoc = parasite this
        member inline this.addXmlDoc = symbiote this
    type ProvidedMethod with
        member inline this.setXmlDoc = parasite this
        member inline this.addXmlDoc = symbiote this
    type ProvidedStaticParameter with
        member inline this.setXmlDoc = parasite this
        member inline this.addXmlDoc = symbiote this

