﻿module Fable.Remoting.MsgPack.Read

open System
open System.Text
open System.Collections
open System.Collections.Concurrent
open System.Collections.Generic
open FSharp.Reflection
open System.Reflection

let interpretStringAs (typ: Type) (str: string) =
#if FABLE_COMPILER
    box str
#else
    if typ = typeof<string> then
        box str
    elif typ = typeof<char> then
        box str.[0]
    else
        // todo cacheable
        // String enum
        let case = FSharpType.GetUnionCases (typ, true) |> Array.find (fun y -> y.Name = str)
        FSharpValue.MakeUnion (case, [||], true)
#endif

let inline interpretIntegerAs typ n =
    if typ = typeof<Int32> then int32 n |> box
    elif typ = typeof<Int64> then int64 n |> box
    elif typ = typeof<Int16> then int16 n |> box
    elif typ = typeof<UInt32> then uint32 n |> box
    elif typ = typeof<UInt64> then uint64 n |> box
    elif typ = typeof<UInt16> then uint16 n |> box
    elif typ = typeof<TimeSpan> then TimeSpan (int64 n) |> box
#if FABLE_COMPILER
    elif typ.FullName = "Microsoft.FSharp.Core.int16`1" then int16 n |> box
    elif typ.FullName = "Microsoft.FSharp.Core.int32`1" then int32 n |> box
    elif typ.FullName = "Microsoft.FSharp.Core.int64`1" then int64 n |> box
#endif
    elif typ = typeof<byte> then byte n |> box
    elif typ = typeof<sbyte> then sbyte n |> box
#if !FABLE_COMPILER
    elif typ.IsEnum then Enum.ToObject (typ, int64 n)
#else
    elif typ.IsEnum then float n |> box
#endif
    else failwithf "Cannot interpret integer %A as %s." n typ.Name

let inline interpretFloatAs typ n =
    if typ = typeof<float32> then float32 n |> box
    elif typ = typeof<float> then float n |> box
#if FABLE_COMPILER
    elif typ.FullName = "Microsoft.FSharp.Core.float32`1" then float32 n |> box
    elif typ.FullName = "Microsoft.FSharp.Core.float`1" then float n |> box
#endif
    else failwithf "Cannot interpret float %A as %s." n typ.Name

#if !FABLE_COMPILER
type DictionaryDeserializer<'k,'v when 'k: equality and 'k: comparison> () =
    static let keyType = typeof<'k>
    static let valueType = typeof<'v>

    static member Deserialize (len: int, isDictionary, read: Type -> obj) =
        if isDictionary then
            let dict = Dictionary<'k, 'v> (len)

            for _ in 0 .. len - 1 do
                dict.Add (read keyType :?> 'k, read valueType :?> 'v)

            box dict
        else
            Array.init len (fun _ -> read keyType :?> 'k, read valueType :?> 'v)
            |> Map.ofArray
            |> box

