using Godot;
using MasterTrack.Networking;
using MasterTrack.Tiles;
using System.Collections.Generic;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The board's hazard gestures: arm a hazard from the tray, and every slot it could go in
/// lights up on the road — click one to place it, right-click to put it away. The mirror of
/// the sentry partial, wearing a shop rather than a hand: every hazard is always on the shelf
/// at a price, and <see cref="HazardFunds"/> is what the builder has to spend across the whole
/// track. What used to be "will I be dealt one" is now "is this spot worth a spring trap".
///
/// View-and-input half only, the standing split: a click on a lit slot becomes a request to
/// <see cref="TrackController"/>, and the server decides what is real. The highlights are the
/// affordance that makes the whole feature self-explanatory — the builder never has to know
/// which pieces carry slots, because the road itself says so the moment a hazard is in hand.
///
/// Placement spends optimistically after running the server's own checks locally, the way the
/// tile flow does: the only way the server then disagrees is a race against a crumbling tile,
/// and a hazard lost to road that stopped existing was lost either way.
/// </summary>
public partial class TrackMasterController
{
	/// <summary>
	/// Fired when the builder's hazard money changes. <c>delta</c> is what just moved — negative
	/// for a purchase, positive for a refund — because the readout wants to show the change and
	/// not only the new total. A balance that silently drops from 510 to 425 tells the builder
	/// something happened; a red <c>-$85</c> beside it tells them what.
	/// </summary>
	[Signal] public delegate void HazardFundsChangedEventHandler(int funds, int delta);

	/// <summary>What the tray's status line should say about hazard placement.</summary>
	[Signal] public delegate void HazardStatusChangedEventHandler(string text);

	/// <summary>
	/// The builder's hazard money before the per-tile allowance, in dollars.
	///
	/// A shop rather than a dealt hand, and the difference is not cosmetic. A hand said "here are
	/// three hazards, take what you are given" — the tile deck's pressure applied to a decision
	/// that does not want it. What the builder is actually doing is furnishing a track they have
	/// just designed, and the interesting question is <i>which</i> device goes <i>where</i>, not
	/// whether the deck will hand them one. A price list asks that question directly: everything
	/// is always available, and a spring trap is one fewer pair of launch pads.
	/// </summary>
	[Export] public int HazardFundsBase { get; set; } = 150;

	/// <summary>Extra dollars per tile of race length. A long track has more road to furnish and
	/// more of it the racers will only see once.</summary>
	[Export] public int HazardFundsPerTile { get; set; } = 18;

	/// <summary>How far a click can miss a lit slot and still take it, in screen pixels. The
	/// marker-pick radius, because it is the same gesture aimed at road instead of cars.</summary>
	private const float SlotPickRadiusPx = 70.0f;

	/// <summary>
	/// What the builder has left to spend. Sized off the host's race length, so a fifty-tile
	/// track is furnished at the same density as a ten-tile one.
	///
	/// <b>Lives on the builder's machine</b>, the hand's trust model unchanged: the server never
	/// checks the price, only that the placement itself is legal — which slot, which kind, is it
	/// free. A client that lied about its money could only give itself hazards the server was
	/// happy to allow anyway, and the honest reading is that the budget is a design constraint on
	/// a cooperative role rather than a thing to defend.
	/// </summary>
	public int HazardFunds
	{
		get
		{
			_hazardFunds ??= HazardFundsBase
							 + HazardFundsPerTile * GameManager.Instance.RaceLength;
			return _hazardFunds.Value;
		}
		private set
		{
			// Read through the property rather than the field so the opening balance is settled
			// before the first spend measures itself against it.
			int before = HazardFunds;
			_hazardFunds = Mathf.Max(0, value);
			EmitSignal(SignalName.HazardFundsChanged, _hazardFunds.Value,
					   _hazardFunds.Value - before);
		}
	}

	private int? _hazardFunds;

	/// <summary>The till. A cartoon register, because the shop is a cartoon shop.</summary>
	private const string CashSfxPath = "res://assets/audio/cash.mp3";

	/// <summary>Whether the builder could buy this right now. Free build buys everything —
	/// the lobby is a tool, not a game, and pricing it would only be in the way.</summary>
	public bool CanAfford(HazardKind kind) => FreeBuild || HazardFunds >= kind.PriceOf();

