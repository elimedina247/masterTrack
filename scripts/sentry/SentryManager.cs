using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;

namespace MasterTrack.Sentry;

/// <summary>
/// The authoritative half of Sentry mode: the points ledger and the only door an action goes
/// through to become real.
///
/// Shaped exactly like tile placement (see <see cref="Tiles.TrackController"/>): the sentry's
/// client asks, the server checks the asker really is the Track Master, that the race is on and
/// that the points cover it, then broadcasts the confirmed action. Every peer applies the
/// broadcast to its own copy of the world — which matters here more than anywhere, because a car
/// is simulated on exactly one machine, and a debuff is only real on the machine that simulates
/// the car it debuffs.
///
/// Lives in the match scene on every peer at the same path, which is what lets the RPCs find it.
/// The missile is deliberately not a replicated node: its flight is deterministic from the
/// launch broadcast, so every peer flies its own copy and they agree to within a frame — the
/// same trick the track itself plays with placements.
/// </summary>
public partial class SentryManager : Node3D
{
    /// <summary>The ledger moved. Fired on every peer; only the sentry's UI listens.</summary>
    [Signal] public delegate void PointsChangedEventHandler(int remaining);

    /// <summary>Something the sentry should read: a rejection, mostly. Fired on the asker only.</summary>
    [Signal] public delegate void SentryMessageEventHandler(string text);

    /// <summary>A debuff landed on a peer's car. Fired on every peer, so HUDs can shout it.</summary>
    [Signal] public delegate void DebuffAppliedEventHandler(int peerId, int kind);

    /// <summary>What is left to spend. Server truth; clients follow the broadcast.</summary>
    public int PointsRemaining { get; private set; } = SentryActions.PointsBudget;

    private static bool Networked => NetworkManager.Instance.IsNetworked;

    public override void _Ready()
    {
        // Warm the missile's model now, while the match scene is still settling, rather than on
        // its first launch mid-race — the same reason the match warms the tile pieces. The
        // resource cache holds it from here, so the launch is an instancing and nothing more.
        GD.Load<PackedScene>(SentryMissile.ModelPath);
    }

    // ---- Requests: sentry client -> server ----

