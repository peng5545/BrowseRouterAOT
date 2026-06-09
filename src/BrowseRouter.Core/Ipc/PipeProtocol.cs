using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Wire format for the Launcher ↔ Host pipe: a 4-byte little-endian length prefix
/// followed by that many UTF-8 bytes of JSON. Synchronous-ish API on top of async
/// pipe streams. Caller is responsible for connecting / disposing the stream.
/// </summary>
public static class PipeProtocol
{
    /// <summary>
    /// Maximum accepted message size (bytes). Anything larger is rejected.
    /// </summary>
    private const int MaxMessageBytes = 1 * 1024 * 1024; // 1 MiB — far above any realistic URL.

    /// <summary>
    /// Serialise <paramref name="value"/> using the supplied <paramref name="typeInfo"/>
    /// (source-generated for AOT) and write it to <paramref name="stream"/>.
    /// </summary>
    public static async Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (json.Length > MaxMessageBytes)
            throw new InvalidOperationException(
                $"Pipe message too large: {json.Length} bytes (max {MaxMessageBytes}).");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read a length-prefixed JSON message from <paramref name="stream"/> and
    /// deserialise it via <paramref name="typeInfo"/>. Returns <c>null</c> if the
    /// peer closed the pipe before sending a complete message.
    /// </summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
            throw new InvalidOperationException($"Invalid pipe message length: {length}.");

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
            return null;
        return JsonSerializer.Deserialize(body, typeInfo);
    }

    /// <summary>
    /// Read exactly <c>buffer.Length</c> bytes from <paramref name="stream"/>; returns
    /// false on premature EOF (the caller should treat this as a clean disconnect).
    /// </summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
                return false;
            offset += read;
        }

        return true;
    }

    /// <summary>
    /// Build the fully-qualified pipe name for the current user / session, given a
    /// base name (typically <see cref="Constants.PipeBaseName"/>). Scoping by SID
    /// and session id prevents cross-session collisions on RDP / Fast User Switching.
    /// </summary>
    public static string BuildPipeName(string baseName, string userSid, int sessionId)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        return $"{baseName}.{Sanitize(userSid)}.{sessionId}";
    }

    private static string Sanitize(string s)
    {
        // Pipe names can't contain backslashes. SIDs only contain alphanumeric and
        // hyphens, but be defensive in case caller passed a domain-qualified name.
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch == '\\' ? '-' : ch);
        return sb.ToString();
    }
}