	/// <summary>
	/// What is on the shelves this match. <b>This is where the two phased modes actually differ.</b>
	///
	/// They share the flow, the clock, the camera, the money and the placement gesture; what
	/// makes Tower Defense a different game is that its rig phase sells things that play
	/// themselves, and Sentry's sells things the builder has to time by hand. Mixing the two
	/// shelves would blur both — a sentry with turrets stops watching the road, and a tower
	/// defense with hand-fired traps is just Sentry with taller scenery.
	///
	/// Free build ignores the split and shows everything, the way it ignores the prices: the
	/// lobby board is for looking at things.
	/// </summary>
	public IReadOnlyList<HazardKind> ShopKinds
	{
		get
		{
			bool towersOnly = !FreeBuild
							  && GameManager.Instance.Mode == GameMode.TowerDefense;
			var kinds = new List<HazardKind>();

			foreach (HazardKind kind in System.Enum.GetValues<HazardKind>())
			{
				bool isTower = kind == HazardKind.RocketTower;
				if (FreeBuild || isTower == towersOnly)
					kinds.Add(kind);
			}

			return kinds;
		}
	}

	/// <summary>What the shop calls itself. The shelves are the mode, so the sign is too.</summary>
	public string ShopTitle
		=> !FreeBuild && GameManager.Instance.Mode == GameMode.TowerDefense
			? "Turrets"
			: "Hazards";

	/// <summary>
	/// Whether the Fire tool belongs on screen. Tower Defense has nothing to fire by hand — its
	/// devices do their own timing — and a button that could never do anything is worse than no
	/// button: it reads as a mechanic the player has failed to find.
	/// </summary>
	public bool FireToolAvailable
		=> FreeBuild || GameManager.Instance.Mode != GameMode.TowerDefense;

	private bool _hazardArmed;
	private HazardKind _armedHazard;

	/// <summary>Lift mode: the next click takes a placed hazard back into the hand.</summary>
	private bool _liftArmed;

	/// <summary>Fire mode: clicks fire rigged devices, and keep firing until it is cancelled.
	/// Latched rather than one-shot on purpose — during a race the sentry is playing the traps
	/// they rigged, and making them re-arm the mode between presses would be making them fight
	/// the UI for the one thing the phase is about.</summary>
	private bool _fireArmed;

	/// <summary>One lit slot: where it is, and its address for the request.</summary>
	private readonly record struct SlotHighlight(int TileIndex, int SlotIndex, Vector3 World);

	private readonly List<SlotHighlight> _litSlots = new();
	private readonly List<MeshInstance3D> _slotMarkers = new();
	private StandardMaterial3D? _slotPaint;

	/// <summary>The ghosted hazard riding the slot nearest the cursor, or null while nothing is
	/// armed. Rebuilt when it moves to a different slot, not every frame.</summary>
	private TrackHazard? _hazardGhost;

	/// <summary>Which slot the ghost is currently sitting on, so a cursor wandering inside one
	/// slot's catchment does not rebuild the preview sixty times a second.</summary>
	private (int Tile, int Slot) _ghostAt = (-1, -1);

	/// <summary>Whether the track's change signals are currently feeding the highlights.</summary>
	private bool _hazardHooksOn;

	/// <summary>
	/// Called by the shop when the builder picks a hazard off the shelf: light up everywhere it
	/// could go. Nothing is charged here — the money moves when the thing actually lands, so
	/// browsing is free and a cancelled purchase costs nothing.
	/// </summary>
	public void ArmHazardPurchase(int kind)
	{
		if (Track == null || !System.Enum.IsDefined((HazardKind)kind))
		{
			CancelHazardPlacement();
			return;
		}

		var wanted = (HazardKind)kind;

		if (!CanAfford(wanted))
		{
			CancelHazardPlacement();
			EmitSignal(SignalName.HazardStatusChanged,
					   $"{wanted.DisplayName()} costs ${wanted.PriceOf()} — you have ${HazardFunds}.");
			return;
		}

		_liftArmed = false;
		_hazardArmed = true;
		_armedHazard = wanted;

		HookHazardSignals(true);
		RebuildSlotHighlights();

		EmitSignal(SignalName.HazardStatusChanged, _litSlots.Count == 0
			? $"{_armedHazard.DisplayName()}: no free spot on the track yet — slotted pieces light up green."
			: $"{_armedHazard.DisplayName()}, ${_armedHazard.PriceOf()}: click a lit spot on the road. Right-click to put it back.");
	}

	public void CancelHazardPlacement()
	{
		HookHazardSignals(false);
		ClearSlotHighlights();

		if (!_hazardArmed && !_liftArmed && !_fireArmed)
			return;

		_hazardArmed = false;
		_liftArmed = false;
		_fireArmed = false;
		EmitSignal(SignalName.HazardStatusChanged, "");
	}

