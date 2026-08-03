using Godot;
using MasterTrack.Audio;
using MasterTrack.Racer;

namespace MasterTrack.Tiles;

/// <summary>
/// What the world does when a tile arrives.
///
/// Placement is the loudest thing that happens in this game and until now it happened in silence:
/// a hundred and thirty metres a second of road stopped dead and nothing anywhere reacted. This
/// partial is the reaction — a shadow on the way down, dust and a shove at the bottom, and a
/// smudge left at the joint for whoever gets there late.
///
/// <b>All of it hangs off <see cref="TrackTile.TileLanded"/>, which the tile has always emitted
/// and nothing has ever listened to.</b> That is deliberate rather than incidental: the effects
/// are a listener like any other, so they can be taken off in one line, and anything else that
/// wants to know about a landing — a sound, a feed line, a board flash — hooks the same signal
/// instead of being threaded through here.
///
/// <b>Nothing here is replicated.</b> Every peer receives the placement and every peer builds the
/// same landing off its own copy, the rule the whole effects layer already lives by. The one thing
/// that differs per machine is who gets shaken, which is correct — the shake is about where your
/// car is, and only you know that.
///
/// A tile that does not fall gets none of this. The starting straight is the ground the race
/// begins on, not an event; dust off road that never moved would be the first dishonest effect in
/// the game.
/// </summary>
public partial class TrackTile
{
	// ---- Camera kick ----

	/// <summary>
	/// How far from the impact a racer still feels it, in metres. Two and a half cells — a car one
	/// tile back is well inside it, a car three tiles up the road is not.
	/// </summary>
	private const float KickRadius = Size * 2.5f;

	/// <summary>
	/// Shake handed to a car sitting right on top of the landing, 0 to 1. Well short of the full
	/// channel: the tile that lands on your head should not be indistinguishable from the tile
	/// that lands on your head while you are already at 300 km/h.
	/// </summary>
	private const float KickStrength = 0.85f;

	/// <summary>How far down the road the seam smudge reaches, in metres.</summary>
	private const float SeamScuffDepth = 9.0f;

	/// <summary>
	/// How far above the resting plane the shadow sits, in metres. It hangs in open air for the
	/// whole descent, so this only matters on the last frame — where without it the patch and the
	/// arriving road would be exactly co-planar and flicker against each other.
	/// </summary>
	private const float ShadowLift = 0.05f;

	// ---- Sound ----

	/// <summary>The descent, faintly: a wind rustle that rides the falling slab and is cut by
	/// the landing. Quiet on purpose — it is atmosphere over the racers' heads, not a warning;
	/// the shadow is the warning.</summary>
	private const string FallWindSfxPath = "res://assets/audio/hazards/tile_wind.mp3";

	/// <summary>How far under full volume the wind sits, in dB.</summary>
	private const float FallWindVolumeDb = -8.0f;

	/// <summary>The arrival: a hundred and thirty metres a second of road stopping dead. The
	/// audible half of the camera kick, and sized to carry like one.</summary>
	private const string LandingSfxPath = "res://assets/audio/hazards/tile_impact.mp3";

	// ---- State ----

	/// <summary>Whether this tile arrived by falling. False for anything simply built in place.</summary>
	private bool _fell;

	/// <summary>How far this tile had to fall, in metres, so the descent has a fraction.</summary>
	private float _fallTotal;

	private TileDropShadow? _shadow;

	/// <summary>The wind under the falling slab, alive only for the descent.</summary>
	private AudioStreamPlayer3D? _fallWind;

	/// <summary>
	/// The tile's footprint in its own space: where the middle of it is across and along the road,
	/// and how big it is. Worked out on the first frame anything asks.
	/// </summary>
	private Vector2 _footprintCentre;

	private Vector2 _footprintSize;

	/// <summary>
	/// Whether the footprint was read off real geometry or fell back to the nominal cell.
	///
	/// Worth keeping apart, because the fallback is not only for failure: a CSG piece has no bounds
	/// worth reading on the frame it is built, so the measurement at the top of the drop can miss
	/// and the one at the bottom — a second later, with the shapes long since resolved — will not.
	/// </summary>
	private bool _footprintExact;