type ListDeserializer<'a> () =
    static let argType = typeof<'a>

    static member Deserialize (len: int, read: Type -> obj) =
        List.init len (fun _ -> read argType :?> 'a)
        |> box

type SetDeserializer<'a when 'a : comparison> () =
    static let argType = typeof<'a>

    static member Deserialize (len: int, read: Type -> obj) =
        let mutable set = Set.empty

        for _ in 0 .. len - 1 do
            set <- set.Add (read argType :?> 'a)

        set
        |> box
#endif

type Reader (data: byte[]) =
    let mutable pos = 0

#if !FABLE_COMPILER
    static let arrayReaderCache = ConcurrentDictionary<Type, (int * Reader) -> obj> ()
    static let mapReaderCache = ConcurrentDictionary<Type, (int * Reader) -> obj> ()
    static let setReaderCache = ConcurrentDictionary<Type, (int * Reader) -> obj> ()
    static let unionConstructorCache = ConcurrentDictionary<UnionCaseInfo, obj [] -> obj> ()
    static let unionCaseFieldCache = ConcurrentDictionary<Type * int, UnionCaseInfo * Type[]> ()
#endif

    let readInt len m =
#if !FABLE_COMPILER
        if BitConverter.IsLittleEndian then
            Array.Reverse (data, pos, len)

        pos <- pos + len
        m (data, pos - len)
#else
        if BitConverter.IsLittleEndian then
            let arr = Array.zeroCreate len

            for i in 0 .. len - 1 do
                arr.[i] <- data.[pos + len - 1 - i]

            pos <- pos + len
            m (arr, 0)
        else
            pos <- pos + len
            m (data, pos - len)
#endif

    member _.ReadByte () =
        pos <- pos + 1
        data.[pos - 1]

    member _.ReadRawBin len =
        pos <- pos + len
        data.[ pos - len .. pos - 1 ]

    member _.ReadString len =
        pos <- pos + len
        Encoding.UTF8.GetString (data, pos - len, len)

    member x.ReadUInt8 () =
        x.ReadByte ()

    member x.ReadInt8 () =
        x.ReadByte () |> sbyte

    member _.ReadUInt16 () =
        readInt 2 BitConverter.ToUInt16

    member _.ReadInt16 () =
        readInt 2 BitConverter.ToInt16

    member _.ReadUInt32 () =
        readInt 4 BitConverter.ToUInt32

    member _.ReadInt32 () =
        readInt 4 BitConverter.ToInt32

    member _.ReadUInt64 () =
        readInt 8 BitConverter.ToUInt64

    member _.ReadInt64 () =
        readInt 8 BitConverter.ToInt64

    member _.ReadFloat32 () =
        readInt 4 BitConverter.ToSingle

    member _.ReadFloat64 () =
        readInt 8 BitConverter.ToDouble

    member x.ReadMap (len: int, t: Type) =
#if !FABLE_COMPILER
        mapReaderCache.GetOrAdd (t, Func<_, _>(fun (t: Type) ->
            let args = t.GetGenericArguments ()

            if args.Length <> 2 then
                failwithf "Expecting %s, but the data contains a map." t.Name

            let mapDeserializer = typedefof<DictionaryDeserializer<_,_>>.MakeGenericType args
            let isDictionary = t.GetGenericTypeDefinition () = typedefof<Dictionary<_, _>>
            let d = Delegate.CreateDelegate (typeof<Func<int, bool, (Type -> obj), obj>>, mapDeserializer.GetMethod "Deserialize") :?> Func<int, bool, (Type -> obj), obj>

            fun (len, x: Reader) -> d.Invoke (len, isDictionary, x.Read))) (len, x)
#else
        let args = t.GetGenericArguments ()

        if args.Length <> 2 then
            failwithf "Expecting %s, but the data contains a map." t.Name

        let pairs =
            let arr = Array.zeroCreate len

            for i in 0 .. len - 1 do
                arr.[i] <- x.Read args.[0] |> box :?> IStructuralComparable, x.Read args.[1]

            arr

        if t.GetGenericTypeDefinition () = typedefof<Dictionary<_, _>> then
            let dict = Dictionary<_, _> len
            pairs |> Array.iter dict.Add
            box dict
        else
            Map.ofArray pairs |> box
#endif

    member x.ReadSet (len: int, t: Type) =
#if !FABLE_COMPILER
        setReaderCache.GetOrAdd (t, Func<_, _>(fun (t: Type) ->
            let args = t.GetGenericArguments ()

            if args.Length <> 1 then
                failwithf "Expecting %s, but the data contains a set." t.Name

            let setDeserializer = typedefof<SetDeserializer<_>>.MakeGenericType args
            let d = Delegate.CreateDelegate (typeof<Func<int, (Type -> obj), obj>>, setDeserializer.GetMethod "Deserialize") :?> Func<int, (Type -> obj), obj>

            fun (len, x: Reader) -> d.Invoke (len, x.Read))) (len, x)
#else
        let args = t.GetGenericArguments ()

        if args.Length <> 1 then
            failwithf "Expecting %s, but the data contains a set." t.Name

        let mutable set = Set.empty

        for _ in 0 .. len - 1 do
            set <- set.Add(x.Read args.[0] |> box :?> IStructuralComparable)

        box set
#endif

    member x.ReadRawArray (len: int, elementType: Type) =
#if !FABLE_COMPILER
        let arr = Array.CreateInstance (elementType, len)

        for i in 0 .. len - 1 do
            arr.SetValue (x.Read elementType, i)

        arr
#else
        let arr = Array.zeroCreate len

        for i in 0 .. len - 1 do
            arr.[i] <- x.Read elementType

        arr
#endif

    member x.ReadArray (len, t) =
#if !FABLE_COMPILER
        match arrayReaderCache.TryGetValue t with
        | true, reader ->
            reader (len, x)
        | _ ->
#endif

        if FSharpType.IsRecord t then
#if !FABLE_COMPILER
            let fieldTypes = FSharpType.GetRecordFields t |> Array.map (fun prop -> prop.PropertyType)
            let ctor = FSharpValue.PreComputeRecordConstructor (t, true)

            arrayReaderCache.GetOrAdd (t, fun (_, x: Reader) ->
                ctor (fieldTypes |> Array.map x.Read)) (len, x)
#else
            let props = FSharpType.GetRecordFields t
            FSharpValue.MakeRecord (t, props |> Array.map (fun prop -> x.Read prop.PropertyType))
#endif
        elif FSharpType.IsUnion (t, true) then
#if !FABLE_COMPILER
            if t.IsGenericType && t.GetGenericTypeDefinition () = typedefof<_ list> then
                let argType = t.GetGenericArguments () |> Array.head
                let listDeserializer = typedefof<ListDeserializer<_>>.MakeGenericType argType
                let d = Delegate.CreateDelegate (typeof<Func<int, (Type -> obj), obj>>, listDeserializer.GetMethod "Deserialize") :?> Func<int, (Type -> obj), obj>

                arrayReaderCache.GetOrAdd (t, fun (len, (x: Reader)) -> d.Invoke (len, x.Read)) (len, x)
            else
                arrayReaderCache.GetOrAdd (t, fun (_, x: Reader) ->
                    let tag = x.Read typeof<int> :?> int
                    let case, fieldTypes =
                        unionCaseFieldCache.GetOrAdd ((t, tag), fun (t, tag) ->
                            let case = FSharpType.GetUnionCases (t, true) |> Array.find (fun x -> x.Tag = tag)
                            let fields = case.GetFields ()
                            case, fields |> Array.map (fun x -> x.PropertyType))

                    let fields =
                        // single parameter is serialized directly, not in an array, saving 1 byte on the array format
                        if fieldTypes.Length = 1 then
                            [| x.Read fieldTypes.[0] |]
                        else
                            // don't care about this byte, it's going to be a fixarr of length fieldTypes.Length
                            x.ReadByte () |> ignore
                            fieldTypes |> Array.map x.Read

                    unionConstructorCache.GetOrAdd (case, Func<_, _>(fun case -> FSharpValue.PreComputeUnionConstructor (case, true))) fields) (len, x)
#else
            let tag = x.Read typeof<int> :?> int
            let case = FSharpType.GetUnionCases (t, true) |> Array.find (fun x -> x.Tag = tag)
            let fieldTypes = case.GetFields () |> Array.map (fun x -> x.PropertyType)

            let fields =
                // single parameter is serialized directly, not in an array, saving 1 byte on the array format
                if fieldTypes.Length = 1 then
                    [| x.Read fieldTypes.[0] |]
                else
                    // don't care about this byte, it's going to be a fixarr of length fieldTypes.Length
                    x.ReadByte () |> ignore
                    fieldTypes |> Array.map x.Read

            FSharpValue.MakeUnion (case, fields, true)
#endif

#if FABLE_COMPILER // Fable does not recognize Option as a union
        elif t.IsGenericType && t.GetGenericTypeDefinition () = typedefof<Option<_>> then
            let tag = x.ReadByte ()

            // none case
            if tag = 0uy then
                x.ReadByte () |> ignore
                box null
            else
                x.Read (t.GetGenericArguments () |> Array.head) |> Some |> box
        elif t.IsGenericType && t.GetGenericTypeDefinition () = typedefof<_ list> then
            let elementType = t.GetGenericArguments () |> Array.head
            [
                for _ in 0 .. len - 1 ->
                    x.Read elementType
            ] |> box
#endif
        elif t.IsArray then
            x.ReadRawArray (len, t.GetElementType ()) |> box
        elif FSharpType.IsTuple t then
#if !FABLE_COMPILER
            let elementTypes = FSharpType.GetTupleElements t
            let tupleCtor = FSharpValue.PreComputeTupleConstructor t
            arrayReaderCache.GetOrAdd (t, fun (_, (x: Reader)) -> elementTypes |> Array.map x.Read |> tupleCtor) (len, x)
#else
            FSharpValue.MakeTuple (FSharpType.GetTupleElements t |> Array.map x.Read, t)
#endif
        elif t = typeof<DateTime> then
            let dateTimeTicks = x.Read typeof<int64> :?> int64
            let kindAsInt = x.Read typeof<int64> :?> int64
            let kind =
                match kindAsInt with
                | 1L -> DateTimeKind.Utc
                | 2L -> DateTimeKind.Local
                | _ -> DateTimeKind.Unspecified
            DateTime(ticks=dateTimeTicks, kind=kind) |> box
        elif t = typeof<DateTimeOffset> then
            let dateTimeTicks = x.Read typeof<int64> :?> int64
            let timeSpanMinutes = x.Read typeof<int16> :?> int16
            DateTimeOffset (dateTimeTicks, TimeSpan.FromMinutes (float timeSpanMinutes)) |> box

        elif t.IsGenericType && t.GetGenericTypeDefinition () = typedefof<Set<_>> then
            x.ReadSet(len, t)
#if !FABLE_COMPILER
        elif t = typeof<System.Data.DataTable> then
            match x.ReadRawArray(2, typeof<string>) :?> string array with
            | [|schema;data|] ->
                let t = new System.Data.DataTable()
                t.ReadXmlSchema(new System.IO.StringReader(schema))
                t.ReadXml(new System.IO.StringReader(data)) |> ignore
                box t
            | otherwise -> failwithf "Expecting %s at position %d, but the data contains an array." t.Name pos
        elif t = typeof<System.Data.DataSet> then
            match x.ReadRawArray(2, typeof<string>) :?> string array with
            | [|schema;data|] ->
                let t = new System.Data.DataSet()
                t.ReadXmlSchema(new System.IO.StringReader(schema))
                t.ReadXml(new System.IO.StringReader(data)) |> ignore
                box t
            | otherwise -> failwithf "Expecting %s at position %d, but the data contains an array." t.Name pos
#endif
        elif t = typeof<decimal> || t.FullName = "Microsoft.FSharp.Core.decimal`1" then
#if !FABLE_COMPILER
            arrayReaderCache.GetOrAdd (t, fun (_, (x: Reader)) -> x.ReadRawArray (4, typeof<int>) :?> int[] |> Decimal |> box) (len, x)
#else
            x.ReadRawArray (4, typeof<int>) |> box :?> int[] |> Decimal |> box
#endif
        else
            failwithf "Expecting %s at position %d, but the data contains an array." t.Name pos

    member x.ReadBin (len, t) =
        if t = typeof<Guid> then
            x.ReadRawBin len |> Guid |> box
        elif t = typeof<byte[]> then
            x.ReadRawBin len |> box
        elif t = typeof<bigint> then
            x.ReadRawBin len |> bigint |> box

        else
            failwithf "Expecting %s at position %d, but the data contains bin." t.Name pos

    member x.Read t =
        match x.ReadByte () with
        // fixstr
        | b when b ||| 0b00011111uy = 0b10111111uy -> b &&& 0b00011111uy |> int |> x.ReadString |> interpretStringAs t
        | Format.Str8 -> x.ReadByte () |> int |> x.ReadString |> interpretStringAs t
        | Format.Str16 -> x.ReadUInt16 () |> int |> x.ReadString |> interpretStringAs t
        | Format.Str32 -> x.ReadUInt32 () |> int |> x.ReadString |> interpretStringAs t
        // fixposnum
        | b when b ||| 0b01111111uy = 0b01111111uy -> interpretIntegerAs t b
        // fixnegnum
        | b when b ||| 0b00011111uy = 0b11111111uy -> sbyte b |> interpretIntegerAs t
        | Format.Int64 -> x.ReadInt64 () |> interpretIntegerAs t
        | Format.Int32 -> x.ReadInt32 () |> interpretIntegerAs t
        | Format.Int16 -> x.ReadInt16 () |> interpretIntegerAs t
        | Format.Int8 -> x.ReadInt8 () |> interpretIntegerAs t
        | Format.Uint8 -> x.ReadUInt8 () |> interpretIntegerAs t
        | Format.Uint16 -> x.ReadUInt16 () |> interpretIntegerAs t
        | Format.Uint32 -> x.ReadUInt32 () |> interpretIntegerAs t
        | Format.Uint64 -> x.ReadUInt64 () |> interpretIntegerAs t
        | Format.Float32 -> x.ReadFloat32 () |> interpretFloatAs t
        | Format.Float64 -> x.ReadFloat64 () |> interpretFloatAs t
        | Format.Nil -> box null
        | Format.True -> box true
        | Format.False -> box false
        // fixarr
        | b when b ||| 0b00001111uy = 0b10011111uy -> x.ReadArray (b &&& 0b00001111uy |> int, t)
        | Format.Array16 ->
            let len = x.ReadUInt16 () |> int
            x.ReadArray (len, t)
        | Format.Array32 ->
            let len = x.ReadUInt32 () |> int
            x.ReadArray (len, t)
        // fixmap
        | b when b ||| 0b00001111uy = 0b10001111uy -> x.ReadMap (b &&& 0b00001111uy |> int, t)
        | Format.Map16 ->
            let len = x.ReadUInt16 () |> int
            x.ReadMap (len, t)
        | Format.Map32 ->
            let len = x.ReadUInt32 () |> int
            x.ReadMap (len, t)
        | Format.Bin8 ->
            let len = x.ReadByte () |> int
            x.ReadBin (len, t)
        | Format.Bin16 ->
            let len = x.ReadUInt16 () |> int
            x.ReadBin (len, t)
        | Format.Bin32 ->
            let len = x.ReadUInt32 () |> int
            x.ReadBin (len, t)
        | b ->
            failwithf "Position %d, byte %d, expected type %s." pos b t.Name
