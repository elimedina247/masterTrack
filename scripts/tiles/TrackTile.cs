using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.Tiles;

/// <summary>
/// A tile that has been placed on the track, and the geometry that goes with it.
///
/// The geometry is built in code from the tile's <see cref="TileData"/> rather than authored
/// as a scene per tile type: every tile is boxes on a run of grid cells, so a new hazard is a
/// new entry in <see cref="TileCatalog"/> plus a case in <see cref="BuildHazard"/> — no scene
/// wiring, and the palette picks it up for free.
///
/// A tile is always one cell wide (<see cref="Size"/>) but can run for several cells along the
/// direction of travel (<see cref="Length"/>), which is why width and length are kept apart
/// below. Geometry is built in local space running north, so length is always the local Z axis.
///
/// Dimensions come in two kinds, and the difference matters when <see cref="TileCatalog.TileSize"/>
/// changes. Anything that describes the tile's <i>footprint</i> — road width, how far a hazard
/// spans across it, how much of it a feature runs for — is a fraction of <see cref="Size"/> or
/// of <see cref="Length"/>, so the tile keeps its proportions at any scale. Anything the
/// <i>car</i> has to fit through, drive over or fall into stays in car-scale metres: the car
/// does not grow with the tiles, and what makes a gap jumpable or a pinch tight is its speed
/// and size, not the width of the road.
///
/// Because the whole tile is derived from its data, every peer can build an identical copy
/// from a replicated placement; nothing about the mesh needs to go over the wire.
/// </summary>
[GlobalClass]
public partial class TrackTile : StaticBody3D
{
	/// <summary>Width of a tile in metres — one grid cell, whatever its length.</summary>
	public const float Size = TileCatalog.TileSize;

	private const float Half = Size * 0.5f;
	private const float FloorThickness = 20.0f;

	// Barriers are car-scale in height — a taller wall on a wider road buys nothing — but
	// thick enough to still read as an edge from the Track Master's board view.
	private const float WallHeight = 2.0f;
	private const float WallThickness = Size * 0.045f;
	private const float WallInset = Half - WallThickness * 0.5f;

	/// <summary>Length of the hole in a <see cref="TileHazard.Gap"/> tile, along the direction
	/// of travel. Car-scale: whether it can be cleared is a question of speed and gravity.</summary>
	private const float GapLength = 6.0f;

	/// <summary>
	/// Length of this tile in metres, along the direction of travel. A multiple of
	/// <see cref="Size"/>, set from <see cref="TileData.CellLength"/> when the tile is built.
	/// </summary>
	public float Length { get; private set; } = Size;

	private float HalfLength => Length * 0.5f;

	/// <summary>Inset of the walls that run down the long sides, from the tile centre.</summary>
	private float LengthInset => HalfLength - WallThickness * 0.5f;

	/// <summary>
	/// How much of the tile an <see cref="TileHazard.IcePatch"/> covers. Scales with the tile
	/// rather than the car: ice is a surface, so on a longer tile it should be a longer sheet,
	/// not the same short patch adrift in the middle of a straight.
	/// </summary>
	private float IceLength => Length * 0.5f;

	/// <summary>Grid coordinate of this tile along the track (index from the start line).</summary>
	[Export] public int TrackIndex { get; set; }

	public TileData Data { get; private set; } = new();

	/// <summary>Direction a racer is travelling as they enter this tile.</summary>
	public TrackDirection EntryDirection { get; private set; } = TrackDirection.North;

	/// <summary>
	/// Fired when the tile reaches its resting place — after the drop, not when it was spawned.
	/// A tile that doesn't drop lands the instant it's built.
	/// </summary>
	[Signal] public delegate void TileLandedEventHandler(int trackIndex);

	public TileHazard Hazard => Data.Hazard;

	/// <summary>Ghosts are preview-only: see-through, and nothing collides with them.</summary>
	private bool _isGhost;

	/// <summary>Flat colour a ghost is drawn in — green for a legal drop, red for an illegal one.</summary>
	private Color _ghostTint = Colors.White;

	/// <summary>Metres this tile still has to descend before it's home. 0 once landed.</summary>
	private float _fallRemaining;

