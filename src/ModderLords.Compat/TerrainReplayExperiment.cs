using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace ModderLords.Compat;

/// <summary>
/// SPIKE, not a fix. Answers the stripped scene's terrain queries from a <em>recorded single-player client</em>
/// terrain-probe CSV, by nearest sample.
///
/// <para>This is the companion to <see cref="TerrainStubExperiment"/> and exists to isolate one column the stub
/// deliberately leaves alone: <c>GetMapPatchAtPosition().sceneIndex</c>, which the server reports as 0 at every
/// position where a client produces 181 distinct values. Fabricating a patch index would be guessing, and a wrong
/// index selects a wrong scene — a different failure that would muddy the result. A recording is ground truth, so a
/// change in behaviour under it means the value mattered, full stop.</para>
///
/// <para>It reads ground truth rather than <c>terrain.bin</c> on purpose: parsing a 56 MB terrain format is the real
/// fix (docs/FIELD-BATTLE-TERRAIN.md step 2) and is only worth starting once these answers are shown to matter. The
/// grid is coarse — 48 samples per axis is roughly 34 map units per cell — which is useless for gameplay and ample
/// for "does the battle load".</para>
///
/// <para>Off unless MODDERLORDS_TERRAIN_REPLAY names a CSV. Defaults to replaying the map patch only, which is the
/// sceneIndex question on its own; MODDERLORDS_TERRAIN_REPLAY_FIELDS widens it. Never for a release.</para>
/// </summary>
internal static class TerrainReplayExperiment
{
    /// <summary>Path to a terrain-probe CSV recorded on a single-player client on the same map.</summary>
    public const string Variable = "MODDERLORDS_TERRAIN_REPLAY";
    /// <summary>Comma-separated: patch (default), height, env — or all.</summary>
    public const string FieldsVariable = "MODDERLORDS_TERRAIN_REPLAY_FIELDS";

    private static Type? _headless;
    private static bool _installed;
    private static bool _patch, _height, _env;
    private static int _grid;
    private static Vec2 _min, _max;
    private static Sample[] _samples = Array.Empty<Sample>();
    private static int _patchServed, _heightServed, _envServed, _missed;

    private struct Sample
    {
        public int SceneIndex;
        public float U, V;
        public bool HeightOk;
        public float Height;
        public TerrainType[] Env;
    }

    internal static void Install(Harmony harmony)
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (_installed || string.IsNullOrEmpty(path)) return;
        if (!ServerDetect.IsDedicatedServer) throw new InvalidOperationException("The terrain replay is server-only");
        ReadFields();
        Load(path!);
        _headless = MapSceneTarget.Headless();

