using System.Linq;
using ModderLords.CompatSync.Coop.LivingEconomy;
using Newtonsoft.Json.Linq;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// Keeping one player's Fourberie book the same on the server and on that player's game, rows travelling both ways.
///
/// The server runs the player's ticks (income, timers, schemes); the player's game runs the player's menus, screens and
/// missions. Each side sends only the rows it changed since the state both last agreed on, and a row from the other
/// side overwrites that row only. A client that misses a server message asks for the whole book again.
///
/// Free of game and Coop types so the tests compile it in; the JSON is the book's persisted fields (FbBookCodec).
/// </summary>
public sealed class FbServerLedger
{
    /// <summary>What the player's game was last sent, with the rows it reported folded in. Null until the first full send.</summary>
    public JObject? Sent { get; private set; }

    public int Sequence { get; private set; }

    /// <summary>The whole book for a player who asked for it (joining, or after a missed message).</summary>
    public string Full(JObject current)
    {
        Sent = (JObject)current.DeepClone();
        Sequence++;
        return LeMirrorDelta.Pack(LeMirrorDelta.KindFull, 0, Sequence, current);
    }

    /// <summary>The rows the server changed since the last send, or null when there are none or the player has no book yet.</summary>
    public string? DeltaIfChanged(JObject current)
    {
        if (Sent == null) return null;
        var diff = LeMirrorDelta.Diff(Sent, current);
        if (diff == null) return null;
        Sent = (JObject)current.DeepClone();
        var payload = LeMirrorDelta.Pack(LeMirrorDelta.KindDelta, Sequence, Sequence + 1, diff);
        Sequence++;
        return payload;
    }

    /// <summary>
    /// Folds the rows a player's game reported into <paramref name="current"/> (the server's book as JSON) and returns the
    /// merged book to install. They are also folded into what was sent, so they are not echoed back to the player.
    /// </summary>
    public JObject Merge(JObject current, JObject reported)
    {
        var merged = (JObject)current.DeepClone();
        LeMirrorDelta.Apply(merged, reported);
        if (Sent != null) LeMirrorDelta.Apply(Sent, reported);
        return merged;
    }

    public void Forget()
    {
        Sent = null;
    }
}

/// <summary>The player's side of <see cref="FbServerLedger"/>.</summary>
public sealed class FbClientLedger
{
    /// <summary>The book as both sides last agreed it. Null until the server's full book arrives; nothing is reported before that.</summary>
    public JObject? Agreed { get; private set; }

    public int Sequence { get; private set; }

    public bool HasBook => Agreed != null;

    /// <summary>
    /// A server message. Returns the book to install into the statics: the server's rows over the local ones, so a row
    /// this game changed and has not reported yet survives unless the server changed that same row. Null when the
    /// message cannot be used; <paramref name="needFull"/> then says whether to ask for the whole book.
    /// </summary>
    public JObject? Receive(string payload, JObject local, out bool needFull)
    {
        needFull = false;
        if (!LeMirrorDelta.TryUnpack(payload, out var kind, out var baseSequence, out var sequence, out var values)) return null;
        if (kind == LeMirrorDelta.KindFull)
        {
            Agreed = (JObject)values.DeepClone();
            Sequence = sequence;
            return values;
        }
        if (Agreed == null || baseSequence != Sequence)
        {
            needFull = true;
            return null;
        }
        LeMirrorDelta.Apply(Agreed, values);
        Sequence = sequence;
        var install = (JObject)local.DeepClone();
        LeMirrorDelta.Apply(install, values);
        return install;
    }

    /// <summary>
    /// The rows this game changed since the last agreement, packed for the server, or null when nothing changed. A field
    /// that is missing locally (it could not be written) is never reported as deleted.
    /// </summary>
    public string? Report(JObject local)
    {
        if (Agreed == null) return null;
        var diff = LeMirrorDelta.Diff(Agreed, local);
        if (diff == null) return null;
        foreach (var gone in diff.Properties().Where(p => p.Value is JObject e && e["$all"]?.Type == JTokenType.Null).ToList())
            gone.Remove();
        if (!diff.HasValues) return null;
        LeMirrorDelta.Apply(Agreed, diff);
        return LeMirrorDelta.Pack(LeMirrorDelta.KindDelta, Sequence, Sequence, diff);
    }

    public void Forget()
    {
        Agreed = null;
        Sequence = 0;
    }
}
