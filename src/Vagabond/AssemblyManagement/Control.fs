﻿module internal MBrace.Vagabond.Control

open System
open System.IO
open System.Reflection

open Microsoft.FSharp.Control

open MBrace.FsPickler
open MBrace.FsPickler.Hashing

open MBrace.Vagabond
open MBrace.Vagabond.Utils
open MBrace.Vagabond.SliceCompiler
open MBrace.Vagabond.SliceCompilerTypes
open MBrace.Vagabond.Serialization
open MBrace.Vagabond.AssemblyManagementTypes
open MBrace.Vagabond.AssemblyCache
open MBrace.Vagabond.AssemblyManagement

type VagabondMessage =
    | ImportAssemblies of IAssemblyDownloader * AssemblyId [] * ReplyChannel<VagabondAssembly []>
    | ExportAssemblies of IAssemblyUploader * VagabondAssembly [] * ReplyChannel<unit>
    | LoadAssembly of AssemblyLookupPolicy * VagabondAssembly * ReplyChannel<AssemblyLoadInfo>
    | TryGetVagabondAssembly of AssemblyLookupPolicy * AssemblyId * ReplyChannel<VagabondAssembly option>
    | GetAssemblyLoadInfo of AssemblyLookupPolicy * AssemblyId * ReplyChannel<AssemblyLoadInfo>
    | CompileAssemblies of Assembly [] * ReplyChannel<Assembly []>
    | RegisterNativeDependency of VagabondAssembly * ReplyChannel<unit>

/// A mailboxprocessor wrapper for handling vagabond state
type VagabondController (uuid : Guid, config : VagabondConfiguration) =

    do 
        if not <| Directory.Exists config.CacheDirectory then
            raise <| new DirectoryNotFoundException(config.CacheDirectory)

    let nativeAssemblies : VagabondAssembly [] ref = ref [||]
    let staticBindings : (FieldInfo * HashResult) [] ref = ref [||]
    let compilerState = ref <| initCompilerState uuid config.DynamicAssemblyProfiles config.CacheDirectory
    let mutable actor = Unchecked.defaultof<MailboxProcessor<VagabondMessage>> // to be initialized at the end of the constructor body

    let serializationCompilerCallback =
        if config.ForceSerializationSliceCompilation then
            Some(fun a -> ignore <| actor.PostAndReply(fun ch -> CompileAssemblies([|a|], ch)))
        else
            None

    let typeNameConverter = mkTypeNameConverter config.ForceLocalFSharpCoreAssembly config.TypeConverter 
                                                (fun () -> compilerState.Value) serializationCompilerCallback

    let serializer = FsPickler.CreateBinarySerializer(typeConverter = typeNameConverter)
    let assemblyCache = new AssemblyCache(config.CacheDirectory, serializer)
    let nativeAssemblyManager = new NativeAssemblyManager(config.CacheDirectory)

    let initState =
        {
            Configuration = config

            CompilerState = !compilerState
            AssemblyLoadState = Map.empty
            StaticBindings = [||]

            NativeAssemblyManager = nativeAssemblyManager

            Serializer = serializer
            AssemblyCache = assemblyCache
        }
        
    let processMessage (state : VagabondState) (message : VagabondMessage) = async {

        match message with
        | ImportAssemblies(importer, ids, rc) ->
            try
                let state', loadInfo = getAssemblyLoadInfos state AssemblyLookupPolicy.ResolveRuntimeStrongNames ids
                let shouldDownload (loadInfo : AssemblyLoadInfo) =
                    match loadInfo with
                    | Loaded(id, _, metadata : VagabondMetadata) ->
                        // only donwload assembly slices if remotely generated
                        if metadata.IsDynamicAssemblySlice then not <| state'.CompilerState.IsLocalDynamicAssemblySlice id
                        else
                            false

                    | NotLoaded _ | LoadFault _ -> true

                let requiredDownloads = 
                    loadInfo 
                    |> Seq.filter shouldDownload
                    |> Seq.map (fun li -> li.Id)
                    |> Set.ofSeq

                let! vas =
                    ids
                    |> Seq.distinct
                    |> Seq.filter requiredDownloads.Contains
                    |> Seq.map(fun id -> state.AssemblyCache.Download(importer, id))
                    |> Async.Parallel

                rc.Reply vas
                return state

            with e ->
                rc.ReplyWithError e
                return state

        | ExportAssemblies(exporter, assemblies, rc) ->
            try
                return!
                    assemblies
                    |> Seq.distinctBy(fun va -> va.Id)
                    |> Seq.map (fun va -> state.AssemblyCache.Upload(exporter, va))
                    |> Async.Parallel
                    |> Async.Ignore

                rc.Reply (())
                return state

            with e ->
                rc.ReplyWithError e
                return state

        | CompileAssemblies (assemblies, rc) ->
            try
                let compState, result = compileAssemblies state.CompilerState (Array.toList assemblies)

                // note: it is essential that the compiler state ref cell is updated *before*
                // a reply is given; this is to eliminate a certain class of race conditions.
                compilerState := compState
                do rc.Reply (result |> Exn.map Array.ofList)
                return { state with CompilerState = compState }

            with e ->
                rc.ReplyWithError e
                return state

        | TryGetVagabondAssembly (policy, id, rc) ->
            try
                let state', va = tryExportAssembly state policy id
                staticBindings := state'.StaticBindings
                rc.Reply va
                return state'

            with e ->
                rc.ReplyWithError e
                return state

        | LoadAssembly (policy, va, rc) ->
            try
                let state', result = loadAssembly state policy va
                staticBindings := state'.StaticBindings
                nativeAssemblies := state.NativeAssemblyManager.LoadedNativeAssemblies
                rc.Reply result
                return state'

            with e ->
                rc.ReplyWithError e
                return state

        | GetAssemblyLoadInfo (policy, id, rc) ->
            try
                let state', result = getAssemblyLoadInfo state policy id
                rc.Reply result
                return state'

            with e ->
                rc.ReplyWithError e
                return state

        | RegisterNativeDependency(assembly, rc) ->
            try
                let _ = state.NativeAssemblyManager.Load assembly
                nativeAssemblies := state.NativeAssemblyManager.LoadedNativeAssemblies
                rc.Reply (())
            with e ->
                rc.ReplyWithError e

            return state
    }

    let cts = new System.Threading.CancellationTokenSource()
    do actor <- MailboxProxessor.Stateful (initState, processMessage, ct = cts.Token)

    member __.Start() = actor.Start()
    member __.Stop() = cts.Cancel()

    member __.CompilerState = !compilerState
    member __.CacheDirectory = assemblyCache.CacheDirectory
    member __.AssemblyCache = assemblyCache

    member __.Serializer = serializer
    member __.TypeNameConverter = typeNameConverter

    member __.PostAndAsyncReply msgB = actor.PostAndAsyncReply msgB
    member __.PostAndReply msgB = actor.PostAndReply msgB
    member __.NativeDependencies = !nativeAssemblies
    member __.StaticBindings = !staticBindings