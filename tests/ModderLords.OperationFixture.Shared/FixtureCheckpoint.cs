using System;
using System.IO;
using System.Text;

namespace ModderLords.OperationFixture;

internal static class FixtureCheckpoint
{
    public static void Emit(string kind, string request = "", string value = "", string revision = "")
    {
        var path = Environment.GetEnvironmentVariable("MODDERLORDS_FIXTURE_EVENTS");
        var run = Environment.GetEnvironmentVariable("MODDERLORDS_FIXTURE_RUN");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(run)) throw new InvalidOperationException("Fixture evidence destination is not configured");
        var line = "{\"run\":" + Quote(run) + ",\"kind\":" + Quote(kind) + ",\"request\":" + Quote(request)
            + ",\"value\":" + Quote(value) + ",\"revision\":" + Quote(revision) + "}";
        File.AppendAllText(path, line + Environment.NewLine);
    }
    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var c in text ?? "")
            if (c == '"' || c == '\\') result.Append('\\').Append(c);
            else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4"));
            else result.Append(c);
        return result.Append('"').ToString();
    }
}
