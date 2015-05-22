﻿module internal Nessos.Vagabond.Control

open System
open System.IO
open System.Reflection

open Microsoft.FSharp.Control

open Nessos.FsPickler
open Nessos.FsPickler.Hashing

open Nessos.Vagabond
open Nessos.Vagabond.Utils
open Nessos.Vagabond.SliceCompiler
open Nessos.Vagabond.SliceCompilerTypes
open Nessos.Vagabond.Serialization
open Nessos.Vagabond.AssemblyManagementTypes
open Nessos.Vagabond.AssemblyCache
open Nessos.Vagabond.AssemblyManagement

type VagabondMessage =
    | ImportAssemblies of IAssemblyImporter * AssemblyId list * ReplyChannel<VagabondAssembly list>
    | ExportAssemblies of IAssemblyExporter * VagabondAssembly list * ReplyChannel<unit>
    | LoadAssembly of AssemblyLoadPolicy * VagabondAssembly * ReplyChannel<AssemblyLoadInfo>
    | GetVagabondAssembly of AssemblyLoadPolicy * AssemblyId * ReplyChannel<VagabondAssembly>
    | GetAssemblyLoadInfo of AssemblyLoadPolicy * AssemblyId * ReplyChannel<AssemblyLoadInfo>
    | CompileDynamicAssemblySlice of Assembly list * ReplyChannel<DynamicAssemblySlice list>
    | RegisterNativeDependency of VagabondAssembly * ReplyChannel<unit>
    | GetRegisteredNativeDependencies of ReplyChannel<VagabondAssembly list>
    | GetStaticBindings of ReplyChannel<(FieldInfo * HashResult) []>

/// A mailboxprocessor wrapper for handling vagabond state
type VagabondController (uuid : Guid, cacheDirectory : string, profiles : IDynamicAssemblyProfile list, requireLoaded : bool, 
                            compressDataFiles : bool, dataPersistThreshold : int64, isIgnoredAssembly : Assembly -> bool, ?tyConv : ITypeNameConverter) =

    do 
        if not <| Directory.Exists cacheDirectory then
            raise <| new DirectoryNotFoundException(cacheDirectory)

    let compilerState = ref <| initCompilerState uuid profiles cacheDirectory

    let typeNameConverter = mkTypeNameConverter tyConv (fun () -> compilerState.Value)

    let serializer = FsPickler.CreateBinary(typeConverter = typeNameConverter)

    let assemblyCache = new AssemblyCache(cacheDirectory, serializer)
    let nativeAssemblyManager = new NativeAssemblyManager(cacheDirectory)

    let initState =
        {
            CompressDataFiles = compressDataFiles
            DataPersistThreshold = dataPersistThreshold

            CompilerState = !compilerState
            DataExportState = Map.empty
            DataImportState = Map.empty

            AssemblyExportState = Map.empty
            AssemblyImportState = Map.empty
            StaticBindings = [||]

            NativeAssemblyManager = nativeAssemblyManager

            Serializer = serializer
            AssemblyCache = assemblyCache
        }
        
    let processMessage (state : VagabondState) (message : VagabondMessage) = async {

        match message with
        | ImportAssemblies(importer, ids, rc) ->
            try
                let! vas =
                    ids
                    |> Seq.distinct
                    |> Seq.map(fun id -> state.AssemblyCache.Import(importer, id))
                    |> Async.Parallel

                rc.Reply(Array.toList vas)
                return state

            with e ->
                rc.ReplyWithError e
                return state

        | ExportAssemblies(exporter, assemblies, rc) ->
            try
                return!
                    assemblies
                    |> Seq.distinctBy(fun va -> va.Id)
                    |> Seq.map (fun va -> state.AssemblyCache.Export(exporter, va))
                    |> Async.Parallel
                    |> Async.Ignore

                rc.Reply (())
                return state

            with e ->
                rc.ReplyWithError e
                return state

        | CompileDynamicAssemblySlice (assemblies, rc) ->
            try
                let compState, result = compileDynamicAssemblySlices isIgnoredAssembly requireLoaded state.CompilerState assemblies

                // note: it is essential that the compiler state ref cell is updated *before*
                // a reply is given; this is to eliminate a certain class of race conditions.
                compilerState := compState
                do rc.Reply result
                return { state with CompilerState = compState }

            with e ->
                rc.ReplyWithError e
                return state

        | GetVagabondAssembly (policy, id, rc) ->
            try
                let state', va = exportAssembly state policy id
                rc.Reply va
                return state'

            with e ->
                rc.ReplyWithError e
                return state

        | LoadAssembly (policy, va, rc) ->
            try
                let state', result = loadAssembly state policy va
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
                let loadInfo = state.NativeAssemblyManager.Load assembly
                rc.Reply (())
            with e ->
                rc.ReplyWithError e

            return state

        | GetRegisteredNativeDependencies rc ->
            rc.Reply state.NativeAssemblyManager.LoadedNativeAssemblies
            return state

        | GetStaticBindings rc ->
            rc.Reply state.StaticBindings
            return state
    }

    let cts = new System.Threading.CancellationTokenSource()
    let actor = MailboxProxessor.Stateful (initState, processMessage, ct = cts.Token)

    member __.Start() = actor.Start()
    member __.Stop() = cts.Cancel()

    member __.CompilerState = !compilerState
    member __.CacheDirectory = assemblyCache.CacheDirectory
    member __.AssemblyCache = assemblyCache

    member __.Serializer = serializer
    member __.TypeNameConverter = typeNameConverter

    member __.PostAndAsyncReply msgB = actor.PostAndAsyncReply msgB
    member __.PostAndReply msgB = actor.PostAndReply msgB
    member __.NativeDependencies = actor.PostAndReply GetRegisteredNativeDependencies
    member __.StaticBindings = actor.PostAndReply GetStaticBindings