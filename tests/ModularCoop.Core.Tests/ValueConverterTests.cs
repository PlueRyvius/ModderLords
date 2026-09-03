using System.Globalization;
using ModularCoop.CompatSync;

namespace ModularCoop.Core.Tests;

/// <summary>The value text format shared by the module (linked source), the wire and the launcher.</summary>
public sealed class ValueConverterTests
{
    private enum Mode { Slow, Fast }

    private sealed class Holder
    {
        public bool B { get; set; } = true;
        public int I { get; set; } = 42;
        public float F { get; set; } = 1.5f;
        public double D { get; set; } = -2.25;
        public string S { get; set; } = "héllo";
        public Mode M { get; set; } = Mode.Fast;
        public int[] Arr { get; set; } = [1];
        public int ReadOnly { get; } = 7;
        public static long StaticL = 9;
    }

    private static IEnumerable<PropertyRef> Refs(Holder h) =>
        typeof(Holder).GetProperties().Select(p => (PropertyRef)new ReflectionPropertyRef(p, () => h))
            .Concat([new ReflectionPropertyRef(typeof(Holder).GetField(nameof(Holder.StaticL))!, null)]);

    [Fact]
    public void Kind_And_Support()
    {
        Assert.Equal("bool", ValueConverter.Kind(typeof(bool)));
        Assert.Equal("int", ValueConverter.Kind(typeof(long)));
        Assert.Equal("float", ValueConverter.Kind(typeof(decimal)));
        Assert.Equal("string", ValueConverter.Kind(typeof(string)));
        Assert.Equal("enum", ValueConverter.Kind(typeof(Mode)));
        Assert.Equal("Int32[]", ValueConverter.Kind(typeof(int[])));
        Assert.False(ValueConverter.IsSupported(typeof(int[])));
        Assert.True(ValueConverter.IsSupported(typeof(Mode)));
    }

    [Fact]
    public void Format_IsInvariant_RegardlessOfCulture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", ValueConverter.Format(1.5f));
            Assert.Equal("-2.25", ValueConverter.Format(-2.25));
            Assert.Equal("true", ValueConverter.Format(true));
            Assert.Equal("Fast", ValueConverter.Format(Mode.Fast));
            Assert.Null(ValueConverter.Format(new[] { 1 }));
            Assert.True(ValueConverter.TryParse("2.75", typeof(float), out var f));
            Assert.Equal(2.75f, f);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void Payload_Apply_RoundTrip()
    {
        var a = new Holder();
        var payload = ValueConverter.Payload(Refs(a));
        Assert.Contains("B\ttrue\n", payload);
        Assert.Contains("M\tFast\n", payload);
        Assert.Contains("StaticL\t9\n", payload);
        Assert.DoesNotContain("Arr", payload);            // unsupported kinds are left out
        Assert.Contains("ReadOnly\t7\n", payload);        // readable, just not writable

        var b = new Holder { B = false, I = 0, F = 0, D = 0, S = "", M = Mode.Slow };
        var changed = ValueConverter.ApplyTo(Refs(b), ValueConverter.ParsePayload(payload), out var report);
        Assert.Equal(6, changed);                         // B I F D S M (ReadOnly same value? no: 7 vs 7 unchanged; StaticL same)
        Assert.True(b.B); Assert.Equal(42, b.I); Assert.Equal(1.5f, b.F); Assert.Equal("héllo", b.S); Assert.Equal(Mode.Fast, b.M);
        Assert.StartsWith("6 changed", report);
    }

    [Fact]
    public void Apply_ReportsUnsupportedAndBadValues()
    {
        var h = new Holder();
        var n = ValueConverter.ApplyTo(Refs(h), new Dictionary<string, string> { ["I"] = "abc", ["ReadOnly"] = "8", ["M"] = "fast", ["Unknown"] = "1" }, out var report);
        Assert.Equal(1, n);                               // "fast" differs as text, parses case-insensitively, is written
        Assert.Equal("1 changed, unsupported: I, ReadOnly", report);
        Assert.Equal(Mode.Fast, h.M);
        Assert.Equal(42, h.I);
    }

    [Fact]
    public void Describe_ShapesEditorEntry()
    {
        var h = new Holder();
        var r = new ReflectionPropertyRef(typeof(Holder).GetProperty(nameof(Holder.M))!, () => h);
        var d = ValueConverter.Describe(r, "Mode", "hint", null, null, requireRestart: true);
        Assert.Equal("enum", d["Kind"]);
        Assert.Equal("Fast", d["Value"]);
        Assert.Equal(true, d["Editable"]);
        Assert.Equal(new List<string> { "Slow", "Fast" }, d["Choices"]);
        Assert.Equal(true, d["RequireRestart"]);
        var ro = ValueConverter.Describe(new ReflectionPropertyRef(typeof(Holder).GetProperty(nameof(Holder.ReadOnly))!, () => h), "RO", null, null, null, false);
        Assert.Equal(false, ro["Editable"]);
        var arr = ValueConverter.Describe(new ReflectionPropertyRef(typeof(Holder).GetProperty(nameof(Holder.Arr))!, () => h), "Arr", null, null, null, false);
        Assert.Equal(false, arr["Editable"]);
        Assert.Equal("Int32[]", arr["Kind"]);
        Assert.Equal("System.Int32[]", arr["Value"]);     // ToString fallback so the tab can still show something
    }
}
