using Godot;
using MasterTrack.Networking;
using MasterTrack.Tiles;
using System.Collections.Generic;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The board's hazard gestures: arm a hazard from the tray, and every slot it could go in
/// lights up on the road — click one to place it, right-click to put it away. The mirror of
/// the sentry partial, wearing the tile tray's economy: what there is to place comes from
/// <see cref="HazardHand"/>, dealt on its own slow clock in its own tray row.
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
	/// <summary>Fired when the hazard hand changes — dealt, spent, or refunded.</summary>
	[Signal] public delegate void HazardHandChangedEventHandler();

	/// <summary>What the tray's status line should say about hazard placement.</summary>
	[Signal] public delegate void HazardStatusChangedEventHandler(string text);

	/// <summary>How many hazards the builder can have waiting at once.</summary>
	[Export] public int HazardHandSlots { get; set; } = 3;

	/// <summary>
	/// Seconds between dealt hazards. An order slower than the tile deal on purpose: tiles are
	/// the job, hazards are the seasoning, and the cadence — not a price — is what stops every
	/// straight becoming a minefield by lap two.
	/// </summary>
	[Export] public float HazardDealInterval { get; set; } = 25.0f;

	/// <summary>Hazards already in hand when the match starts.</summary>
	[Export] public int StartingHazards { get; set; } = 1;

	/// <summary>How far a click can miss a lit slot and still take it, in screen pixels. The
	/// marker-pick radius, because it is the same gesture aimed at road instead of cars.</summary>
	private const float SlotPickRadiusPx = 70.0f;

	public HazardHand HazardHand => _hazardHand ??=
		new HazardHand(HazardHandSlots, HazardDealInterval, StartingHazards);

	private HazardHand? _hazardHand;

	private bool _hazardArmed;
	private HazardKind _armedHazard;

	/// <summary>Lift mode: the next click takes a placed hazard back into the hand.</summary>
	private bool _liftArmed;

	/// <summary>Tray slot the armed hazard came from, so the spend hits the right card.</summary>
	private int _armedHandSlot;

	/// <summary>One lit slot: where it is, and its address for the request.</summary>
	private readonly record struct SlotHighlight(int TileIndex, int SlotIndex, Vector3 World);

	private readonly List<SlotHighlight> _litSlots = new();
	private readonly List<MeshInstance3D> _slotMarkers = new();
	private StandardMaterial3D? _slotPaint;

	/// <summary>Whether the track's change signals are currently feeding the highlights.</summary>
	private bool _hazardHooksOn;

	/// <summary>How many cards the hazard tray shows: the hand, or every kind in free build —
	/// the tile tray's split, for the tile tray's reason.</summary>
	public int HazardTrayLength
		=> FreeBuild ? System.Enum.GetValues<HazardKind>().Length : HazardHand.SlotCount;

	/// <summary>The kind a hazard tray position offers as an int, or <see cref="HazardHand.Empty"/>.</summary>
	public int HazardKindAt(int slot)
	{
		if (!FreeBuild)
			return HazardHand.At(slot);

		return slot >= 0 && slot < System.Enum.GetValues<HazardKind>().Length
			? slot
			: HazardHand.Empty;
	}

	/// <summary>Called by the tray when the builder clicks a hazard card: light up everywhere
	/// it could go. Re-arming swaps cleanly; an empty card disarms.</summary>
	public void ArmHazardPlacement(int handSlot)
	{
		int kind = HazardKindAt(handSlot);
		if (kind == HazardHand.Empty || Track == null)
		{
			CancelHazardPlacement();
			return;
		}

		_liftArmed = false;
		_hazardArmed = true;
		_armedHazard = (HazardKind)kind;
		_armedHandSlot = handSlot;

		HookHazardSignals(true);
		RebuildSlotHighlights();

		EmitSignal(SignalName.HazardStatusChanged, _litSlots.Count == 0
			? $"{_armedHazard.DisplayName()}: no free spot on the track yet — slotted pieces light up green."
			: $"{_armedHazard.DisplayName()}: click a lit spot on the road. Right-click to put it back.");
	}

	public void CancelHazardPlacement()
	{
		HookHazardSignals(false);
		ClearSlotHighlights();

		if (!_hazardArmed && !_liftArmed)
			return;

		_hazardArmed = false;
		_liftArmed = false;
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

	/// <summary>
	/// First look at input while a hazard is armed, slotted between the sentry's gestures and
	/// the board's own. Wheel and camera-look fall through on purpose, the sentry's rule.
	/// </summary>
	private bool HandleHazardInput(InputEvent @event)
	{
		if ((!_hazardArmed && !_liftArmed) || Track == null)
			return false;

		if (@event is not InputEventMouseButton { Pressed: true } mouse)
			return false;

		switch (mouse.ButtonIndex)
		{
			case MouseButton.Right:
				CancelHazardPlacement();
				return true;

			case MouseButton.Left:
				if (_liftArmed)
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

		if (!FreeBuild)
			HazardHand.Take(_armedHandSlot);

		Track.RequestPlaceHazard(target.TileIndex, target.SlotIndex, _armedHazard);

		CancelHazardPlacement();
		EmitSignal(SignalName.HazardHandChanged);
		EmitSignal(SignalName.HazardStatusChanged, $"{_armedHazard.DisplayName()} placed.");
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

		if (!FreeBuild)
			HazardHand.Return(target.Kind);

		CancelHazardPlacement();
		EmitSignal(SignalName.HazardHandChanged);
		EmitSignal(SignalName.HazardStatusChanged, $"{target.Kind.DisplayName()} lifted back into the hand.");
	}

	/// <summary>The deal clock, ticked from the board's <c>_Process</c> beside the tile hand's.</summary>
	private void TickHazardHand(float delta)
	{
		if (!FreeBuild && HazardHand.Tick(delta))
			EmitSignal(SignalName.HazardHandChanged);
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
	}

	/// <summary>
	/// The controller's only <c>_ExitTree</c>, living in this partial. Two duties: take the
	/// track's signal handlers back by hand (a C# <c>+=</c> outlives the control that made it),
	/// and free the shared marker paint while the engine is alive — the exit-crash rule.
	/// </summary>
	public override void _ExitTree()
	{
		HookHazardSignals(false);
		_slotPaint?.Dispose();
		_slotPaint = null;
	}
}
