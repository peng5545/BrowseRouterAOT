using System.Text.Json.Serialization;

namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Polymorphic base for every request sent over the Launcher ↔ Host named pipe.
/// The <c>"type"</c> JSON property is the discriminator that selects the concrete
/// DTO — explicit discriminated-union polymorphism (source-generated, AOT-safe),
/// matching the project's <c>match: { type, value }</c> convention.
///
/// <para>
/// Register new request kinds with <see cref="JsonDerivedTypeAttribute"/> on this
/// base and add a dispatch branch in the Host's pipe handler. The Launcher and
/// Host always ship together, so cross-version wire compatibility is not a
/// concern — an unknown discriminator throws on read, which the pipe handler
/// surfaces as a logged warning.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenUrlRequest), "openUrl")]
[JsonDerivedType(typeof(GcRequest), "gc")]
public abstract class PipeRequest;