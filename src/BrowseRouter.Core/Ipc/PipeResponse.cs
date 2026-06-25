using System.Text.Json.Serialization;

namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Polymorphic base for every reply written by the Host back to the Launcher.
/// Mirrors <see cref="PipeRequest"/>: the <c>"type"</c> discriminator selects the
/// concrete response so a single pipe read can return either an
/// <see cref="OpenUrlResponse"/> or a <see cref="GcResponse"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenUrlResponse), "openUrl")]
[JsonDerivedType(typeof(GcResponse), "gc")]
public abstract class PipeResponse;