	private float _fallSpeed;

	public override void _Ready()
	{
		// Only a tile that's still coming down has anything to do per frame, and a long track
		// is a lot of tiles to be waking up for nothing.
		SetProcess(false);
	}

	/// <summary>
	/// Populate from replicated data and build the geometry. Called after placement on every
	/// peer. The tile positions and rotates itself from its entry cell and its own length, so
	/// callers only supply the track state.
	///
	/// Give it a <paramref name="fallHeight"/> and <paramref name="fallSpeed"/> and it starts
	/// that far above the track and sinks into place, which is how a placement reads as an
	/// event to the racers driving toward it rather than track that was always there. Leave
	/// them at zero and it simply exists, which is what the starting straight wants.
	/// </summary>
	public void Initialize(TileData data, int trackIndex, Vector2I cell, TrackDirection entryDirection,
						   bool isGhost = false, Color? ghostTint = null,
						   float fallHeight = 0.0f, float fallSpeed = 0.0f)
	{
		Data = data;
		TrackIndex = trackIndex;
		EntryDirection = entryDirection;
		// A hairpin is one cell per leg whatever its CellLength says, so it anchors on its entry
		// cell and builds the outgoing lane out to the side from there.
		int runCells = data.IsHairpin ? 1 : data.CellLength;
		Length = Size * runCells;
		_isGhost = isGhost;
		_ghostTint = ghostTint ?? Colors.White;

		// Road surface: the tire model reads the first node group to pick its grip values.
		if (!isGhost)
			AddToGroup(SurfaceGroups.Road);

		// A long tile is positioned by its middle, not its entry cell, so the mesh straddles
		// every cell the grid handed it.
		Position = TileCatalog.SpanCenterToWorld(cell, entryDirection, runCells);
		Rotation = new Vector3(0.0f, entryDirection.Yaw(), 0.0f);

		BuildGeometry();

		// A ghost is a preview of where a tile would go, so it belongs at the destination —
		// watching the preview fall would say nothing about the placement.
		bool drops = !isGhost && fallHeight > 0.0f && fallSpeed > 0.0f;
		if (drops)
		{
			_fallRemaining = fallHeight;
			_fallSpeed = fallSpeed;
			Position += new Vector3(0.0f, fallHeight, 0.0f);
			SetProcess(true);
			return;
		}

		if (!isGhost)
			EmitSignal(SignalName.TileLanded, trackIndex);
	}

	/// <summary>
	/// Sink toward the resting place at a constant speed. Constant rather than eased on purpose:
	/// the knob is a speed, so the tile should visibly travel at it, and a racer judging whether
	/// they'll beat a tile down needs it to move the way it looks like it's moving.
	/// </summary>
	public override void _Process(double delta)
	{
		float step = _fallSpeed * (float)delta;

		if (step < _fallRemaining)
		{
			_fallRemaining -= step;
			Position -= new Vector3(0.0f, step, 0.0f);
			return;
		}

		// Last step: snap to exactly the resting height rather than however close the frame got.
		Position -= new Vector3(0.0f, _fallRemaining, 0.0f);
		_fallRemaining = 0.0f;
		SetProcess(false);

		EmitSignal(SignalName.TileLanded, TrackIndex);
	}

	private void BuildGeometry()
	{
		TileDefinition definition = TileCatalog.Match(Data);

		// The hairpin is built whole rather than assembled from the parts below: those wall off
		// the edges of a straight run, which has nothing useful to say about a tile shaped like
		// an L with both openings on the same side.
		if (Data.IsHairpin)
		{
			BuildHairpin(definition);
			return;
		}

		// In local space the tile always runs "north": the racer comes in over the +Z edge
		// and leaves through whichever edge the turn points at.
		TrackDirection exitLocal = TrackDirection.North.Turn(Data.ExitTurn);

		BuildFloor(definition);
		BuildWalls(definition, exitLocal);
		BuildRacingLine(exitLocal);
		BuildHazard(definition);
	}