	/// <summary>
	/// Local Z of the entry face — the seam this tile was laid onto. Local space always runs
	/// "north", so the racer comes in over the +Z edge of the footprint.
	/// </summary>
	private float _entryEdgeZ;

	/// <summary>
	/// Start the descent's telegraph. Called from <see cref="Initialize"/> for a tile that drops,
	/// and never for a ghost or for track that was simply there.
	/// </summary>
	private void BeginDropTelegraph(float fallHeight)
	{
		_fell = true;
		_fallTotal = fallHeight;

		MeasureFootprint();

		_shadow = new TileDropShadow { Name = "DropShadow", Footprint = _footprintSize };
		AddChild(_shadow);

		// The wind rides the tile itself — the tile is the thing falling — parked over the
		// middle of the footprint rather than the origin, which on an authored piece sits on
		// the entry seam with all of the road ahead of it.
		_fallWind = Sfx.Attach(this, FallWindSfxPath,
							   volumeDb: FallWindVolumeDb, unitSize: 25.0f);
		if (_fallWind != null)
			_fallWind.Position = new Vector3(_footprintCentre.X, 0.0f, _footprintCentre.Y);

		UpdateDropTelegraph();
	}

	/// <summary>
	/// Hold the shadow on the resting plane while the tile comes down over it, and tighten it as
	/// the gap closes.
	///
	/// The shadow is a child of the tile so that it cannot outlive it or be orphaned by a removal,
	/// which means it has to be walked back up by however far the tile still has to fall. The tile
	/// descends along <i>world</i> Y while a banked scene piece has its own axes tilted under it,
	/// so the offset is taken through the tile's basis rather than applied straight to local Y.
	/// </summary>
	private void UpdateDropTelegraph()
	{
		if (_shadow == null)
			return;

		Vector3 rest = new(_footprintCentre.X, ShadowLift, _footprintCentre.Y);
		Vector3 stayBehind = GlobalBasis.Inverse() * new Vector3(0.0f, -_fallRemaining, 0.0f);

		_shadow.Position = rest + stayBehind;
		_shadow.SetProgress(_fallTotal > 0.0f ? 1.0f - _fallRemaining / _fallTotal : 1.0f);
	}

	/// <summary>
	/// The impact. Connected to this tile's own <see cref="TileLanded"/> in <c>_Ready</c>, so it
	/// runs for the instant-placement path too — and bails there, because nothing landed.
	/// </summary>
	private void OnTileLanded(int trackIndex)
	{
		_shadow?.QueueFree();
		_shadow = null;

		// The wind stops the frame the tile does; the cut is what the impact interrupting it
		// sounds like, the same trade the missile's whistle makes.
		_fallWind?.QueueFree();
		_fallWind = null;

		if (!_fell || _isGhost)
			return;

		MeasureFootprint();
		Vector2 centre = _footprintCentre;

		var dust = new TileLandingDust
		{
			Name = "LandingDust",
			Position = new Vector3(centre.X, 0.0f, centre.Y),
			RingSize = _footprintSize,
		};

		// Added before it is fired: the tile is already in the tree, so _Ready has run and the
		// emitter exists by the time the burst asks it for particles.
		AddChild(dust);
		dust.Burst();

		AddChild(new TileSeamScuff
		{
			Name = "SeamScuff",
			// Pushed half its own depth in from the seam so the band sits on the new road rather
			// than half of it hanging off the end into the previous tile.
			Position = new Vector3(centre.X, TileSeamScuff.Lift, _entryEdgeZ - SeamScuffDepth * 0.5f),
			Patch = new Vector2(_footprintSize.X, SeamScuffDepth),
		});

		Sfx.PlayAt(this, LandingSfxPath, ToGlobal(new Vector3(centre.X, 0.0f, centre.Y)),
				   volumeDb: 2.0f, unitSize: 40.0f, pitchJitter: 0.05f);

		KickNearbyCameras(centre);
	}

