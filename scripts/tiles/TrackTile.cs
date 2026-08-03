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
///
/// Split across partials to keep this file about the tile itself: <c>TrackTile.Hazards.cs</c> for
/// the still hazards, <c>TrackTile.Moving.cs</c> for the ones with moving parts, and
/// <c>TrackTile.Shapes.cs</c> for tiles that build their own geometry outright because the
/// assembly here cannot describe them.
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

	/// <summary>
	/// Length of this tile in metres, along the direction of travel. A multiple of
	/// set from <see cref="TileData.RunLength"/> when the tile is built.
	/// </summary>
	public float Length { get; private set; } = Size;

	private float HalfLength => Length * 0.5f;

	/// <summary>Inset of the walls that run down the long sides, from the tile centre.</summary>
	private float LengthInset => HalfLength - WallThickness * 0.5f;

	/// <summary>Grid coordinate of this tile along the track (index from the start line).</summary>
	[Export] public int TrackIndex { get; set; }

	public TileData Data { get; private set; } = new();

	/// <summary>
	/// The seam this tile was built onto: where the racer crosses into it and which way it runs.
	/// Everything about where the tile sits in the world comes from this one value.
	/// </summary>
	public TrackAnchor EntryAnchor { get; private set; }

	/// <summary>
	/// Fired when the tile reaches its resting place — after the drop, not when it was spawned.
	/// A tile that doesn't drop lands the instant it's built.
	/// </summary>
	[Signal] public delegate void TileLandedEventHandler(int trackIndex);

	/// <summary>
	/// Fired when a condemned tile's time runs out. The tile is still in the tree when this goes
	/// off — whoever condemned it owns taking it away. See <see cref="Condemn"/>.
	/// </summary>
	[Signal] public delegate void TileExpiredEventHandler(int trackIndex);

	public TileHazard Hazard => Data.Hazard;

	/// <summary>Ghosts are preview-only: see-through, and nothing collides with them.</summary>
	private bool _isGhost;

	/// <summary>Flat colour a ghost is drawn in — green for a legal drop, red for an illegal one.</summary>
	private Color _ghostTint = Colors.White;

	/// <summary>Metres this tile still has to descend before it's home. 0 once landed.</summary>
	private float _fallRemaining;

	private float _fallSpeed;

	/// <summary>Seconds this tile has left before it crumbles. 0 unless condemned.</summary>
	private float _expiryRemaining;

	/// <summary>How long the condemnation was for, so the blink knows how far through it is.</summary>
	private float _expiryTotal;

	/// <summary>Seconds since the last blink flip.</summary>
	private float _blinkPhase;

	/// <summary>Whether the tile has been condemned. Once true it never goes back.</summary>
	public bool IsCondemned => _expiryTotal > 0.0f;

	/// <summary>Seconds between blink flips at the start of the countdown — a slow, calm pulse.</summary>
	private const float BlinkSlowest = 0.34f;

	/// <summary>
	/// And at the end of it. Fast enough to read as panic rather than as a pulse, but not so fast
	/// it becomes a solid half-lit grey that says nothing.
	/// </summary>
	private const float BlinkFastest = 0.045f;

	public override void _Ready()
	{
		// Only a tile that's still coming down has anything to do per frame, and a long track
		// is a lot of tiles to be waking up for nothing. Both are switched back on by whoever
		// needs them: the fall by Initialize, the physics step by the hazards that move or push.
		SetProcess(false);
		SetPhysicsProcess(false);

		// The tile reacts to its own arrival through the signal rather than by calling the effects
		// out of StepFall, so the landing layer is a listener that can be lifted off in one line
		// and anything else that wants the moment hooks the same place. See TrackTile.Landing.cs.
		TileLanded += OnTileLanded;
	}

	/// <summary>
	/// Start this tile's countdown to falling away. It blinks for <paramref name="seconds"/>, faster
	/// and faster as the time runs out, and then fires <see cref="TileExpired"/>.
	///
	/// It stays solid the whole way through. The blink is a warning, and a warning that had already
	/// taken the road away would be a warning about something that had happened.
	/// </summary>
	public void Condemn(float seconds)
	{
		if (IsCondemned || _isGhost || seconds <= 0.0f)
			return;

		_expiryTotal = seconds;
		_expiryRemaining = seconds;
		_blinkPhase = 0.0f;
		SetProcess(true);
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
	public void Initialize(TileData data, int trackIndex, TrackAnchor anchor, bool isGhost = false,
						   Color? ghostTint = null, float fallHeight = 0.0f, float fallSpeed = 0.0f,
						   Transform3D? entryFrame = null)
	{
		Data = data;
		TrackIndex = trackIndex;
		EntryAnchor = anchor;
		// A turning tile builds its own arc and is anchored where that arc starts, so it reports one
		// tile of Length purely so the positioning below puts its origin on the arc's centre of
		// curvature line. Only a tile that really runs straight has a length worth the name.
		Length = data.IsTurn ? Size : data.RunLength;
		_isGhost = isGhost;
		_ghostTint = ghostTint ?? Colors.White;

		// Road surface: the tire model reads the first node group to pick its grip values.
		if (!isGhost)
			AddToGroup(SurfaceGroups.Road);

		// The anchor is the middle of the entry face and the geometry is built about the tile's own
		// centre, so the mesh sits half its own length down the road from the seam. One line, and
		// the same line for every tile in the catalog: a turn reports a single cell of Length, which
		// puts its centre exactly where the grid used to put it.
		//
		// A scene piece is the exception twice over: it is placed by its own entry seam, so the
		// tile's origin sits on the anchor itself — and when the chain hands over a full entry
		// frame, the tile takes the whole basis, which is how a piece placed on a banked seam
		// arrives banked instead of flat.
		if (data.ScenePath.Length > 0)
		{
			Transform = entryFrame is { } frame
				? new Transform3D(frame.Basis, frame.Origin)
				: new Transform3D(new Basis(Vector3.Up, anchor.Yaw), anchor.Position);
		}
		else
		{
			Position = anchor.Position + anchor.Forward * (Length * 0.5f);
			Rotation = new Vector3(0.0f, anchor.Yaw, 0.0f);
		}

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
			BeginDropTelegraph(fallHeight);
			return;
		}

		if (!isGhost)
			EmitSignal(SignalName.TileLanded, trackIndex);
	}

	/// <summary>
	/// The two things a tile can be doing that aren't standing still: arriving, and running out of
	/// time. They are independent — a tile at the back of a very short track can be condemned while
	/// it is still coming down — so both are stepped and the per-frame work is only given up once
	/// neither has anything left to do.
	/// </summary>
	public override void _Process(double delta)
	{
		if (_fallRemaining > 0.0f)
			StepFall((float)delta);

		if (_expiryRemaining > 0.0f)
			StepExpiry((float)delta);

		if (_fallRemaining <= 0.0f && _expiryRemaining <= 0.0f)
			SetProcess(false);
	}

	/// <summary>
	/// Sink toward the resting place at a constant speed. Constant rather than eased on purpose:
	/// the knob is a speed, so the tile should visibly travel at it, and a racer judging whether
	/// they'll beat a tile down needs it to move the way it looks like it's moving.
	/// </summary>
	private void StepFall(float delta)
	{
		float step = _fallSpeed * delta;

		if (step < _fallRemaining)
		{
			_fallRemaining -= step;
			Position -= new Vector3(0.0f, step, 0.0f);
			UpdateDropTelegraph();
			return;
		}

		// Last step: snap to exactly the resting height rather than however close the frame got.
		Position -= new Vector3(0.0f, _fallRemaining, 0.0f);
		_fallRemaining = 0.0f;

		EmitSignal(SignalName.TileLanded, TrackIndex);
	}

	/// <summary>
	/// Run the countdown, blinking the whole tile on and off at a rate that climbs as it runs out.
	///
	/// Squared rather than linear, so most of the countdown is a slow "this one is going" and the
	/// urgency all arrives in the last second or so — which is the part a racer has to react to.
	/// The tile is hidden and shown rather than faded because visibility costs nothing: fading
	/// would mean reaching into every material on a tile that is about to be thrown away.
	/// </summary>
	private void StepExpiry(float delta)
	{
		_expiryRemaining -= delta;

		if (_expiryRemaining <= 0.0f)
		{
			_expiryRemaining = 0.0f;
			Visible = false;
			EmitSignal(SignalName.TileExpired, TrackIndex);
			return;
		}

		float gone = 1.0f - _expiryRemaining / _expiryTotal;
		float period = Mathf.Lerp(BlinkSlowest, BlinkFastest, gone * gone);

		_blinkPhase += delta;
		if (_blinkPhase < period)
			return;

		_blinkPhase = 0.0f;
		Visible = !Visible;
	}

	private void BuildGeometry()
	{
		// An authored piece is its own geometry: instance it and step aside. None of the
		// generated assembly below — floor, walls, stripes, hazard — has anything to add to a
		// shape somebody built by hand.
		if (Data.ScenePath.Length > 0)
		{
			BuildFromScene();
			return;
		}

		TileDefinition definition = TileCatalog.Match(Data);

		// Some tiles are built whole, because the assembly below cannot describe them: it walls
		// off the edges of a flat, level straight run, which has nothing useful to say about an L
		// with both openings on one side, a road that climbs, or one that leaves the ground.
		// See TrackTile.Shapes.cs.
		if (Data.IsHairpin)
		{
			BuildHairpin(definition);
			return;
		}

		if (Data.IsTurn && !Data.IsHairpin)
		{
			BuildWideTurn(definition);
			return;
		}

		switch (Data.Hazard)
		{
			case TileHazard.RampUp:
			case TileHazard.RampDown:
				BuildRamp(definition);
				return;

			case TileHazard.LoopAhead:
				BuildLoop(definition);
				return;
		}

		// In local space the tile always runs "north": the racer comes in over the +Z edge
		// and leaves through whichever edge the turn points at.
		TrackDirection exitLocal = TrackDirection.North.Turn(Data.ExitTurn);

		BuildFloor(definition);

		if (HasSideWalls)
			BuildWalls(definition, exitLocal);

		if (!DrawsOwnRacingLine)
			BuildRacingLine(exitLocal);

		BuildHazard(definition);
	}

	/// <summary>
	/// Instance the authored piece this tile is, seated so its entry seam lands exactly on the
	/// tile's origin — which <see cref="Initialize"/> put on the entry anchor. The piece keeps all
	/// of its own behaviour: surface group, baked mesh or live CSG, props and hazards.
	/// </summary>
	private void BuildFromScene()
	{
		if (GD.Load<PackedScene>(Data.ScenePath) is not { } scene
			|| scene.Instantiate() is not Tool.TrackPiece piece)
		{
			GD.PushError($"[TrackTile] {Data.ScenePath} would not instance as a TrackPiece, so "
						 + "this tile has no geometry.");
			return;
		}

		AddChild(piece);

		// The inverse of the entry seam: whatever corner of its own scene the piece was built in,
		// its entry lands on this tile's origin facing down -Z, which is what the anchor means.
		piece.Transform = piece.EntrySeam is { } entry
			? entry.Transform.Orthonormalized().AffineInverse()
			: Transform3D.Identity;

		if (_isGhost)
			GhostifyPiece(piece);
	}

	/// <summary>
	/// A scene piece shown as a placement preview: see-through, tinted the verdict's colour, and
	/// solid to nothing — the same rules every generated ghost already follows.
	/// </summary>
	private void GhostifyPiece(Node node)
	{
		switch (node)
		{
			case CollisionObject3D body:
				body.CollisionLayer = 0;
				body.CollisionMask = 0;
				break;

			case CollisionShape3D shape:
				shape.Disabled = true;
				break;

			case GeometryInstance3D geometry:
				geometry.Transparency = 0.55f;
				break;
		}

		foreach (Node child in node.GetChildren())
			GhostifyPiece(child);
	}

	/// <summary>
	/// Whether the tile gets the standard barriers along its closed edges.
	///
	/// The log trap is the exception, and deliberately so: its whole threat is being shoved off
	/// the track, which needs there to be nothing to be shoved into.
	/// </summary>
	private bool HasSideWalls => Data.Hazard != TileHazard.LogTrap;

	/// <summary>
	/// Whether the hazard paints its own markings instead of taking the centre line. True where a
	/// stripe down the middle of the tile would be painted across something that is not road —
	/// thin air on a split, the runoff either side of a squiggle's ribbon.
	/// </summary>
	private bool DrawsOwnRacingLine
		=> Data.Hazard is TileHazard.SplitTrack or TileHazard.Squiggle;

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

	/// <summary>
	/// A box placed by a full transform rather than a position and euler angles.
	///
	/// Everything flat, sloped or looping so far has turned about exactly one axis, so
	/// <see cref="AddBox"/>'s <c>rotationDegrees</c> was enough. A banked corner turns about two at
	/// once — it yaws round the arc and rolls up the bank — and composing that out of euler angles
	/// is a source of sign bugs rather than a convenience, so the caller hands over the basis it
	/// already worked out from the surface.
	///
	/// Always a direct child of the tile, never of a helper node: a <see cref="CollisionShape3D"/>
	/// is only picked up as an immediate child of the body it belongs to, so nesting the geometry
	/// under a rotated <c>Node3D</c> would produce a corner you can see and drive straight through.
	/// </summary>
	private void AddOrientedBox(Vector3 size, Transform3D transform, StandardMaterial3D material,
								bool collision = true)
	{
		AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = size, Material = material },
			Transform = transform,
		});

		if (!collision || _isGhost)
			return;

		AddChild(new CollisionShape3D
		{
			Shape = new BoxShape3D { Size = size },
			Transform = transform,
		});
	}

	// ---- Materials ----
	//
	// Every one of these is a colour and nothing else. There are no roughness or metallic values
	// here on purpose: Finish strips them, so a material is only ever asked what colour it is.

	/// <summary>
	/// Tarmac. A neutral mid grey, and it stays neutral — this is the surface every hazard colour
	/// has to be read against, so it is deliberately the least interesting thing on screen.
	/// </summary>
	private StandardMaterial3D RoadMaterial()
		=> Finish(new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.37f, 0.40f) });

	private StandardMaterial3D WallMaterial(Color accent)
		=> Finish(new StandardMaterial3D { AlbedoColor = accent });

	private StandardMaterial3D RampMaterial(Color accent)
		=> Finish(new StandardMaterial3D { AlbedoColor = accent });

	private StandardMaterial3D LineMaterial()
		=> Finish(new StandardMaterial3D { AlbedoColor = new Color(0.96f, 0.96f, 0.92f) });

	/// <summary>The look of a boost pad, on any tile that carries one. See <c>BoostAccent</c>.</summary>
	private StandardMaterial3D BoostMaterial()
		=> Finish(new StandardMaterial3D { AlbedoColor = BoostAccent });

	private StandardMaterial3D GravelMaterial()
		=> Finish(new StandardMaterial3D { AlbedoColor = new Color(0.74f, 0.58f, 0.32f) });

	/// <summary>
	/// Ice. Reads as ice by being the palest, coldest thing on the track — not by being shiny.
	/// With specular gone there is no highlight to sell it with, which is the trade the whole
	/// look is making: colour does every job that gloss used to.
	/// </summary>
	private StandardMaterial3D IceMaterial()
		=> Finish(new StandardMaterial3D { AlbedoColor = new Color(0.68f, 0.95f, 1.00f) });

	/// <summary>
	/// The house style, applied to every material any tile builds — and the reason none of the
	/// helpers above set anything but a colour.
	///
	/// Per-vertex shading is the whole trick. It is Gouraud shading, which is what the arcade
	/// hardware this look comes from actually did: light is worked out at the corners and smeared
	/// across the face between them, so a box lights as six flat facets instead of a smooth
	/// gradient. Specular is off and roughness pinned at 1 because a highlight sliding across a
	/// surface is the single most modern-looking thing a renderer does.
	///
	/// A ghost then throws all of that away for one flat see-through colour: the shape still reads
	/// from the walls and ramp, and a single green or red tells the Track Master what they need.
	/// </summary>
	private StandardMaterial3D Finish(StandardMaterial3D material)
	{
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex;
		material.SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled;
		material.Metallic = 0.0f;
		material.Roughness = 1.0f;

		if (!_isGhost)
			return material;

		material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		material.AlbedoColor = _ghostTint with { A = 0.45f };
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		return material;
	}
}