	/// <summary>Called by the tray's Lift button: the next click on a placed hazard takes it
	/// back into the hand instead of the road.</summary>
	public void ArmHazardLift()
	{
		CancelHazardPlacement();
		_liftArmed = true;
		EmitSignal(SignalName.HazardStatusChanged,
				   "Lift: click a placed hazard to take it back. Right-click to cancel.");
	}

	/// <summary>Called by the tray's Fire button: clicks now fire rigged devices. The race-phase
	/// gesture, and the whole of what the sentry does once the rig is down.</summary>
	public void ArmHazardFire()
	{
		CancelHazardPlacement();
		_fireArmed = true;
		EmitSignal(SignalName.HazardStatusChanged,
				   "Fire: click a rigged trap to set it off. Right-click to stop.");
	}

	/// <summary>
	/// First look at input while a hazard is armed, slotted between the sentry's gestures and
	/// the board's own. Wheel and camera-look fall through on purpose, the sentry's rule.
	/// </summary>
	private bool HandleHazardInput(InputEvent @event)
	{
		if ((!_hazardArmed && !_liftArmed && !_fireArmed) || Track == null)
			return false;

		if (@event is not InputEventMouseButton { Pressed: true } mouse)
			return false;

		// The right button is <c>camera_look</c>, and while the board is being flown by hand it
		// belongs to the camera. Handing it to "cancel" instead is what left the sentry unable to
		// turn their head at all once firing became the resting state of the race phase — every
		// right-click was being spent disarming and re-arming the same mode. A tool can always be
		// put away by clicking its button again; a camera that cannot look has no other door.
		if (mouse.ButtonIndex == MouseButton.Right && CameraMode == BoardCameraMode.FreeRoam)
			return false;

		switch (mouse.ButtonIndex)
		{
			case MouseButton.Right:
				CancelHazardPlacement();

				// Firing is the resting state of the race phase, not a mode you visit: the whole
				// job is pressing traps at the right moment, and a right-click that left the
				// sentry holding nothing would be a right-click that disarmed them.
				if (GameManager.Instance.Phase == MatchPhase.Racing)
					ArmHazardFire();

				return true;

			case MouseButton.Left:
				if (_fireArmed)
					FireHazardNear(mouse.Position);
				else if (_liftArmed)
					LiftHazardNear(mouse.Position);
				else
					PickHazardSlot(mouse.Position);
				return true;

			default:
				return false;
		}
	}

	/// <summary>
	/// The placement click: the lit slot nearest the cursor, picked in screen space exactly the
	/// way racer markers are — screen distance is what "I clicked that one" means from board
	/// altitude.
	/// </summary>
	private void PickHazardSlot(Vector2 screenPosition)
	{
		SlotHighlight? best = null;
		float bestDistance = SlotPickRadiusPx;

		foreach (SlotHighlight lit in _litSlots)
		{
			if (_camera.IsPositionBehind(lit.World))
				continue;

			float distance = _camera.UnprojectPosition(lit.World).DistanceTo(screenPosition);
			if (distance >= bestDistance)
				continue;

			bestDistance = distance;
			best = lit;
		}

		if (best is not { } target)
		{
			EmitSignal(SignalName.HazardStatusChanged, "No spot there — click one of the lit markers.");
			return;
		}

		// The server's own checks, run here first so an illegal click says why on the spot and
		// spends nothing — the tile flow's rule.
		if (!Track!.IsSlotFree(target.TileIndex, target.SlotIndex)
			|| GameManager.Instance.Phase == MatchPhase.Racing)
		{
			RebuildSlotHighlights();
			EmitSignal(SignalName.HazardStatusChanged, "That spot is gone — pick another.");
			return;
		}

		// Re-checked at the till as well as at the shelf: the shop stays armed across a placement,
		// so the click that empties the wallet and the click after it are the same gesture.
		if (!CanAfford(_armedHazard))
		{
			EmitSignal(SignalName.HazardStatusChanged,
					   $"Not enough — {_armedHazard.DisplayName()} costs ${_armedHazard.PriceOf()}.");
			return;
		}

		HazardKind bought = _armedHazard;

		if (!FreeBuild)
			HazardFunds -= bought.PriceOf();

		// Rung at the purchase rather than off the funds signal, because that signal also fires
		// on a refund — and a till that goes off when money comes back is a till that means
		// nothing. Free build gets the sound too: the click still bought something, it was just
		// free, and silence there would read as a placement that failed.
		Audio.Sfx.PlayUi(this, CashSfxPath, volumeDb: -2.0f, pitchJitter: 0.05f);

		Track.RequestPlaceHazard(target.TileIndex, target.SlotIndex, bought);

		CancelHazardPlacement();
		EmitSignal(SignalName.HazardStatusChanged,
				   $"{bought.DisplayName()} placed — ${HazardFunds} left.");
	}

