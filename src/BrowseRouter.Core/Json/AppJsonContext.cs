using BrowseRouter.Core.Config;
using BrowseRouter.Core.Ipc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrowseRouter.Core.Json;

/// <summary>
/// Source-generated metadata for all DTOs sent over the pipe or persisted to disk.
/// AOT MUST go through this context — reflection-based serialisation is disabled
/// project-wide (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>).
///
/// Registering only the root types is enough: the source generator discovers
/// transitively referenced properties (lists / dictionaries / polymorphic
/// derivatives) automatically.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RootConfig))]
[JsonSerializable(typeof(OpenUrlRequest))]
[JsonSerializable(typeof(OpenUrlResponse))]
public partial class AppJsonContext : JsonSerializerContext;