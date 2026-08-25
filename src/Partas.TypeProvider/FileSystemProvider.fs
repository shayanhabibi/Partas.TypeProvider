namespace Partas.TypeProvider.BuildHelper.DesignTime

open System
open System.IO
open System.Text.RegularExpressions
open ProviderImplementation.ProvidedTypes

module private VirtualFileProvider =
    module Parser =
        // Tree parser is based on the work of Nathan Friend:
        // https://gitlab.com/nfriend/tree-online/-/blob/ef0414eb6f5f097d38272ef45b6633b2d7aaec62/src/lib/parse-input.ts

        type INode(name: string, indentCount: int, ?parent: INode) =
            let children = ResizeArray<INode>()
            let mutable parent = parent

            /// <summary>
            /// Returns the name of the inode
            /// </summary>
            /// <returns></returns>
            member _.Name =
                // Remove trailing slash or backslash
                // The name of a folder should not have a trailing slash
                name.TrimEnd('/', '\\')

            /// <summary>
            /// Returns a normalized name for the inode.
            ///
            /// If the inode is a file, the name is returned as is.
            /// If the inode is a folder, the name is returned with a trailing slash.
            /// </summary>
            /// <returns></returns>
            member this.NormalizedName =
                if this.IsFolder then
                    this.Name + string Path.DirectorySeparatorChar
                else
                    this.Name

            member _.IndentCount = indentCount
            member _.Children = children
            member _.Parent = parent

            member _.SetParent(parentRef: INode) = parent <- Some parentRef

            /// <summary>
            /// Get a value indicating whether the inode is a file or not.
            ///
            /// By opposition, to a folder a file is a node without children and without a trailing slash or backslash.
            /// </summary>
            /// <returns>
            /// True if the inode is a file, false otherwise.
            /// </returns>
            member this.IsFile = not this.IsFolder

            /// <summary>
            /// Get a value indicating whether the inode is a folder or not.
            ///
            /// A folder is a node with children or a node with a trailing slash or backslash.
            /// </summary>
            /// <returns>
            /// True if the inode is a folder, false otherwise.
            /// </returns>
            member _.IsFolder =
                // A folder is a node with children or a node with a trailing slash
                // Trailing slash allows to have empty folders
                children.Count > 0 || name.EndsWith("/") || name.EndsWith("\\")

            override this.Equals(obj) =
                match obj with
                | :? INode as other ->
                    let parentLiteEquals =
                        match this.Parent, other.Parent with
                        | Some thisParent, Some otherParent ->
                            // We can't use the default equals because it will cause a stack overflow
                            // because the types have mutual references
                            thisParent.Name = otherParent.Name
                            && thisParent.IndentCount = otherParent.IndentCount
                            && thisParent.Children.Count = otherParent.Children.Count
                        | None, None -> true
                        | _ -> false

                    let childrenEquals =
                        this.Children.Count = other.Children.Count
                        && Seq.forall2
                            (fun thisChild otherChild -> thisChild.Equals(otherChild))
                            this.Children
                            other.Children

                    other.Name = this.Name
                    && other.IndentCount = this.IndentCount
                    && parentLiteEquals
                    && childrenEquals

                | _ -> false

            override this.GetHashCode() =
                (this.Name, this.IndentCount, this.Parent, this.Children).GetHashCode()

        let private processText (text: string) =
            // Normalize line endings
            text.Replace("\r\n", "\n").Split('\n')
            // Remove empty lines
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            // Transform into a inode
            |> Array.map (fun line ->
                let regex = Regex("^(?'indentation'\s*)(?'name'.*)$")

                let m = regex.Match(line)

                if m.Success then
                    let indentation = m.Groups["indentation"].Value.Length
                    let name = m.Groups["name"].Value

                    INode(name, indentation)
                else
                    failwithf "Failed to parse line: %s" line
            )

        /// <summary>
        /// Parse a configuration text into a INode tree.
        /// </summary>
        /// <param name="rootFolder">
        /// Name of the root inode
        ///
        /// It can be a simple name like "dist", "." or a path like "dist/client" or "/home/user/"
        /// </param>
        /// <param name="config">Config to parse</param>
        /// <returns>Returns a INode representing the parser configuration</returns>
        let rec parse (rootFolder: string) (config: string) =
            let root = INode(rootFolder, -1)
            let inodes = processText config

            let mutable walkingPaths = ResizeArray [ root ]

            for inode in inodes do
                while walkingPaths[walkingPaths.Count - 1].IndentCount >= inode.IndentCount do
                    // Pop the last element
                    walkingPaths.RemoveAt(walkingPaths.Count - 1)

                let parent = walkingPaths[walkingPaths.Count - 1]
                parent.Children.Add(inode)
                inode.SetParent parent

                walkingPaths.Add(inode)

            root

    let private createFileLiteral
        (inode: Parser.INode)
        (rootPath: string)
        (rootType: ProvidedTypeDefinition)
        =

        let fullPath = rootPath + string Path.DirectorySeparatorChar + inode.Name

        let pathFieldProperty =
            ProvidedProperty(
                inode.Name,
                typeof<FileInfo>,
                isStatic = true,
                getterCode = fun args -> <@@ FileInfo(fullPath) @@>
            )

        pathFieldProperty.setXmlDoc { "Path to "; squote { fullPath } }
        rootType.AddMember pathFieldProperty

    let rec createInodeProperties
        (inode: Parser.INode)
        (rootPath: string)
        (rootType: ProvidedTypeDefinition)
        =

        let directoryInfo = Path.Combine(rootPath, inode.Name) |> DirectoryInfo

        // If we are not at the top of the virtual tree, we add access to the current folder
        // If user needs to access the current folder, he should use RelativeFileSystemProvider instead
        if inode.IndentCount <> -1 then

            // Extract the full path in a variable so we can use it in the ToString method
            let currentFolderFullName = directoryInfo.FullName

            let currentFolderProperty =
                ProvidedProperty(
                    ".",
                    typeof<DirectoryInfo>,
                    isStatic = true,
                    getterCode = fun args -> <@@ DirectoryInfo(currentFolderFullName) @@>
                )

            let toStringMethod =
                ProvidedMethod(
                    "ToString",
                    [],
                    typeof<string>,
                    isStatic = true,
                    invokeCode = fun args -> <@@ currentFolderFullName @@>
                )

            let getInfoMethod =
                ProvidedMethod(
                    "GetInfo",
                    [],
                    typeof<DirectoryInfo>,
                    isStatic = true,
                    invokeCode = fun args -> <@@ DirectoryInfo(currentFolderFullName) @@>
                )

            let xmlDocText = $"Get the full path to '{currentFolderFullName}'"

            currentFolderProperty.AddXmlDoc xmlDocText
            toStringMethod.AddXmlDoc xmlDocText
            getInfoMethod.AddXmlDoc xmlDocText

            rootType.AddMember currentFolderProperty
            rootType.AddMember toStringMethod
            rootType.AddMember getInfoMethod

        // Add parent directory if we have one
        match inode.Parent with
        | Some parent ->
            rootType.AddMemberDelayed(fun () ->
                let directoryType =
                    ProvidedTypeDefinition("..", Some typeof<obj>, hideObjectMethods = true)

                createInodeProperties parent directoryInfo.Parent.FullName directoryType
                directoryType
            )

        | None -> ()

        inode.Children
        |> Seq.iter (fun inode ->
            if inode.IsFile then
                createFileLiteral inode directoryInfo.FullName rootType
            else
                let folderType =
                    ProvidedTypeDefinition(inode.Name, Some typeof<obj>, hideObjectMethods = true)

                folderType.AddXmlDoc $"Interface representing folder '{directoryInfo.FullName}'"

                createInodeProperties inode directoryInfo.FullName folderType
                rootType.AddMember folderType
        )

    let make
        (typ: ProvidedTypeDefinition)
        (rootDirectory: DirectoryInfo)
        (rootNode: Parser.INode) = typ.AddMemberDelayed <| fun () ->
        let virtualProvider = ProvidedTypeDefinition("VirtualFileSystem", Some typeof<obj>, hideObjectMethods = true)
        createInodeProperties rootNode rootDirectory.FullName virtualProvider
        virtualProvider.AddXmlDoc "Interface representing a virtual file provider"
        virtualProvider