	/// <summary>
	/// Lift a placed hazard back into the hand: called by the tray's lift mode, aimed the same
	/// screen-space way. The refund is optimistic — we are the only peer that ever removes, so
	/// the only race is against a crumbling tile, and a hazard that went down with its road
	/// coming back as a card is a small gift, not a bug.
	/// </summary>
	public void LiftHazardNear(Vector2 screenPosition)
	{
		if (Track == null || GameManager.Instance.Phase == MatchPhase.Racing)
			return;

		PlacedHazard? best = null;
		float bestDistance = SlotPickRadiusPx;

		foreach (PlacedHazard placed in Track.Hazards)
		{
			if (Track.SlotWorldTransform(placed.TileIndex, placed.SlotIndex) is not { } at
				|| _camera.IsPositionBehind(at.Origin))
				continue;

			float distance = _camera.UnprojectPosition(at.Origin).DistanceTo(screenPosition);
			if (distance >= bestDistance)
				continue;

			bestDistance = distance;
			best = placed;
		}

		if (best is not { } target)
		{
			EmitSignal(SignalName.HazardStatusChanged, "No hazard there to lift.");
			return;
		}

		Track.RequestRemoveHazard(target.TileIndex, target.SlotIndex);

		// Refunded in full. A lift is a change of mind about *where*, not something to be taxed
		// for — a restocking fee would only teach the builder to leave bad placements where they
		// fell rather than fix them.
		if (!FreeBuild)
			HazardFunds += target.Kind.PriceOf();

		CancelHazardPlacement();
		EmitSignal(SignalName.HazardStatusChanged,
				   $"{target.Kind.DisplayName()} lifted — ${HazardFunds} back in the pot.");
	}

	/// <summary>
	/// Fire the rigged device nearest the click, aimed the same screen-space way as Lift.
	///
	/// Only devices that are actually ready are candidates, so a press near a plate that is
	/// already in the air reaches past it to the next one rather than being swallowed — with a
	/// pack strung out across a tile that is nearly always what the sentry meant. The mode stays
	/// armed afterwards: the next trap is one click away, which is the whole rhythm of the phase.
	/// </summary>
	public void FireHazardNear(Vector2 screenPosition)
	{
		if (Track == null)
			return;

		PlacedHazard? best = null;
		float bestDistance = SlotPickRadiusPx;

		foreach (PlacedHazard placed in Track.Hazards)
		{
			if (Track.HazardAt(placed.TileIndex, placed.SlotIndex)
				is not { CanDetonate: true, IsReady: true })
				continue;

			if (Track.SlotWorldTransform(placed.TileIndex, placed.SlotIndex) is not { } at
				|| _camera.IsPositionBehind(at.Origin))
				continue;

			float distance = _camera.UnprojectPosition(at.Origin).DistanceTo(screenPosition);
			if (distance >= bestDistance)
				continue;

			bestDistance = distance;
			best = placed;
		}

		// Said on both lines because the two phases read different ones: the tray's status is the
		// builder's during the rig, and by race time the palette has given the screen to the
		// sentry bar. One call, and whichever line is on screen is the one that answers.
		if (best is not { } target)
		{
			SayFireStatus("Nothing armed there — click a rigged trap.");
			return;
		}

		Track.RequestDetonateHazard(target.TileIndex, target.SlotIndex);
		SayFireStatus($"{target.Kind.DisplayName()} fired!");
	}

	private void SayFireStatus(string text)
	{
		EmitSignal(SignalName.HazardStatusChanged, text);
		EmitSignal(SignalName.SentryStatusChanged, text);
	}

	/// <summary>While armed, the road can change under the highlights — a tile lands, a slot
	/// fills, the tail crumbles — so they follow the track's own signals.</summary>
	private void HookHazardSignals(bool on)
	{
		if (Track == null || !IsInstanceValid(Track) || on == _hazardHooksOn)
			return;

		if (on)
		{
			Track.TrackHeadChanged += RebuildSlotHighlights;
			Track.HazardPlaced += OnHazardBoardChanged;
		}
		else
		{
			Track.TrackHeadChanged -= RebuildSlotHighlights;
			Track.HazardPlaced -= OnHazardBoardChanged;
		}

		_hazardHooksOn = on;
	}

	private void OnHazardBoardChanged(int tileIndex, int slotIndex, int kind)
		=> RebuildSlotHighlights();

