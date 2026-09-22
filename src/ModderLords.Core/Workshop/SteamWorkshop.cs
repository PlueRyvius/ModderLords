using System.Runtime.InteropServices;
using ModderLords.Core.Launch;

namespace ModderLords.Core.Workshop;

/// <summary>
/// Subscribes to Workshop items through the Steam client, the same way the game's own launcher would.
///
/// Uses the game's own steam_api64.dll (bin\Win64_Shipping_Client), so nothing of Valve's is redistributed and the API
/// version always matches what Bannerlord itself talks to. SteamAPI_Init makes Steam treat the calling process as
/// Bannerlord, so this is meant to run in a short-lived child process (see <see cref="SubscribeArg"/>): the user shows as
/// "playing" only while it runs, and the app itself never loads the Steam library.
/// </summary>
public sealed class SteamWorkshop : IDisposable
{
    public const string SubscribeArg = "--workshop-subscribe";
    public const string GameRootArg = "--game-root";

    private const uint StateSubscribed = 1, StateInstalled = 4, StateNeedsUpdate = 8, StateDownloading = 16, StateDownloadPending = 32;
    private const int CallbackSubscribeResult = 1313;        // RemoteStorageSubscribePublishedFileResult_t
    private const int CallbackQueryCompleted = 3401;         // SteamUGCQueryCompleted_t
    private const ulong InvalidQueryHandle = ulong.MaxValue;

    private readonly IntPtr _lib;
    private readonly IntPtr _ugc, _utils;
    private readonly RunCallbacksFn _runCallbacks;
    private readonly ShutdownFn _shutdown;
    private readonly SubscribeItemFn _subscribe;
    private readonly GetItemStateFn _getItemState;
    private readonly GetItemDownloadInfoFn _getDownloadInfo;
    private readonly CreateQueryDetailsFn _createQuery;
    private readonly SetReturnChildrenFn _setReturnChildren;
    private readonly SendQueryFn _sendQuery;
    private readonly GetQueryResultFn _getQueryResult;
    private readonly GetQueryChildrenFn _getQueryChildren;
    private readonly ReleaseQueryFn _releaseQuery;
    private readonly IsCallCompletedFn _isCallCompleted;
    private readonly GetCallResultFn _getCallResult;

    private SteamWorkshop(IntPtr lib)
    {
        _lib = lib;
        T Fn<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p)
                : throw new SteamUnavailableException($"the game's steam_api64.dll has no {name}");
        IntPtr Accessor(string prefix)
        {
            // The interface version in the export name follows the SDK the game shipped with; take whichever is there.
            for (var v = 30; v >= 10; v--)
                if (NativeLibrary.TryGetExport(lib, $"{prefix}{v:000}", out var p))
                    return Marshal.GetDelegateForFunctionPointer<AccessorFn>(p)();
            throw new SteamUnavailableException($"the game's steam_api64.dll has no {prefix}*");
        }

        var init = Fn<InitFn>("SteamAPI_Init");
        _runCallbacks = Fn<RunCallbacksFn>("SteamAPI_RunCallbacks");
        _shutdown = Fn<ShutdownFn>("SteamAPI_Shutdown");
        _subscribe = Fn<SubscribeItemFn>("SteamAPI_ISteamUGC_SubscribeItem");
        _getItemState = Fn<GetItemStateFn>("SteamAPI_ISteamUGC_GetItemState");
        _getDownloadInfo = Fn<GetItemDownloadInfoFn>("SteamAPI_ISteamUGC_GetItemDownloadInfo");
        _createQuery = Fn<CreateQueryDetailsFn>("SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest");
        _setReturnChildren = Fn<SetReturnChildrenFn>("SteamAPI_ISteamUGC_SetReturnChildren");
        _sendQuery = Fn<SendQueryFn>("SteamAPI_ISteamUGC_SendQueryUGCRequest");
        _getQueryResult = Fn<GetQueryResultFn>("SteamAPI_ISteamUGC_GetQueryUGCResult");
        _getQueryChildren = Fn<GetQueryChildrenFn>("SteamAPI_ISteamUGC_GetQueryUGCChildren");
        _releaseQuery = Fn<ReleaseQueryFn>("SteamAPI_ISteamUGC_ReleaseQueryUGCRequest");
        _isCallCompleted = Fn<IsCallCompletedFn>("SteamAPI_ISteamUtils_IsAPICallCompleted");
        _getCallResult = Fn<GetCallResultFn>("SteamAPI_ISteamUtils_GetAPICallResult");