	private void BuildFloor(TileDefinition definition)
	{
		switch (Data.Hazard)
		{
			case TileHazard.Gap:
				AddAprons(GapLength, RoadMaterial());
				break;

			case TileHazard.IcePatch:
				// The ice replaces the middle of the road rather than sitting on top of it,
				// so there's no lip for the suspension to trip over.
				AddAprons(IceLength, RoadMaterial());
				BuildIcePatch();
				break;

			default:
				AddBox(new Vector3(Size, FloorThickness, Length), new Vector3(0, -FloorThickness * 0.5f, 0),
					   RoadMaterial());
				break;
		}
	}

	/// <summary>
	/// Road either side of a centred feature of the given length: the entry apron a racer
	/// arrives on and the exit apron they have to reach.
	/// </summary>
	private void AddAprons(float featureLength, StandardMaterial3D material)
	{
		float length = (Length - featureLength) * 0.5f;
		float offset = (Length - length) * 0.5f;

		AddBox(new Vector3(Size, FloorThickness, length),
			   new Vector3(0, -FloorThickness * 0.5f, offset), material);
		AddBox(new Vector3(Size, FloorThickness, length),
			   new Vector3(0, -FloorThickness * 0.5f, -offset), material);
	}

	/// <summary>
	/// The ice needs its own body so it can carry its own surface group — the tire model
	/// looks the group up on whatever the wheel ray actually hits.
	/// </summary>
	private void BuildIcePatch()
	{
		var iceBody = new StaticBody3D { Name = "IceSurface" };
		if (!_isGhost)
			iceBody.AddToGroup(SurfaceGroups.Ice);
		AddChild(iceBody);

		AddBox(new Vector3(Size, FloorThickness, IceLength), new Vector3(0, -FloorThickness * 0.5f, 0),
			   IceMaterial(), parent: iceBody);
	}

	private void BuildWalls(TileDefinition definition, TrackDirection exitLocal)
	{
		StandardMaterial3D material = WallMaterial(definition.Accent);

		// Open at the entry (+Z) and at the exit; wall off the other two edges. That turns a
		// straight into a corridor and a curve into an L without any custom meshes.
		foreach (TrackDirection edge in new[]
				 { TrackDirection.North, TrackDirection.East, TrackDirection.South, TrackDirection.West })
		{
			if (edge == TrackDirection.South || edge == exitLocal)
				continue;

			(Vector3 size, Vector3 position) = WallFor(edge);
			AddBox(size, position, material);
		}
	}

	/// <summary>
	/// Size and position of the barrier along one edge. North and south are the tile's two ends,
	/// so they are a cell wide and sit at either end of its <see cref="Length"/>; east and west
	/// are the long sides, so they run the whole length at the fixed width inset.
	/// </summary>
	private (Vector3 Size, Vector3 Position) WallFor(TrackDirection edge) => edge switch
	{
		TrackDirection.North => (new Vector3(Size, WallHeight, WallThickness),
								 new Vector3(0, WallHeight * 0.5f, -LengthInset)),
		TrackDirection.South => (new Vector3(Size, WallHeight, WallThickness),
								 new Vector3(0, WallHeight * 0.5f, LengthInset)),
		TrackDirection.East => (new Vector3(WallThickness, WallHeight, Length),
								new Vector3(WallInset, WallHeight * 0.5f, 0)),
		_ => (new Vector3(WallThickness, WallHeight, Length),
			  new Vector3(-WallInset, WallHeight * 0.5f, 0)),
	};

	/// <summary>
	/// A stripe from the middle of the tile out to each open edge. Cosmetic, but from the
	/// Track Master's top-down view it's what makes the path readable at a glance.
	/// </summary>
	private void BuildRacingLine(TrackDirection exitLocal)
	{
		StandardMaterial3D material = LineMaterial();
		AddStripe(TrackDirection.South, material);
		AddStripe(exitLocal, material);
	}

