﻿(**
A Vagabond Demo: ThunkServer

This script offers a demo of the functionality of Vagabond, through an ad-hoc application.
ThunkServer, as its name suggests, is a server that receives and executes arbitrary thunks,
that is functions of type unit -> 'T. ThunkServer uses the Vagabond library to correctly
resolve and submit code dependencies, even if those happen to be dynamic assemblies as
is the case with F# Interactive.

The actual implementation of ThunkServer is a straightforward 100 lines of code.
Dependency resolution and exportation logic is handled transparently by Vagabond
**)

#I "../../bin/"

#r "FsPickler.dll"
#r "Vagabond.Tests.exe"

open Nessos.Vagabond.Tests

// initialize & test a local instance
ThunkClient.Executable <- __SOURCE_DIRECTORY__ + "/../../bin/Vagabond.Tests.exe"
let client = ThunkClient.InitLocal()

(**
Example 1: simple, incremental interactions
**)

let askDeepThought () = 42

let answer = client.EvaluateThunk askDeepThought

client.EvaluateThunk (fun () -> if answer = 42 then failwith "yet another mindless hitchhiker reference")

(**
Example 2: custom type definitions
**)

type BinTree<'T> = Leaf | Node of 'T * BinTree<'T> * BinTree<'T>

let rec init = function 0 -> Leaf | n -> Node(n, init (n-1), init (n-1))
let rec map f = function Leaf -> Leaf | Node(a,l,r) -> Node(f a, map f l, map f r)
let rec reduce id f = function Leaf -> id | Node(a,l,r) -> f (f (reduce id f l) a) (reduce id f r)

let tree = client.EvaluateThunk <| fun () -> init 5

let tree' = client.EvaluateThunk <| fun () -> map (fun x -> 2. ** float x) tree

let sum = client.EvaluateThunk <| fun () -> reduce 1. (+) tree'

(**
Example 3: Type providers
**)

#r "../../packages/FSharp.Data.2.0.9/lib/net40/FSharp.Data.dll"

open FSharp.Data

// World Bank

let wb = WorldBankData.GetDataContext()

let top5 () = 
    query {
        for country in wb.Regions.``Euro area``.Countries do
        sortByDescending country.Indicators.``GDP per capita (current US$)``.[2012]
        take 5
        select country.Name
    } |> Seq.toList

client.EvaluateThunk top5

// FreeBase

let fb = FSharp.Data.FreebaseData.GetDataContext()

let displaySussman () =
    let sussman = fb.``Science and Technology``.Computers.``Computer Scientists``.Individuals.``Gerald Jay Sussman``
    for line in sussman.Blurb do printfn "%s" line

client.EvaluateThunk displaySussman
        

(**
Example 4 : Asynchronous workflows
**)

let runRemoteAsync (workflow : Async<'T>) =
    client.EvaluateThunk(fun () -> Async.RunSynchronously workflow)

let test = async {
    
    let printfn fmt = Printf.ksprintf System.Console.WriteLine fmt
    let workflow i = async { if i = 7 then invalidOp "error" else printfn "Running task %d.." i }

    try
        let! results = [1..10] |> List.map workflow |> Async.Parallel
        return None

    with :? System.InvalidOperationException as e ->
        return Some "error"
}

runRemoteAsync test

(**
Example 5: Deploy a locally defined actor
**)

#r "Thespian.dll"
open Nessos.Thespian

let deployRemoteActor (behaviour : Actor<'T> -> Async<unit>) : ActorRef<'T> = 
    client.EvaluateThunk(fun () ->
        printfn "deploying actor..."
        let actor = Actor.bind behaviour |> Actor.Publish
        actor.Ref)

type Counter =
    | Increment of int
    | GetCount of IReplyChannel<int>

let rec loop state (self : Actor<Counter>) =
    async {
        let! msg = self.Receive ()
        match msg with
        | Increment i -> 
            printfn "Increment by %d" i
            return! loop (i + state) self
        | GetCount rc ->
            do! rc.Reply state
            return! loop state self
    }

let ref = deployRemoteActor (loop 0)

ref <-- Increment 1
ref <-- Increment 2
ref <-- Increment 3
ref <!= GetCount

(*
Example 6: Deploy library-generated dynamic assemblies
*)

#I "../../packages/LinqOptimizer.FSharp.0.6.3/lib/"

#r "LinqOptimizer.Base.dll"
#r "LinqOptimizer.Core.dll"
#r "LinqOptimizer.FSharp.dll"

open Nessos.LinqOptimizer.FSharp

let nums = [|1..100000|]

let query = 
    nums
    |> Query.ofSeq
    |> Query.filter (fun num -> num % 2 = 0)
    |> Query.map (fun num -> num * num)
    |> Query.sum
    |> Query.compile

query ()

client.EvaluateThunk query