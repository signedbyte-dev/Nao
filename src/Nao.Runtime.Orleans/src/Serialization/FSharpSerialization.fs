namespace Nao.Runtime.Orleans.Serialization

open Orleans

/// F# discriminated unions and records need explicit serialization setup for Orleans.
/// This module provides surrogate types and converters for Orleans serialization.

/// Marker module for registering F# type serializers with Orleans.
/// Orleans requires [<GenerateSerializer>] on types or custom surrogates for F# types.
[<AutoOpen>]
module SerializationSetup =

    /// Apply this attribute to F# record types used as grain state or messages
    /// to ensure Orleans can serialize them.
    /// For F# DUs, register a custom surrogate converter via ISerializableTypeConverter.
    let [<Literal>] FSharpSerializationNote =
        "Use [<GenerateSerializer>] on simple records. For DUs, implement ISerializableTypeConverter<'T, Surrogate>."
