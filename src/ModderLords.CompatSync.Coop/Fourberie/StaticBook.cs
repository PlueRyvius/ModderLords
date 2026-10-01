using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// One set of values for a fixed list of static fields: a "book". A single-player mod that keeps the player's state in
/// statics (Fourberie: 36 saved fields plus campaign-level scratch) can then run on a co-op server once per player, with
/// that player's book swapped in around the call and read back out afterwards. Reading back after the call is what makes
/// both in-place edits (a dictionary gaining a row) and reassignments (a field set to a new list) land in the book.
///
/// Free of game and Coop types so the tests compile it in.
/// </summary>
public sealed class StaticBookSchema
{
    private readonly FieldInfo[] _fields;
    private readonly object?[] _pristine;

    /// <param name="fields">The swapped fields; all static, none readonly.</param>
    /// <param name="persisted">Names of the fields that are saved and mirrored; the rest are swapped but kept in memory only.</param>
    public StaticBookSchema(IEnumerable<FieldInfo> fields, IEnumerable<string> persisted)
    {
        _fields = fields.ToArray();
        foreach (var f in _fields)
            if (!f.IsStatic || f.IsInitOnly || f.IsLiteral) throw new ArgumentException(f.DeclaringType?.Name + "." + f.Name + " is not a writable static field");
        var names = new HashSet<string>(persisted, StringComparer.Ordinal);
        Persisted = Enumerable.Range(0, _fields.Length).Where(i => names.Contains(_fields[i].Name)).ToArray();
        // What the fields held before any campaign touched them: the shape a new player's book starts from.
        _pristine = Capture();
    }

    public IReadOnlyList<FieldInfo> Fields => _fields;

    /// <summary>Indexes into <see cref="Fields"/> of the persisted fields.</summary>
    public IReadOnlyList<int> Persisted { get; }

    public object?[] Capture()
    {
        var values = new object?[_fields.Length];
        for (var i = 0; i < _fields.Length; i++) values[i] = _fields[i].GetValue(null);
        return values;
    }

    public void Install(object?[] values)
    {
        if (values.Length != _fields.Length) throw new ArgumentException("book has " + values.Length + " values for " + _fields.Length + " fields");
        for (var i = 0; i < _fields.Length; i++) _fields[i].SetValue(null, values[i]);
    }

    /// <summary>A new player's book: empty collections of the same types, scalars as they started.</summary>
    public object?[] Fresh()
    {
        var values = new object?[_fields.Length];
        for (var i = 0; i < _fields.Length; i++) values[i] = FreshValue(_pristine[i], _fields[i].FieldType);
        return values;
    }

    /// <summary>
    /// Collections start empty (of the type the field held, else the declared type when it is a concrete collection);
    /// everything else starts at its default. Scalars deliberately do not copy the pristine value: the schema may be
    /// built after a campaign already loaded, when the statics hold the server's own stand-in values.
    /// </summary>
    internal static object? FreshValue(object? pristine, Type declared)
    {
        if (pristine is IDictionary or IList && pristine is not Array) return Activator.CreateInstance(pristine.GetType());
        if (!declared.IsAbstract && !declared.IsInterface && !declared.IsArray && declared.GetConstructor(Type.EmptyTypes) != null
            && (typeof(IDictionary).IsAssignableFrom(declared) || typeof(IList).IsAssignableFrom(declared)))
            return Activator.CreateInstance(declared);
        return declared.IsValueType ? Activator.CreateInstance(declared) : null;
    }
}

/// <summary>
/// Which book is installed in the statics, and swapping between them. Re-entering the book that is already installed is
/// a no-op: installing its stored values again would undo any reassignment the running code made since the outer entry.
/// </summary>
public sealed class StaticBookSwitch
{
    private readonly StaticBookSchema _schema;
    private readonly Stack<(string? Owner, object?[] Outside)> _stack = new Stack<(string?, object?[])>();

    public StaticBookSwitch(StaticBookSchema schema) => _schema = schema;

    /// <summary>The key of the installed book, or null when the statics hold the values from outside any book.</summary>
    public string? Current { get; private set; }

    public bool InBook => Current != null;

    /// <summary>
    /// True when <paramref name="key"/>'s book is installed further down the stack but another book is installed now.
    /// Its live values are parked in the outer frame, not in its store, so entering it again would run on stale values.
    /// </summary>
    public bool IsSuspended(string key) =>
        !string.Equals(Current, key, StringComparison.Ordinal) && _stack.Any(f => string.Equals(f.Owner, key, StringComparison.Ordinal));

    /// <summary>Installs <paramref name="book"/> for <paramref name="key"/>; dispose to read it back and restore what was there.</summary>
    public IDisposable Enter(string key, Func<object?[]> book, Action<object?[]> store)
    {
        if (string.Equals(Current, key, StringComparison.Ordinal)) return Nothing.Instance;
        var outside = _schema.Capture();
        _stack.Push((Current, outside));
        try
        {
            _schema.Install(book());
        }
        catch
        {
            _stack.Pop();
            _schema.Install(outside);
            throw;
        }
        Current = key;
        return new Exit(this, store);
    }

    private sealed class Exit : IDisposable
    {
        private StaticBookSwitch? _owner;
        private readonly Action<object?[]> _store;

        public Exit(StaticBookSwitch owner, Action<object?[]> store)
        {
            _owner = owner;
            _store = store;
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;
            var (previous, outside) = owner._stack.Pop();
            try { _store(owner._schema.Capture()); }
            finally
            {
                owner._schema.Install(outside);
                owner.Current = previous;
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new Nothing();
        public void Dispose() { }
    }
}
