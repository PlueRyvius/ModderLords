using System;
using System.Collections.Generic;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Hands a player the server's prompts one at a time, and only when their game can take one.
/// <para>
/// Each prompt used to be opened the moment it arrived. Bellum decides things in bursts - a day tick can raise several
/// rebellions, calls to arms and court decisions at once - so a player could be handed a stack of inquiries in one
/// frame, in the middle of a battle. That is the situation a host reported crashes in (2026-10-03); there was no log
/// to say which call died, so this removes the situation rather than a known faulting line: nothing opens while the
/// player is in a mission or another inquiry is up, and the rest wait their turn.
/// </para>
/// <para>
/// A prompt is dropped once the server has stopped waiting for it, so a player who comes out of a long battle is not
/// asked things that were already decided for them. Free of game types so the tests can drive it.
/// </para>
/// </summary>
public sealed class PromptQueue<T>
{
    private readonly Queue<KeyValuePair<T, DateTime>> _waiting = new Queue<KeyValuePair<T, DateTime>>();
    private readonly TimeSpan _keepFor, _settle;
    private bool _showing;
    private DateTime _shownAt;

    /// <param name="keepFor">How long the server waits for an answer; a prompt held longer than this is not shown.</param>
    /// <param name="settle">How long after opening a prompt "nothing is open" is believed to mean it was closed.</param>
    public PromptQueue(TimeSpan keepFor, TimeSpan settle)
    {
        _keepFor = keepFor;
        _settle = settle;
    }

    public int Count => _waiting.Count;

    public void Add(T prompt, DateTime now) => _waiting.Enqueue(new KeyValuePair<T, DateTime>(prompt, now));

    /// <summary>The prompt taken last was answered; the next one may follow without waiting out the settle time.</summary>
    public void Answered() => _showing = false;

    /// <summary>Forgets everything: the session these prompts belonged to is over.</summary>
    public void Clear()
    {
        _waiting.Clear();
        _showing = false;
    }

    /// <summary>
    /// The next prompt to open, if the player's game can take one now. <paramref name="busy"/> is true while the
    /// player is in a mission or any inquiry is open. <paramref name="dropped"/> counts prompts passed over because
    /// the server has stopped waiting for them.
    /// </summary>
    public bool TryTake(DateTime now, bool busy, out T prompt, out int dropped)
    {
        prompt = default!;
        dropped = 0;
        // Counted before the busy check: a prompt that ran out during a battle is gone whether or not the battle is over.
        while (_waiting.Count > 0 && now - _waiting.Peek().Value >= _keepFor) { _waiting.Dequeue(); dropped++; }
        if (busy) return false;
        if (_showing)
        {
            // Not busy, yet ours was never answered: either it has not appeared yet (the screen opens it a moment
            // after the call) or it was closed without either callback. Past the settle time it is the second, and
            // waiting on it for ever would hold every later prompt back.
            if (now - _shownAt < _settle) return false;
            _showing = false;
        }
        if (_waiting.Count == 0) return false;
        prompt = _waiting.Dequeue().Key;
        _showing = true;
        _shownAt = now;
        return true;
    }
}
