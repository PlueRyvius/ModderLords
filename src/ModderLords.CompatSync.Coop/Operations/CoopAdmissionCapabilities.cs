using System;
using System.Linq;
using System.Reflection;
using Common.Messaging;
using Common.Network;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Connections.States;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using LiteNetLib;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// The narrow Coop ABI needed by managed operation sessions. Coop is intentionally not version-pinned: a compatible
/// update keeps working, while a moved or missing seam refuses activation before a campaign or joining client loads.
/// The exact admission methods are resolved by signature; the transport/identity checks cover the services used after
/// admission. This is a capability contract, not a claim that an arbitrary future implementation is compatible.
/// </summary>
internal static class CoopAdmissionCapabilities
{
    public static bool TryResolve(bool server, out MethodInfo? target, out string reason)
    {
        target = null;
        var missing = TransportAndIdentityProblems(server).ToArray();
        if (missing.Length > 0)
        {
            reason = string.Join(", ", missing);
            return false;
        }

        target = server
            ? AccessTools.DeclaredMethod(typeof(ResolveCharacterState), "Handle_ClientValidate", new[] { typeof(MessagePayload<NetworkClientValidate>) })
            : AccessTools.DeclaredMethod(typeof(global::Coop.Core.Client.States.ReceivingSavedDataState), "Handle_NetworkGameSaveDataReceived",
                new[] { typeof(MessagePayload<global::Coop.Core.Client.Messages.NetworkGameSaveDataReceived>) });
        if (target == null || target.IsStatic || target.ReturnType != typeof(void))
        {
            reason = server
                ? "server validation hook Handle_ClientValidate(MessagePayload<NetworkClientValidate>) is missing"
                : "client save-load hook Handle_NetworkGameSaveDataReceived(MessagePayload<NetworkGameSaveDataReceived>) is missing";
            target = null;
            return false;
        }

        reason = "";
        return true;
    }

    public static string Describe(bool server) => server
        ? "message transport, player/object identity, and server validation hook"
        : "message transport, connection lifecycle, and client save-load hook";

    private static System.Collections.Generic.IEnumerable<string> TransportAndIdentityProblems(bool server)
    {
        var broker = typeof(IMessageBroker).GetMethods();
        if (!broker.Any(m => m.Name == "Subscribe" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1))
            yield return "IMessageBroker.Subscribe<T>";
        if (!broker.Any(m => m.Name == "Unsubscribe" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1))
            yield return "IMessageBroker.Unsubscribe<T>";

        var network = typeof(INetwork).GetMethods();
        if (!network.Any(m => m.Name == "Send" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(NetPeer)))
            yield return "INetwork.Send(NetPeer, message)";
        if (!network.Any(m => m.Name == "SendImmediate" && m.GetParameters().Length == 2
            && m.GetParameters()[0].ParameterType == typeof(NetPeer)
            && typeof(IMessage).IsAssignableFrom(m.GetParameters()[1].ParameterType)))
            yield return "INetwork.SendImmediate(NetPeer, message)";
        if (!network.Any(m => m.Name == "SendAll" && m.GetParameters().Length == 1))
            yield return "INetwork.SendAll(message)";

        if (typeof(MessagePayload<>).GetProperty("Who") == null || typeof(MessagePayload<>).GetProperty("What") == null)
            yield return "MessagePayload<T>.Who/What";
        if (typeof(Common.ModInformation).GetProperty("IsServer", BindingFlags.Public | BindingFlags.Static) == null)
            yield return "ModInformation.IsServer";

        if (server)
        {
            var players = typeof(IPlayerManager).GetMethods();
            if (!players.Any(m => m.Name == "TryGetPlayer" && m.GetParameters().Length == 2)) yield return "IPlayerManager.TryGetPlayer";
            if (!players.Any(m => m.Name == "TryGetPeer" && m.GetParameters().Length == 2)) yield return "IPlayerManager.TryGetPeer";
            if (!typeof(IObjectManager).GetMethods().Any(m => m.Name == "TryGetObject" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2))
                yield return "IObjectManager.TryGetObject<T>";
        }
        else
        {
            // Bannerlord loads Coop through its module loader. On the client that assembly can be present in the
            // AppDomain while Type.GetType's display-name binder still returns null; Harmony's loaded-assembly
            // resolver is the same mechanism used for the admission targets below.
            if (AccessTools.TypeByName("Coop.Core.Client.Messages.NetworkConnected") == null)
                yield return "NetworkConnected";
            if (AccessTools.TypeByName("Coop.Core.Client.Messages.NetworkDisconnected") == null)
                yield return "NetworkDisconnected";
        }
    }
}