        if (!init()) throw new SteamUnavailableException("Steam is not running, or this Steam account does not own Bannerlord");
        _ugc = Accessor("SteamAPI_SteamUGC_v");
        _utils = Accessor("SteamAPI_SteamUtils_v");
        if (_ugc == IntPtr.Zero || _utils == IntPtr.Zero) { _shutdown(); throw new SteamUnavailableException("Steam's Workshop interface is not available"); }
    }

    /// <summary>The game's steam_api64.dll, or null when the game is not where we were told.</summary>
    public static string? SteamApiPath(string gameRoot)
    {
        var p = Path.Combine(gameRoot, "bin", "Win64_Shipping_Client", "steam_api64.dll");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Connects to Steam as Bannerlord. Throws <see cref="SteamUnavailableException"/> with a readable reason.</summary>
    public static SteamWorkshop Open(string gameRoot)
    {
        var path = SteamApiPath(gameRoot) ?? throw new SteamUnavailableException($"steam_api64.dll not found under {gameRoot}");
        // SteamAPI_Init reads the app id from here when there is no steam_appid.txt beside the exe.
        Environment.SetEnvironmentVariable("SteamAppId", GamePaths.BannerlordAppId.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", GamePaths.BannerlordAppId.ToString());
        var lib = NativeLibrary.Load(path);
        try { return new SteamWorkshop(lib); }
        catch { NativeLibrary.Free(lib); throw; }
    }

    /// <summary>
    /// Subscribes to every item (and, once, to the items they require), then waits until each is installed, fails, or
    /// stops making progress. Every step is reported, so the caller can show it and knows exactly what ended up where.
    /// </summary>
    public void SubscribeAndWait(IReadOnlyList<ulong> ids, Action<WorkshopEvent> report, TimeSpan stallTimeout, CancellationToken ct = default)
    {
        var wanted = new List<ulong>();
        void Add(ulong id, ulong? parent)
        {
            if (wanted.Contains(id)) return;
            wanted.Add(id);
            var call = _subscribe(_ugc, id);
            var result = Await<SubscribeResult>(call, CallbackSubscribeResult, ct);
            if (result is { Result: 1 })
                report(new WorkshopEvent(id, WorkshopEventKind.Subscribed, parent is null ? null : $"required by {parent}", Parent: parent));
            else
                report(new WorkshopEvent(id, WorkshopEventKind.Failed, result is null ? "Steam did not answer" : $"Steam refused it (result {result.Value.Result}) — removed, private, or hidden", Parent: parent));
        }

        foreach (var id in ids) Add(id, null);
        foreach (var (parent, children) in RequiredItems(ids, ct))
            foreach (var child in children) Add(child, parent);

        var done = new HashSet<ulong>();
        var lastProgress = DateTime.UtcNow;
        var lastBytes = new Dictionary<ulong, ulong>();
        while (done.Count < wanted.Count)
        {
            ct.ThrowIfCancellationRequested();
            _runCallbacks();
            foreach (var id in wanted.Where(i => !done.Contains(i)))
            {
                var state = _getItemState(_ugc, id);
                if ((state & StateInstalled) != 0 && (state & (StateNeedsUpdate | StateDownloading | StateDownloadPending)) == 0)
                {
                    done.Add(id);
                    lastProgress = DateTime.UtcNow;
                    report(new WorkshopEvent(id, WorkshopEventKind.Installed, null));
                    continue;
                }
                if (_getDownloadInfo(_ugc, id, out var got, out var total) && total > 0)
                {
                    if (!lastBytes.TryGetValue(id, out var before) || before != got)
                    {
                        lastBytes[id] = got;
                        lastProgress = DateTime.UtcNow;
                        report(new WorkshopEvent(id, WorkshopEventKind.Downloading, null, (double)got / total));
                    }
                }
            }
            if (DateTime.UtcNow - lastProgress > stallTimeout)
            {
                foreach (var id in wanted.Where(i => !done.Contains(i)))
                    report(new WorkshopEvent(id, WorkshopEventKind.Failed, "no download progress — check the Steam Downloads page"));
                return;
            }
            Thread.Sleep(250);
        }
    }

    /// <summary>Items each of <paramref name="ids"/> lists as required on its Workshop page.</summary>
    private IEnumerable<(ulong Parent, IReadOnlyList<ulong> Children)> RequiredItems(IReadOnlyList<ulong> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var arr = ids.ToArray();
        var handle = _createQuery(_ugc, arr, (uint)arr.Length);
        if (handle == InvalidQueryHandle) return [];
        var found = new List<(ulong, IReadOnlyList<ulong>)>();
        try
        {
            _setReturnChildren(_ugc, handle, true);
            var done = Await<QueryCompleted>(_sendQuery(_ugc, handle), CallbackQueryCompleted, ct);
            if (done is not { Result: 1 }) return [];
            // SteamUGCDetails_t is large; only its first field (the item id) is read, so a generous buffer is enough.
            var details = Marshal.AllocHGlobal(32 * 1024);
            try
            {
                for (uint i = 0; i < done.Value.NumResults; i++)
                {
                    if (!_getQueryResult(_ugc, handle, i, details)) continue;
                    var parent = (ulong)Marshal.ReadInt64(details);
                    var children = new ulong[64];
                    if (!_getQueryChildren(_ugc, handle, i, children, (uint)children.Length)) continue;
                    var list = children.Where(c => c != 0).ToList();
                    if (list.Count > 0) found.Add((parent, list));
                }
            }
            finally { Marshal.FreeHGlobal(details); }
        }
        finally { _releaseQuery(_ugc, handle); }
        return found;
    }

    private T? Await<T>(ulong call, int callbackId, CancellationToken ct) where T : struct
    {
        if (call == 0) return null;
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            _runCallbacks();
            if (_isCallCompleted(_utils, call, out var failed))
            {
                if (failed) return null;
                var size = Marshal.SizeOf<T>();
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    return _getCallResult(_utils, call, buf, size, callbackId, out var f2) && !f2 ? Marshal.PtrToStructure<T>(buf) : null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            Thread.Sleep(100);
        }
        return null;
    }

    public void Dispose()
    {
        try { _shutdown(); } catch { }
        NativeLibrary.Free(_lib);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SubscribeResult { public int Result; public ulong PublishedFileId; }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct QueryCompleted
    {
        public ulong Handle;
        public int Result;
        public uint NumResults;
        public uint TotalMatching;
        [MarshalAs(UnmanagedType.U1)] public bool Cached;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] NextCursor;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] private delegate bool InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RunCallbacksFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AccessorFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong SubscribeItemFn(IntPtr self, ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetItemStateFn(IntPtr self, ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool GetItemDownloadInfoFn(IntPtr self, ulong id, out ulong downloaded, out ulong total);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong CreateQueryDetailsFn(IntPtr self, ulong[] ids, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool SetReturnChildrenFn(IntPtr self, ulong handle, [MarshalAs(UnmanagedType.U1)] bool value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong SendQueryFn(IntPtr self, ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool GetQueryResultFn(IntPtr self, ulong handle, uint index, IntPtr details);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool GetQueryChildrenFn(IntPtr self, ulong handle, uint index, [Out] ulong[] children, uint max);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] private delegate bool ReleaseQueryFn(IntPtr self, ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsCallCompletedFn(IntPtr self, ulong call, [MarshalAs(UnmanagedType.U1)] out bool failed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool GetCallResultFn(IntPtr self, ulong call, IntPtr buffer, int size, int callbackId, [MarshalAs(UnmanagedType.U1)] out bool failed);
}

public sealed class SteamUnavailableException(string message) : Exception(message);
