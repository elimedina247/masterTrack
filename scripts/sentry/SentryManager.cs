using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using System.Collections.Generic;

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

    /// <summary>An action went on cooldown. Fired on every peer; only the sentry's UI listens.</summary>
    [Signal] public delegate void CooldownStartedEventHandler(int kind, float seconds);

    /// <summary>
    /// A blast resolved, and these peers' cars were inside it. Fired on every peer off its own
    /// copy of the detonation — the count is geometry against replicated poses, so no packet
    /// carries it — and only the sentry's UI listens. An empty array is a miss, which is worth
    /// saying out loud: a shot with no answer is the thing this signal exists to end.
    /// </summary>
    [Signal] public delegate void BlastLandedEventHandler(int[] caughtPeerIds);

    /// <summary>
    /// A placed action was confirmed at a spot in the world. Fired on every peer alongside the
    /// spawn it announces; the sentry's board uses it to fly the follow-through camera to the
    /// consequence. Confirmed rather than requested on purpose — a camera that flew off to watch
    /// a rejection would be selling something that never happened.
    /// </summary>
    [Signal] public delegate void ActionPlacedEventHandler(int kind, Vector3 target);

    /// <summary>What is left to spend. Server truth; clients follow the broadcast.</summary>
    public int PointsRemaining { get; private set; }

    /// <summary>Seconds banked toward the next regen tick. Server only.</summary>
    private float _regenBank;

    /// <summary>
    /// When each action comes off cooldown, on this machine's clock. The server's copy is the
    /// one that gates; every client keeps its own from the broadcast, purely so the bar can
    /// count down without asking — the same each-peer-runs-its-own-clock trick the build timer
    /// uses. A few network milliseconds of disagreement changes a label, never a decision.
    /// </summary>
    private readonly Dictionary<SentryActionKind, ulong> _cooldownUntil = new();

    private static bool Networked => NetworkManager.Instance.IsNetworked;

    public override void _Ready()
    {
        // The pool starts full at whatever the host picked in the lobby. Every peer seeds the
        // same number from the replicated setting; the server's broadcasts keep it true from here.
        PointsRemaining = GameManager.Instance.SentryPointLimit;

        // Warm the missile's model now, while the match scene is still settling, rather than on
        // its first launch mid-race — the same reason the match warms the tile pieces. The
        // resource cache holds it from here, so the launch is an instancing and nothing more.
        GD.Load<PackedScene>(SentryMissile.ModelPath);
    }

    /// <summary>
    /// The regen drip: while the race runs, the server hands points back once a second up to the
    /// lobby's limit, through the same broadcast every spend uses — so clients never do their own
    /// arithmetic and the ledger stays one machine's opinion.
    /// </summary>
    public override void _Process(double delta)
    {
        if (Networked && !Multiplayer.IsServer())
            return;

        if (GameManager.Instance.Phase != MatchPhase.Racing)
            return;

        int limit = GameManager.Instance.SentryPointLimit;
        if (PointsRemaining >= limit)
        {
            _regenBank = 0.0f;
            return;
        }

        _regenBank += (float)delta;
        if (_regenBank < 1.0f)
            return;

        _regenBank -= 1.0f;
        PointsRemaining = Mathf.Min(PointsRemaining + SentryActions.RegenPerSecond, limit);

        if (Networked)
            Rpc(MethodName.NotifyPoints, PointsRemaining);
        NotifyPoints(PointsRemaining);
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

    public void RequestMagnet(Vector3 target)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerMagnet(Multiplayer.GetUniqueId(), target);
            return;
        }

        RpcId(1, MethodName.ServerRequestMagnet, target);
    }

    public void RequestCargoSpill(Vector3 target)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerCargoSpill(Multiplayer.GetUniqueId(), target);
            return;
        }

        RpcId(1, MethodName.ServerRequestCargoSpill, target);
    }

    public void RequestPopUpRamp(int tileIndex, int slotIndex)
    {
        if (!Networked || Multiplayer.IsServer())
        {
            ServerPopUpRamp(Multiplayer.GetUniqueId(), tileIndex, slotIndex);
            return;
        }

        RpcId(1, MethodName.ServerRequestPopUpRamp, tileIndex, slotIndex);
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

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestMagnet(Vector3 target)
        => ServerMagnet(Multiplayer.GetRemoteSenderId(), target);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestCargoSpill(Vector3 target)
        => ServerCargoSpill(Multiplayer.GetRemoteSenderId(), target);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestPopUpRamp(int tileIndex, int slotIndex)
        => ServerPopUpRamp(Multiplayer.GetRemoteSenderId(), tileIndex, slotIndex);

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

    private void ServerMagnet(int senderId, Vector3 target)
    {
        if (!MaySentry(senderId))
            return;

        if (!TrySpend(SentryActionKind.Magnet, senderId))
            return;

        if (Networked)
            Rpc(MethodName.NotifyMagnet, target);
        NotifyMagnet(target);
    }

    private void ServerCargoSpill(int senderId, Vector3 target)
    {
        if (!MaySentry(senderId))
            return;

        if (!TrySpend(SentryActionKind.CargoSpill, senderId))
            return;

        // The seed is the server's roll, made once — every peer grows the same rainfall from
        // it. See SentryCargoSpill.
        int seed = unchecked((int)GD.Randi());

        if (Networked)
            Rpc(MethodName.NotifyCargoSpill, target, seed);
        NotifyCargoSpill(target, seed);
    }

    /// <summary>
    /// Server only. The pop-up ramp goes through the track's own hazard pipeline rather than a
    /// sentry Notify: the slot is checked <i>before</i> the points move, then
    /// <see cref="Tiles.TrackController.ServerCommitHazard"/> broadcasts the same confirmation
    /// every other hazard placement rides — composition at race time, never a tile swap.
    /// </summary>
    private void ServerPopUpRamp(int senderId, int tileIndex, int slotIndex)
    {
        if (!MaySentry(senderId))
            return;

        if (GetTree().GetFirstNodeInGroup(Tiles.TrackController.GroupName)
                is not Tiles.TrackController track)
            return;

        if (!track.CanPlaceHazard(tileIndex, slotIndex, Tiles.HazardKind.PopUpRamp, out string reason))
        {
            Reject(senderId, $"No ramp there — {reason}.");
            return;
        }

        if (!TrySpend(SentryActionKind.PopUpRamp, senderId))
            return;

        track.ServerCommitHazard(tileIndex, slotIndex, Tiles.HazardKind.PopUpRamp, out _);
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

    /// <summary>Seconds until an action may fire again, or 0 when it is ready. For the bar.</summary>
    public float CooldownLeft(SentryActionKind kind)
        => _cooldownUntil.TryGetValue(kind, out ulong until) && Time.GetTicksMsec() < until
            ? (until - Time.GetTicksMsec()) / 1000.0f
            : 0.0f;

    /// <summary>
    /// Server only. Take the cost out of the ledger and start the cooldown, or say why not.
    /// Two gates rather than one because they guard different failure modes: the cooldown stops
    /// one tool being spammed, the pool stops the whole kit being fired at once.
    /// </summary>
    private bool TrySpend(SentryActionKind kind, int senderId)
    {
        float reloading = CooldownLeft(kind);
        if (reloading > 0.0f)
        {
            Reject(senderId, $"{SentryActions.NameOf(kind)} is reloading — {reloading:0}s.");
            return false;
        }

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
        {
            Rpc(MethodName.NotifyPoints, PointsRemaining);
            Rpc(MethodName.NotifyCooldown, (int)kind, SentryActions.CooldownOf(kind));
        }
        NotifyPoints(PointsRemaining);
        NotifyCooldown((int)kind, SentryActions.CooldownOf(kind));

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
    private void NotifyCooldown(int kind, float seconds)
    {
        _cooldownUntil[(SentryActionKind)kind] = Time.GetTicksMsec() + (ulong)(seconds * 1000.0f);
        EmitSignal(SignalName.CooldownStarted, kind, seconds);
    }

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
        EmitSignal(SignalName.ActionPlaced, (int)SentryActionKind.Missile, target);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyBarrel(Vector3 target)
    {
        var barrel = new SentryBarrelBomb { Name = "BarrelBomb", Position = target };
        AddChild(barrel);
        EmitSignal(SignalName.ActionPlaced, (int)SentryActionKind.BarrelBomb, target);
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
        EmitSignal(SignalName.ActionPlaced, (int)SentryActionKind.OilSlick, target);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyMagnet(Vector3 target)
    {
        var magnet = new SentryMagnet { Name = "Magnet", Position = target };
        AddChild(magnet);
        EmitSignal(SignalName.ActionPlaced, (int)SentryActionKind.Magnet, target);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyCargoSpill(Vector3 target, int seed)
    {
        var spill = new SentryCargoSpill { Name = "CargoSpill", Position = target, Seed = seed };
        AddChild(spill);
        EmitSignal(SignalName.ActionPlaced, (int)SentryActionKind.CargoSpill, target);
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

    /// <summary>Called by <see cref="SentryBlast"/> when a detonation resolves: who was inside
    /// it. Just a relay onto the signal — the blast is a static helper and cannot emit.</summary>
    public void ReportBlast(int[] caughtPeerIds)
        => EmitSignal(SignalName.BlastLanded, caughtPeerIds);

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