        if (_patch)
            foreach (var target in MapSceneTarget.Both("GetMapPatchAtPosition", new[] { typeof(CampaignVec2).MakeByRefType() }))
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(TerrainReplayExperiment), nameof(ServePatch)));
        if (_height)
            foreach (var target in MapSceneTarget.Both("GetHeightAtPoint",
                         new[] { typeof(CampaignVec2).MakeByRefType(), typeof(float).MakeByRefType() }))
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(TerrainReplayExperiment), nameof(ServeHeight)));
        if (_env)
            foreach (var target in MapSceneTarget.Both("GetEnvironmentTerrainTypesCount",
                         new[] { typeof(CampaignVec2).MakeByRefType(), typeof(TerrainType).MakeByRefType() }))
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(TerrainReplayExperiment), nameof(ServeEnv)));

        _installed = true;
        Log.Warn($"SPIKE {Variable} is on: replaying {Fields()} from {_grid}x{_grid} recorded client samples over " +
                 $"{_min}..{_max}. The answers are real but are sampled every " +
                 $"{(_max.x - _min.x) / _grid:0.#} units; this is a diagnostic, not a fix.");
    }

    /// <summary>Null when the spike is off, so a normal run logs nothing at all about it.</summary>
    internal static string? Summary() => _installed
        ? $"terrain replay: served {_patchServed} patch, {_heightServed} height, {_envServed} env answer(s); " +
          $"{_missed} position(s) fell outside the recording"
        : null;

    private static void ReadFields()
    {
        var raw = Environment.GetEnvironmentVariable(FieldsVariable);
        if (string.IsNullOrWhiteSpace(raw)) { _patch = true; return; }
        foreach (var field in raw!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim().ToLowerInvariant()))
            switch (field)
            {
                case "all": _patch = _height = _env = true; break;
                case "patch": _patch = true; break;
                case "height": _height = true; break;
                case "env": _env = true; break;
                default: throw new ArgumentException(FieldsVariable + ": unknown field '" + field + "' (patch, height, env, all)");
            }
        if (!_patch && !_height && !_env) throw new ArgumentException(FieldsVariable + " named no fields");
    }

    private static string Fields()
    {
        var on = new List<string>();
        if (_patch) on.Add("map patch");
        if (_height) on.Add("height");
        if (_env) on.Add("environment terrain types");
        return string.Join(" + ", on);
    }

    /// <summary>
    /// The CSV is this repo's own probe output, so it is parsed strictly. A recording from the wrong map would look
    /// exactly like a working spike, so refuse anything whose header does not describe a square grid over its borders.
    /// </summary>
    private static void Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = lines.Where(l => l.StartsWith("#", StringComparison.Ordinal)).ToList();
        var body = lines.Where(l => !l.StartsWith("#", StringComparison.Ordinal) && l.Length > 0).ToList();
        if (body.Count < 2) throw new InvalidDataException(path + " has no samples");
        var side = Field(header, "side");
        if (side == "server")
            throw new InvalidDataException("that is a server recording; the replay needs a single-player client one");

        var borders = Field(header, "borders") ?? throw new InvalidDataException("the recording has no borders");
        var ends = borders.Split(new[] { ".." }, StringSplitOptions.None);
        if (ends.Length != 2) throw new InvalidDataException("unreadable borders: " + borders);
        _min = ParseVec2(ends[0]);
        _max = ParseVec2(ends[1]);
        if (_max.x <= _min.x || _max.y <= _min.y) throw new InvalidDataException("the recording's borders are inverted");

        // Extra MODDERLORDS_TERRAIN_PROBE_AT samples are appended after the grid, so take the largest square prefix.
        var rows = body.Count - 1;
        _grid = (int)Math.Sqrt(rows);
        if (_grid < 2) throw new InvalidDataException("the recording is too small to be a grid");

        var columns = body[0].Split(',');
        int Column(string name)
        {
            var index = Array.IndexOf(columns, name);
            if (index < 0) throw new InvalidDataException("the recording has no '" + name + "' column");
            return index;
        }
        int scene = Column("patchScene"), u = Column("patchU"), v = Column("patchV");
        int heightOk = Column("heightOk"), height = Column("height"), env = Column("envTypes");
        int x = Column("x"), y = Column("y");

        _samples = new Sample[_grid * _grid];
        for (var i = 0; i < _samples.Length; i++)
        {
            var cells = body[i + 1].Split(',');
            // Trust the recorded position over row order: a cell read from the wrong row is a silent wrong answer.
            var index = IndexOf(Number(cells[x]), Number(cells[y]));
            if (index < 0) throw new InvalidDataException($"sample {i} sits outside the recording's own borders");
            _samples[index] = new Sample
            {
                SceneIndex = (int)Number(cells[scene]),
                U = Number(cells[u]),
                V = Number(cells[v]),
                HeightOk = cells[heightOk] == "1",
                Height = Number(cells[height]),
                Env = cells[env].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => (TerrainType)int.Parse(t, CultureInfo.InvariantCulture)).ToArray(),
            };
        }
    }

    private static string? Field(IEnumerable<string> header, string name)
    {
        foreach (var line in header)
        foreach (var token in line.TrimStart('#').Trim().Split(' '))
            if (token.StartsWith(name + "=", StringComparison.Ordinal)) return token.Substring(name.Length + 1);
        return null;
    }

    private static Vec2 ParseVec2(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 2) throw new InvalidDataException("unreadable position: " + text);
        return new Vec2(Number(parts[0]), Number(parts[1]));
    }

    private static float Number(string text) =>
        text == "nan" ? float.NaN : float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The probe samples cell centres, so flooring the normalized position inverts its mapping exactly.</summary>
    private static int IndexOf(float x, float y)
    {
        var ix = (int)Math.Floor((x - _min.x) / (_max.x - _min.x) * _grid);
        var iy = (int)Math.Floor((y - _min.y) / (_max.y - _min.y) * _grid);
        if (ix < 0 || iy < 0 || ix >= _grid || iy >= _grid) return -1;
        return iy * _grid + ix;
    }

    private static bool Find(object[] args, out Sample sample)
    {
        sample = default;
        if (args.Length < 1 || args[0] is not CampaignVec2 position) return false;
        var index = IndexOf(position.X, position.Y);
        if (index < 0) { _missed++; return false; }
        sample = _samples[index];
        return true;
    }

    private static void ServePatch(object __instance, ref MapPatchData __result, object[] __args)
    {
        if (__instance.GetType() != _headless || !Find(__args, out var sample)) return;
        __result.sceneIndex = sample.SceneIndex;
        __result.normalizedCoordinates = new Vec2(sample.U, sample.V);
        if (_patchServed++ == 0) Log.Info($"terrain replay: first map patch served (sceneIndex {sample.SceneIndex})");
    }

    /// <summary>
    /// The position arrives through __args because the two declarations name it differently (originPosition against
    /// vec2), but the by-ref output is taken by name: Harmony writes a named ref parameter back to the caller, and
    /// both declarations agree on this one's name.
    /// </summary>
    private static void ServeHeight(object __instance, ref bool __result, object[] __args, ref float height)
    {
        if (__instance.GetType() != _headless || !Find(__args, out var sample)) return;
        __result = sample.HeightOk;
        height = sample.Height;
        if (_heightServed++ == 0) Log.Info($"terrain replay: first height served ({sample.Height:0.##})");
    }

    private static void ServeEnv(object __instance, ref List<TerrainType> __result, object[] __args)
    {
        if (__instance.GetType() != _headless || !Find(__args, out var sample)) return;
        if (sample.Env.Length == 0) return;
        if (__result != null && __result.Count > 0) return;       // idempotent: both declarations may fire
        __result = new List<TerrainType>(sample.Env);
        if (_envServed++ == 0) Log.Info($"terrain replay: first environment type list served ({sample.Env.Length} entries)");
    }
}