	/// <summary>
	/// One lit marker over every free slot the armed hazard fits, across every live tile. A
	/// race tops out around a hundred tiles at three slots each; unshaded discs at that count
	/// are nothing, and the builder gets the whole answer at a glance.
	/// </summary>
	private void RebuildSlotHighlights()
	{
		ClearSlotHighlights();

		if (!_hazardArmed || Track == null)
			return;

		_slotPaint ??= new StandardMaterial3D
		{
			AlbedoColor = new Color(0.35f, 1.0f, 0.45f, 0.55f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			// Drawn through the road rather than clipped by it: a slot on the far side of a
			// crest still shows, which is the whole point of lighting them all.
			NoDepthTest = true,
		};

		HazardSlotKind wanted = _armedHazard.SlotKind();

		for (int tileIndex = Track.Grid.OldestLiveIndex; tileIndex < Track.Grid.Count; tileIndex++)
		{
			IReadOnlyList<Tiles.Tool.PieceSlot> slots = Track.SlotsOf(tileIndex);

			for (int slotIndex = 0; slotIndex < slots.Count; slotIndex++)
			{
				if (slots[slotIndex].Kind != wanted || !Track.IsSlotFree(tileIndex, slotIndex))
					continue;

				if (Track.SlotWorldTransform(tileIndex, slotIndex) is not { } at)
					continue;

				var marker = new MeshInstance3D
				{
					Mesh = new TorusMesh
					{
						InnerRadius = 4.6f,
						OuterRadius = 6.0f,
						Material = _slotPaint,
					},
					Transform = at,
				};
				AddChild(marker);
				marker.GlobalPosition += at.Basis.Y * 0.5f;

				_slotMarkers.Add(marker);
				_litSlots.Add(new SlotHighlight(tileIndex, slotIndex, marker.GlobalPosition));
			}
		}
	}

	private void ClearSlotHighlights()
	{
		foreach (MeshInstance3D marker in _slotMarkers)
			marker.QueueFree();

		_slotMarkers.Clear();
		_litSlots.Clear();
		ClearHazardGhost();
	}

	/// <summary>
	/// Keep a ghost of the armed hazard on whichever lit slot the cursor is nearest — which is
	/// also, exactly, the slot a click would take. Called from the board's <c>_Process</c> beside
	/// the sentry's aim ghost, and the same promise: what the builder is shown is what they are
	/// about to get, at full size, in place.
	/// </summary>
	private void UpdateHazardGhost()
	{
		if (!_hazardArmed || Track == null || _litSlots.Count == 0)
		{
			ClearHazardGhost();
			return;
		}

		Vector2 mouse = GetViewport().GetMousePosition();

		SlotHighlight? nearest = null;
		float bestDistance = float.MaxValue;

		foreach (SlotHighlight lit in _litSlots)
		{
			if (_camera.IsPositionBehind(lit.World))
				continue;

			float distance = _camera.UnprojectPosition(lit.World).DistanceTo(mouse);
			if (distance >= bestDistance)
				continue;

			bestDistance = distance;
			nearest = lit;
		}

		if (nearest is not { } target)
		{
			ClearHazardGhost();
			return;
		}

		// Rebuilt only when the answer changes. Instancing a gantry every frame would be a scene
		// load per frame, and the ghost would flicker as it went.
		if (_hazardGhost != null && _ghostAt == (target.TileIndex, target.SlotIndex))
			return;

		ClearHazardGhost();

		if (Track.SlotWorldTransform(target.TileIndex, target.SlotIndex) is not { } at)
			return;

		TrackHazard ghost = TrackHazard.Create(_armedHazard);
		ghost.Name = "HazardGhost";
		ghost.MakeGhost(new Color(0.35f, 1.0f, 0.45f));
		ghost.Transform = at;
		AddChild(ghost);

		_hazardGhost = ghost;
		_ghostAt = (target.TileIndex, target.SlotIndex);
	}

	private void ClearHazardGhost()
	{
		if (_hazardGhost == null)
			return;

		_hazardGhost.QueueFree();
		_hazardGhost = null;
		_ghostAt = (-1, -1);
	}

	/// <summary>
	/// The controller's only <c>_ExitTree</c>, living in this partial. Three duties: take the
	/// track's and the sentry's signal handlers back by hand (a C# <c>+=</c> outlives the
	/// control that made it), and free the shared marker paint while the engine is alive — the
	/// exit-crash rule. The aim ghost's paint goes with its ghost in <c>ClearSentryAim</c>.
	/// </summary>
	public override void _ExitTree()
	{
		HookHazardSignals(false);
		UnhookSentry();
		ClearSentryAim();
		_slotPaint?.Dispose();
		_slotPaint = null;
	}
}