    public void RequestBouncy(int targetPeerId)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerBouncy(Multiplayer.GetUniqueId(), targetPeerId);
            return;
        }

        RpcId(1, MethodName.ServerRequestBouncy, targetPeerId);
    }

    public void RequestChain(int peerA, int peerB)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerChain(Multiplayer.GetUniqueId(), peerA, peerB);
            return;
        }

        RpcId(1, MethodName.ServerRequestChain, peerA, peerB);
    }

    public void RequestMissile(Vector3 target)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerMissile(Multiplayer.GetUniqueId(), target);
            return;
        }

        RpcId(1, MethodName.ServerRequestMissile, target);
    }

    public void RequestBarrel(Vector3 target)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerBarrel(Multiplayer.GetUniqueId(), target);
            return;
        }

        RpcId(1, MethodName.ServerRequestBarrel, target);
    }

    public void RequestBooster(int targetPeerId)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerBooster(Multiplayer.GetUniqueId(), targetPeerId);
            return;
        }

        RpcId(1, MethodName.ServerRequestBooster, targetPeerId);
    }

    public void RequestWires(int targetPeerId)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerWires(Multiplayer.GetUniqueId(), targetPeerId);
            return;
        }

        RpcId(1, MethodName.ServerRequestWires, targetPeerId);
    }

    public void RequestOilSlick(Vector3 target)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerOilSlick(Multiplayer.GetUniqueId(), target);
            return;
        }

        RpcId(1, MethodName.ServerRequestOilSlick, target);
    }

    public void RequestMoonGravity()
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerMoonGravity(Multiplayer.GetUniqueId());
            return;
        }

        RpcId(1, MethodName.ServerRequestMoonGravity);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestBouncy(int targetPeerId)
        => ServerBouncy(Multiplayer.GetRemoteSenderId(), targetPeerId);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestChain(int peerA, int peerB)
        => ServerChain(Multiplayer.GetRemoteSenderId(), peerA, peerB);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestMissile(Vector3 target)
        => ServerMissile(Multiplayer.GetRemoteSenderId(), target);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestBarrel(Vector3 target)
        => ServerBarrel(Multiplayer.GetRemoteSenderId(), target);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestBooster(int targetPeerId)
        => ServerBooster(Multiplayer.GetRemoteSenderId(), targetPeerId);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestWires(int targetPeerId)
        => ServerWires(Multiplayer.GetRemoteSenderId(), targetPeerId);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestOilSlick(Vector3 target)
        => ServerOilSlick(Multiplayer.GetRemoteSenderId(), target);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestMoonGravity()
        => ServerMoonGravity(Multiplayer.GetRemoteSenderId());

    // ---- Authority: validate, spend, broadcast ----

    private void ServerBouncy(int senderId, int targetPeerId)
    {
        if (!MaySentry(senderId))
            return;

        if (RacerOf(targetPeerId) == null)
        {
            Reject(senderId, "They're not on the track.");
            return;
        }

        if (!TrySpend(SentryActionKind.Bouncy, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyBouncy, targetPeerId);
        NotifyBouncy(targetPeerId);
    }

    private void ServerChain(int senderId, int peerA, int peerB)
    {
        if (!MaySentry(senderId))
            return;

        if (peerA == peerB || RacerOf(peerA) == null || RacerOf(peerB) == null)
        {
            Reject(senderId, "Chains need two different cars.");
            return;
        }

        if (!TrySpend(SentryActionKind.ChainedUp, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyChain, peerA, peerB);
        NotifyChain(peerA, peerB);
    }

    private void ServerMissile(int senderId, Vector3 target)
    {
        if (!MaySentry(senderId))
            return;

        if (!TrySpend(SentryActionKind.Missile, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyMissile, target);
        NotifyMissile(target);
    }

    private void ServerBarrel(int senderId, Vector3 target)
    {
        if (!MaySentry(senderId))
            return;

        if (!TrySpend(SentryActionKind.BarrelBomb, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyBarrel, target);
        NotifyBarrel(target);
    }

    private void ServerBooster(int senderId, int targetPeerId)
    {
        if (!MaySentry(senderId))
            return;

        if (RacerOf(targetPeerId) == null)
        {
            Reject(senderId, "They're not on the track.");
            return;
        }

        if (!TrySpend(SentryActionKind.RunawayBooster, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyBooster, targetPeerId);
        NotifyBooster(targetPeerId);
    }

    private void ServerWires(int senderId, int targetPeerId)
    {
        if (!MaySentry(senderId))
            return;

        if (RacerOf(targetPeerId) == null)
        {
            Reject(senderId, "They're not on the track.");
            return;
        }

        if (!TrySpend(SentryActionKind.CrossedWires, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyWires, targetPeerId);
        NotifyWires(targetPeerId);
    }

    private void ServerOilSlick(int senderId, Vector3 target)
    {
        if (!MaySentry(senderId))
            return;

        if (!TrySpend(SentryActionKind.OilSlick, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyOilSlick, target);
        NotifyOilSlick(target);
    }

    private void ServerMoonGravity(int senderId)
    {
        if (!MaySentry(senderId))
            return;

        if (!TrySpend(SentryActionKind.MoonGravity, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyMoonGravity);
        NotifyMoonGravity();
    }

    /// <summary>
    /// Server only. Whether a peer may spend sentry points right now: the race must actually be
    /// running, and the asker must hold the role. Solo skips the role half the way the track's
    /// own <c>MayBuild</c> does — there are no roles to have been dealt.
    /// </summary>
    private bool MaySentry(int senderId)
    {
        if (GameManager.Instance.Phase != MatchPhase.Racing)
            return false;

        if (!Networked)
            return true;

        if (!NetworkManager.Instance.IsHost)
            return false;

        if (senderId != GameManager.Instance.TrackMasterPeerId)
        {
            GD.PushWarning($"[Sentry] Peer {senderId} tried a sentry action but is not the Track Master.");
            return false;
        }

        return true;
    }

    /// <summary>Server only. Take the cost out of the ledger, or say why not.</summary>
    private bool TrySpend(SentryActionKind kind, int senderId)
    {
        int cost = SentryActions.CostOf(kind);
        if (PointsRemaining < cost)
        {
            Reject(senderId, $"Not enough points for {SentryActions.NameOf(kind)} — " +
                             $"{PointsRemaining} left, it costs {cost}.");
            return false;
        }

        PointsRemaining -= cost;
        GD.Print($"[Sentry] {SentryActions.NameOf(kind)} bought for {cost}; {PointsRemaining} left.");

        if (Networked)
            Rpc(MethodName.NotifyPoints, PointsRemaining);
        NotifyPoints(PointsRemaining);

        return true;
    }

    /// <summary>Server only. Tell the asker their action didn't happen, on their machine.</summary>
    private void Reject(int senderId, string reason)
    {
        if (!Networked || senderId == Multiplayer.GetUniqueId())
        {
            EmitSignal(SignalName.SentryMessage, reason);
            return;
        }

        RpcId(senderId, MethodName.NotifyRejected, reason);
    }

    // ---- Broadcasts: server -> everyone, applied identically on every peer ----

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyPoints(int remaining)
    {
        PointsRemaining = remaining;
        EmitSignal(SignalName.PointsChanged, remaining);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyRejected(string reason)
        => EmitSignal(SignalName.SentryMessage, reason);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyBouncy(int targetPeerId)
    {
        RacerOf(targetPeerId)?.ApplyBouncy(SentryActions.BouncyDuration);
        EmitSignal(SignalName.DebuffApplied, targetPeerId, (int)SentryActionKind.Bouncy);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyChain(int peerA, int peerB)
    {
        RacerController? a = RacerOf(peerA);
        RacerController? b = RacerOf(peerB);

        // A racer can vanish between the server's check and this landing — a drop mid-flight.
        // A chain to nobody is no chain; skipping is better than a rope to a freed node.
        if (a == null || b == null)
            return;

        a.ApplyChain(b, SentryActions.ChainDuration);
        b.ApplyChain(a, SentryActions.ChainDuration);

        var visual = new SentryChainVisual { Name = "ChainVisual" };
        AddChild(visual);
        visual.Initialize(a, b, SentryActions.ChainDuration);

        EmitSignal(SignalName.DebuffApplied, peerA, (int)SentryActionKind.ChainedUp);
        EmitSignal(SignalName.DebuffApplied, peerB, (int)SentryActionKind.ChainedUp);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyMissile(Vector3 target)
    {
        var missile = new SentryMissile { Name = "Missile", Position = target };
        AddChild(missile);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyBarrel(Vector3 target)
    {
        var barrel = new SentryBarrelBomb { Name = "BarrelBomb", Position = target };
        AddChild(barrel);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyBooster(int targetPeerId)
    {
        RacerOf(targetPeerId)?.ApplyRunawayBooster(SentryActions.LeadSeconds, SentryActions.BoosterDuration);
        EmitSignal(SignalName.DebuffApplied, targetPeerId, (int)SentryActionKind.RunawayBooster);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyWires(int targetPeerId)
    {
        RacerOf(targetPeerId)?.ApplyCrossedWires(SentryActions.LeadSeconds, SentryActions.WiresDuration);
        EmitSignal(SignalName.DebuffApplied, targetPeerId, (int)SentryActionKind.CrossedWires);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyOilSlick(Vector3 target)
    {
        var slick = new SentryOilSlick { Name = "OilSlick", Position = target };
        AddChild(slick);
    }

    /// <summary>The moon lands on everybody at once — every car this peer knows about gets the
    /// same fuse, and the <c>DebuffApplied</c> peer id of 0 means "all of you" to the HUD.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyMoonGravity()
    {
        foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
        {
            if (node is RacerController racer && racer.IsInsideTree())
                racer.ApplyMoonGravity(SentryActions.LeadSeconds, SentryActions.MoonGravityDuration);
        }

        EmitSignal(SignalName.DebuffApplied, 0, (int)SentryActionKind.MoonGravity);
    }

    /// <summary>A peer's car, found the way the board finds them: through the group.</summary>
    private RacerController? RacerOf(int peerId)
    {
        foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
        {
            if (node is RacerController racer && racer.OwnerPeerId == peerId
                && IsInstanceValid(racer) && racer.IsInsideTree())
                return racer;
        }

        return null;
    }
}