	/// <summary>
	/// Shake the camera of every racer close enough to have felt it.
	///
	/// Racers only. The Track Master is looking down from altitude at the whole board and a board
	/// that lurches every time they place a tile is nausea, not impact — and it would shake hardest
	/// exactly when they are trying to aim the next one.
	/// </summary>
	private void KickNearbyCameras(Vector2 centre)
	{
		Vector3 impact = ToGlobal(new Vector3(centre.X, 0.0f, centre.Y));

		foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
		{
			if (node is not Node3D car)
				continue;

			float distance = car.GlobalPosition.DistanceTo(impact);
			if (distance >= KickRadius)
				continue;

			// Squared falloff, so the kick fades in over the last tile rather than switching on at
			// the edge of the radius.
			float reach = 1.0f - distance / KickRadius;
			car.GetNodeOrNull<CameraRig>("CameraRig")?.AddImpact(reach * reach * KickStrength);
		}
	}

	/// <summary>
	/// Work out the box this tile actually occupies, in its own space, by merging the bounds of
	/// every piece of geometry hanging off it.
	///
	/// Measured rather than assumed, because the tile's origin is not in the same place from one
	/// kind of tile to the next: a generated straight is built about its centre, a turn about the
	/// centre of its arc, and an authored piece sits on its own entry seam with all of its geometry
	/// ahead of it. Reading the geometry is the only answer that is right for all three — and it
	/// stays right when a new piece is authored, which an assumption would not.
	///
	/// A box rather than an outline on purpose: a box is what the game already means by a footprint
	/// (see <see cref="TrackFootprint"/>), it is what the grid reserves for the tile, and it is what
	/// the dust has to promise. Falls back to the nominal cell if there is no geometry to measure,
	/// which should only happen if a scene piece failed to instance.
	/// </summary>
	private void MeasureFootprint()
	{
		if (_footprintExact)
			return;

		float minX = float.MaxValue, minZ = float.MaxValue;
		float maxX = float.MinValue, maxZ = float.MinValue;
		var found = false;

		Transform3D toTile = GlobalTransform.AffineInverse();
		CollectExtent(this, toTile, ref minX, ref minZ, ref maxX, ref maxZ, ref found);

		if (!found || maxX - minX <= 0.01f || maxZ - minZ <= 0.01f)
		{
			// An authored piece is placed by its entry seam, so its geometry runs ahead of the
			// origin; everything else is built about its middle.
			float centreZ = Data.ScenePath.Length > 0 ? -HalfLength : 0.0f;
			_footprintCentre = new Vector2(0.0f, centreZ);
			_footprintSize = new Vector2(Size, Length);
			_entryEdgeZ = centreZ + HalfLength;
			return;
		}

		_footprintCentre = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
		_footprintSize = new Vector2(maxX - minX, maxZ - minZ);
		_entryEdgeZ = maxZ;
		_footprintExact = true;
	}

	/// <summary>
	/// Merge every drawable descendant's bounds into a running box, in tile space.
	///
	/// The landing's own effects are skipped: the dust is sized <i>from</i> this measurement and
	/// the shadow is held out at the resting plane, so either one would grow the box that produced
	/// it.
	/// </summary>
	private static void CollectExtent(Node node, Transform3D toTile,
									  ref float minX, ref float minZ,
									  ref float maxX, ref float maxZ, ref bool found)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is TileDropShadow or TileSeamScuff or GpuParticles3D)
				continue;

			if (child is VisualInstance3D visual)
			{
				Aabb box = visual.GetAabb();

				if (box.Size.LengthSquared() > 0.0f)
				{
					Transform3D relative = toTile * visual.GlobalTransform;

					for (var i = 0; i < 8; i++)
					{
						Vector3 corner = relative * box.GetEndpoint(i);
						minX = Mathf.Min(minX, corner.X);
						maxX = Mathf.Max(maxX, corner.X);
						minZ = Mathf.Min(minZ, corner.Z);
						maxZ = Mathf.Max(maxZ, corner.Z);
						found = true;
					}
				}
			}

			CollectExtent(child, toTile, ref minX, ref minZ, ref maxX, ref maxZ, ref found);
		}
	}
}