	private void AddStripe(TrackDirection edge, StandardMaterial3D material)
	{
		const float width = Size * 0.05f;
		const float y = 0.011f;

		// The stripe runs from the middle of the tile to the edge it points at, so how far that
		// is depends on which edge: the ends are half a length away, the sides half a width.
		float run = edge == TrackDirection.North || edge == TrackDirection.South ? HalfLength : Half;

		(Vector3 size, Vector3 position) = edge switch
		{
			TrackDirection.North => (new Vector3(width, 0.02f, run), new Vector3(0, y, -run * 0.5f)),
			TrackDirection.South => (new Vector3(width, 0.02f, run), new Vector3(0, y, run * 0.5f)),
			TrackDirection.East => (new Vector3(run, 0.02f, width), new Vector3(run * 0.5f, y, 0)),
			_ => (new Vector3(run, 0.02f, width), new Vector3(-run * 0.5f, y, 0)),
		};

		// Mesh only — a 2 cm lip in the road would upset the suspension for no reason.
		AddBox(size, position, material, collision: false);
	}

	private void BuildHazard(TileDefinition definition)
	{
		switch (Data.Hazard)
		{
			case TileHazard.JumpAhead:
			{
				// A take-off ramp rising toward the exit. It spans the road so it can't be
				// driven around, but its run-up and rise are car-scale — a ramp four times
				// longer at the same angle would fire the car off the far end of the track.
				const float angle = 12.0f;
				const float length = 10.0f;
				const float thickness = 0.4f;
				float rise = length * 0.5f * Mathf.Sin(Mathf.DegToRad(angle));

				// Sunk so the low lip sits flush with the road; the take-off edge ends up
				// about 2 m up. Centred, which on a long tile is the point: the run-up is
				// what lets a racer choose the speed they take it at, and the run-out is
				// what they land on.
				AddBox(new Vector3(Size * 0.9f, thickness, length),
					   new Vector3(0, rise - thickness * 0.5f + 0.02f, 0),
					   RampMaterial(definition.Accent), rotationDegrees: new Vector3(angle, 0, 0));
				break;
			}

			case TileHazard.Bottleneck:
			{
				// Intrusions from both walls leaving a car-scale slot down the middle. The slot
				// deliberately doesn't scale with the road: on a wide tile that's what turns
				// this from a decoration into a squeeze racers have to fight over.
				//
				// The depth is a fraction of one cell rather than of the whole tile, so the
				// pinch stays a pinch — a squeeze that went on for a hundred metres would just
				// be a narrow road.
				const float slot = 7.0f;
				const float depth = Size * 0.35f;
				const float width = (Size - slot) * 0.5f;
				const float offset = (Size + slot) * 0.25f;

				StandardMaterial3D material = WallMaterial(definition.Accent);
				AddBox(new Vector3(width, WallHeight, depth), new Vector3(offset, WallHeight * 0.5f, 0),
					   material);
				AddBox(new Vector3(width, WallHeight, depth), new Vector3(-offset, WallHeight * 0.5f, 0),
					   material);
				break;
			}
		}
	}

	// ---- Hairpin ----

	/// <summary>
	/// A 180-degree turn: the racer arrives up one lane, sweeps round an apex at the far end, and
	/// comes back down a second lane alongside the first, heading the way they came.
	///
	/// Two cells side by side in local space — the entry lane on the origin, the outgoing lane one
	/// cell to whichever side <see cref="TileData.TurnSide"/> points at — so both openings are on
	/// the +Z edge and every wall below is either the outer shell or the apex between the lanes.
	///
	/// The apex is the tile. Without a barrier between the lanes this would be an eighty metre bay
	/// that a racer could clip the corner of and drive straight out of; with one they have to go
	/// to the far end and turn around, which is what a hairpin costs.
	/// </summary>
	private void BuildHairpin(TileDefinition definition)
	{
		int side = Data.TurnSide;

		// Centre of the outgoing lane, one cell to the side of the entry lane.
		float lane = side * Size;

		// How far the apex reaches back from the open edge. Half the cell leaves the far half of
		// both lanes clear to turn around in.
		const float apex = Size * 0.5f;

		// Both lanes as one slab: two cells across, one deep, centred between them.
		AddBox(new Vector3(Size * 2.0f, FloorThickness, Size),
			   new Vector3(lane * 0.5f, -FloorThickness * 0.5f, 0), RoadMaterial());

		StandardMaterial3D wall = WallMaterial(definition.Accent);

		// The outer shell: across the far end of both lanes, then down the outside of each.
		AddBox(new Vector3(Size * 2.0f, WallHeight, WallThickness),
			   new Vector3(lane * 0.5f, WallHeight * 0.5f, -LengthInset), wall);
		AddBox(new Vector3(WallThickness, WallHeight, Size),
			   new Vector3(-side * WallInset, WallHeight * 0.5f, 0), wall);
		AddBox(new Vector3(WallThickness, WallHeight, Size),
			   new Vector3(lane + side * WallInset, WallHeight * 0.5f, 0), wall);

		// The apex, on the boundary the two lanes share, reaching back from the open edge.
		AddBox(new Vector3(WallThickness, WallHeight, apex),
			   new Vector3(side * Half, WallHeight * 0.5f, Half - apex * 0.5f), wall);

		BuildHairpinRacingLine(lane);
	}