module private FileSystemProvider =
    module FileSystem =
        let private createFileLiterals
            (directoryInfo: DirectoryInfo)
            (rootType: ProvidedTypeDefinition)
            =

            for file in tryEnumerate directoryInfo.EnumerateFiles do
                let adjustedFieldPath = file.FullName

                let pathFieldProperty =
                    ProvidedProperty(
                        file.Name,
                        typeof<FileInfo>,
                        isStatic = true,
                        getterCode = fun args -> <@@ FileInfo(adjustedFieldPath) @@>
                    )

                pathFieldProperty.setXmlDoc {
                    summary {
                        c { "System.IO.FileInfo" }
                        "for"
                        squote { file.FullName }
                    }
                }
                rootType.AddMember pathFieldProperty

        let rec createDirectoryProperties
            (directoryInfo: DirectoryInfo)
            (rootType: ProvidedTypeDefinition)
            =

            // Extract the full path in a variable so we can use it in the ToString method
            let currentFolderFullName = directoryInfo.FullName

            let currentFolderProperty =
                ProvidedProperty(
                    ".",
                    typeof<DirectoryInfo>,
                    isStatic = true,
                    getterCode = fun args -> <@@ DirectoryInfo(currentFolderFullName) @@>
                )

            let toStringMethod =
                ProvidedMethod(
                    "ToString",
                    [],
                    typeof<string>,
                    isStatic = true,
                    invokeCode = fun args -> <@@ currentFolderFullName @@>
                )

            let getInfoMethod =
                ProvidedMethod(
                    "GetInfo",
                    [],
                    typeof<DirectoryInfo>,
                    isStatic = true,
                    invokeCode = fun args -> <@@ DirectoryInfo(currentFolderFullName) @@>
                )

            let xmlDocText = $"Get the full path to '{currentFolderFullName}'"

            let xmlDocInfoText =
                summary {
                    "Get the"
                    c { "System.IO.DirectoryInfo" }
                    "to"
                    squote { currentFolderFullName }
                }

            currentFolderProperty.AddXmlDoc xmlDocInfoText
            getInfoMethod.AddXmlDoc xmlDocInfoText
            toStringMethod.AddXmlDoc xmlDocText

            rootType.AddMember currentFolderProperty
            rootType.AddMember toStringMethod
            rootType.AddMember getInfoMethod
            createFileLiterals directoryInfo rootType

            // Add parent directory, unless this is a drive root and there is none
            if not (isNull (box directoryInfo.Parent)) then
                rootType.AddMemberDelayed(fun () ->
                    let directoryType =
                        ProvidedTypeDefinition("..", Some typeof<obj>, hideObjectMethods = true)

                    directoryType.AddXmlDoc $"Interface representing directory '{directoryInfo.FullName}'"

                    createDirectoryProperties directoryInfo.Parent directoryType
                    directoryType
                )

            for folder in tryEnumerate directoryInfo.EnumerateDirectories do
                // Build the folder member on demand as we can have a lot of folders/files
                rootType.AddMemberDelayed(fun () ->
                    let folderType =
                        ProvidedTypeDefinition(folder.Name, Some typeof<obj>, hideObjectMethods = true)

                    folderType.AddXmlDoc $"Interface representing folder '{folder.FullName}'"

                    // Walk through the folder
                    createDirectoryProperties folder folderType
                    // Store the folder type in the member
                    folderType
                )

        let private watchDir (directoryInfo: DirectoryInfo) =
            let watcher = new FileSystemWatcher(directoryInfo.FullName)
            watcher.EnableRaisingEvents <- true

            watcher

    let make (typ: ProvidedTypeDefinition) (root: DirectoryInfo) = typ.AddMemberDelayed <| fun () ->
        let fileProvider = ProvidedTypeDefinition("FileSystem", Some typeof<obj>, hideObjectMethods = true)
        fileProvider.AddXmlDoc "Interface representing a file provider"
        FileSystem.createDirectoryProperties root fileProvider
        fileProvider
