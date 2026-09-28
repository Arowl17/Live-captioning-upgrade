using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveCaptionsUpgrade.Core.Sharing;

public enum ConnectPurpose
{
    Session,
    Pair,
}

/// <summary>Messages on the encrypted connection between the two computers.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(PairCommitMessage), "pair-commit")]
[JsonDerivedType(typeof(PairNonceMessage), "pair-nonce")]
[JsonDerivedType(typeof(PairRevealMessage), "pair-reveal")]
[JsonDerivedType(typeof(PairDecisionMessage), "pair-decision")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(UnpairMessage), "unpair")]
[JsonDerivedType(typeof(CaptionsMessage), "captions")]
[JsonDerivedType(typeof(SenderStatusMessage), "status")]
public abstract record ControlMessage;

/// <summary>First message in each direction. <paramref name="TcpPort"/> is where the sender accepts connections.</summary>
public sealed record HelloMessage(int ProtocolVersion, string DeviceName, ConnectPurpose Purpose, CaptionSharingMode Mode, int TcpPort, string AppVersion) : ControlMessage;

public sealed record PairCommitMessage(byte[] Commitment) : ControlMessage;

public sealed record PairNonceMessage(byte[] Nonce) : ControlMessage;

public sealed record PairRevealMessage(byte[] Nonce) : ControlMessage;

public sealed record PairDecisionMessage(bool Accepted) : ControlMessage;

public sealed record PingMessage(long Timestamp) : ControlMessage;

public sealed record PongMessage(long Timestamp) : ControlMessage;

/// <summary>"I forgot you": the receiver forgets the pairing too.</summary>
public sealed record UnpairMessage : ControlMessage;

/// <summary>A finished sentence. Ids increase within a <see cref="CaptionsMessage.Session"/>.</summary>
/// <param name="AgeMs">How long ago it was finished, when the message was sent (clocks may differ between computers).</param>
public sealed record SharedLine(long Id, string Text, long AgeMs);

/// <summary>
/// New finished sentences and the sentence being spoken. The first message after connecting is a
/// snapshot of recent sentences, so the receiver can fill in what it missed.
/// </summary>
/// <param name="Session">Identifies one run of the sending app; line ids start again in a new session.</param>
public sealed record CaptionsMessage(string Session, IReadOnlyList<SharedLine> Lines, string Pending, bool Snapshot) : ControlMessage;

/// <summary>The sending computer's problem message (e.g. Live Captions not running), or null when all is well.</summary>
public sealed record SenderStatusMessage(string? Status) : ControlMessage;

internal static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },

        // Keep non-English text as UTF-8 rather than \uXXXX escapes, which would make messages up to 6x bigger.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize(ControlMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static ControlMessage? Deserialize(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<ControlMessage>(json, Options);
}