	/// <summary>
	/// The racing line as a U: up the entry lane, across the open end past the apex, and back down
	/// the outgoing lane. Same job as <see cref="BuildRacingLine"/> — from board altitude this is
	/// what tells the Track Master which way the track leaves a tile.
	/// </summary>
	private void BuildHairpinRacingLine(float lane)
	{
		const float width = Size * 0.05f;
		const float y = 0.011f;

		// The crossover sits north of the apex, in the clear half of the tile — drawn anywhere
		// south of it the line would run through a wall.
		const float crossZ = -Half * 0.5f;
		const float legLength = Half - crossZ;
		const float legZ = (Half + crossZ) * 0.5f;

		StandardMaterial3D material = LineMaterial();

		AddBox(new Vector3(width, 0.02f, legLength), new Vector3(0, y, legZ), material, collision: false);
		AddBox(new Vector3(Mathf.Abs(lane), 0.02f, width), new Vector3(lane * 0.5f, y, crossZ), material,
			   collision: false);
		AddBox(new Vector3(width, 0.02f, legLength), new Vector3(lane, y, legZ), material, collision: false);
	}

	// ---- Primitive construction ----

	private void AddBox(Vector3 size, Vector3 position, StandardMaterial3D material,
						Vector3 rotationDegrees = default, bool collision = true, Node? parent = null)
	{
		parent ??= this;

		var mesh = new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = size, Material = material },
			Position = position,
			RotationDegrees = rotationDegrees,
		};
		parent.AddChild(mesh);

		// Ghosts are a preview of a placement that hasn't happened — they must never collide.
		if (!collision || _isGhost)
			return;

		var shape = new CollisionShape3D
		{
			Shape = new BoxShape3D { Size = size },
			Position = position,
			RotationDegrees = rotationDegrees,
		};
		parent.AddChild(shape);
	}

	// ---- Materials ----

	private StandardMaterial3D RoadMaterial()
		=> Finish(new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.30f, 0.32f), Roughness = 0.9f });

	private StandardMaterial3D WallMaterial(Color accent)
		=> Finish(new StandardMaterial3D { AlbedoColor = accent, Roughness = 0.8f });

	private StandardMaterial3D RampMaterial(Color accent)
		=> Finish(new StandardMaterial3D { AlbedoColor = accent, Roughness = 0.7f });

	private StandardMaterial3D LineMaterial()
		=> Finish(new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.92f, 0.88f), Roughness = 0.9f });

	private StandardMaterial3D IceMaterial()
		=> Finish(new StandardMaterial3D
		{
			AlbedoColor = new Color(0.62f, 0.88f, 0.96f),
			Roughness = 0.05f,
			Metallic = 0.2f,
		});

	/// <summary>
	/// Turn a material into a placement preview: one flat, see-through colour for the whole
	/// tile. Dropping the per-part colours is deliberate — the shape still reads from the
	/// walls and ramp, and a single green/red tells the Track Master what they need to know.
	/// </summary>
	private StandardMaterial3D Finish(StandardMaterial3D material)
	{
		if (!_isGhost)
			return material;

		material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		material.AlbedoColor = _ghostTint with { A = 0.45f };
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		return material;
	}
}
