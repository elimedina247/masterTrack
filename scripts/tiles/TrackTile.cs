using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.Tiles;

/// <summary>
/// A tile that has been placed on the track, and the geometry that goes with it.
///
/// The geometry is built in code from the tile's <see cref="TileData"/> rather than authored
/// as a scene per tile type: every tile is boxes on a 10 m cell, so a new hazard is a new
/// entry in <see cref="TileCatalog"/> plus a case in <see cref="BuildHazard"/> — no scene
/// wiring, and the palette picks it up for free.
///
/// Because the whole tile is derived from its data, every peer can build an identical copy
/// from a replicated placement; nothing about the mesh needs to go over the wire.
/// </summary>
[GlobalClass]
public partial class TrackTile : StaticBody3D
{
    /// <summary>Width and depth of a tile in metres.</summary>
    public const float Size = TileCatalog.TileSize;

    private const float Half = Size * 0.5f;
    private const float FloorThickness = 0.2f;
    private const float WallHeight = 1.2f;
    private const float WallThickness = 0.5f;
    private const float WallInset = Half - WallThickness * 0.5f;

    /// <summary>Grid coordinate of this tile along the track (index from the start line).</summary>
    [Export] public int TrackIndex { get; set; }

    public TileData Data { get; private set; } = new();

    /// <summary>Direction a racer is travelling as they enter this tile.</summary>
    public TrackDirection EntryDirection { get; private set; } = TrackDirection.North;

    [Signal] public delegate void TileLandedEventHandler(int trackIndex);

    public TileHazard Hazard => Data.Hazard;

    /// <summary>Ghosts are preview-only: see-through, and nothing collides with them.</summary>
    private bool _isGhost;

    /// <summary>Flat colour a ghost is drawn in — green for a legal drop, red for an illegal one.</summary>
    private Color _ghostTint = Colors.White;

    /// <summary>
    /// Populate from replicated data and build the geometry. Called after placement on every
    /// peer. The tile positions and rotates itself from its grid cell, so callers only supply
    /// the track state.
    /// </summary>
    public void Initialize(TileData data, int trackIndex, Vector2I cell, TrackDirection entryDirection,
                           bool isGhost = false, Color? ghostTint = null)
    {
        Data = data;
        TrackIndex = trackIndex;
        EntryDirection = entryDirection;
        _isGhost = isGhost;
        _ghostTint = ghostTint ?? Colors.White;

        // Road surface: the tire model reads the first node group to pick its grip values.
        if (!isGhost)
            AddToGroup(SurfaceGroups.Road);

        Position = TileCatalog.CellToWorld(cell);
        Rotation = new Vector3(0.0f, entryDirection.Yaw(), 0.0f);

        BuildGeometry();

        if (!isGhost)
            EmitSignal(SignalName.TileLanded, trackIndex);
    }

    private void BuildGeometry()
    {
        TileDefinition definition = TileCatalog.Match(Data);

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
                // Two aprons with a 3 m hole between them.
                AddBox(new Vector3(Size, FloorThickness, 3.5f), new Vector3(0, -FloorThickness * 0.5f, 3.25f),
                       RoadMaterial());
                AddBox(new Vector3(Size, FloorThickness, 3.5f), new Vector3(0, -FloorThickness * 0.5f, -3.25f),
                       RoadMaterial());
                break;

            case TileHazard.IcePatch:
                // The ice replaces the middle of the road rather than sitting on top of it,
                // so there's no lip for the suspension to trip over.
                AddBox(new Vector3(Size, FloorThickness, 2.0f), new Vector3(0, -FloorThickness * 0.5f, 4.0f),
                       RoadMaterial());
                AddBox(new Vector3(Size, FloorThickness, 2.0f), new Vector3(0, -FloorThickness * 0.5f, -4.0f),
                       RoadMaterial());
                BuildIcePatch();
                break;

            default:
                AddBox(new Vector3(Size, FloorThickness, Size), new Vector3(0, -FloorThickness * 0.5f, 0),
                       RoadMaterial());
                break;
        }
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

        AddBox(new Vector3(Size, FloorThickness, 6.0f), new Vector3(0, -FloorThickness * 0.5f, 0),
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

    private static (Vector3 Size, Vector3 Position) WallFor(TrackDirection edge) => edge switch
    {
        TrackDirection.North => (new Vector3(Size, WallHeight, WallThickness),
                                 new Vector3(0, WallHeight * 0.5f, -WallInset)),
        TrackDirection.South => (new Vector3(Size, WallHeight, WallThickness),
                                 new Vector3(0, WallHeight * 0.5f, WallInset)),
        TrackDirection.East => (new Vector3(WallThickness, WallHeight, Size),
                                new Vector3(WallInset, WallHeight * 0.5f, 0)),
        _ => (new Vector3(WallThickness, WallHeight, Size),
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
        const float width = 0.4f;
        const float length = Half;
        const float y = 0.011f;

        (Vector3 size, Vector3 position) = edge switch
        {
            TrackDirection.North => (new Vector3(width, 0.02f, length), new Vector3(0, y, -length * 0.5f)),
            TrackDirection.South => (new Vector3(width, 0.02f, length), new Vector3(0, y, length * 0.5f)),
            TrackDirection.East => (new Vector3(length, 0.02f, width), new Vector3(length * 0.5f, y, 0)),
            _ => (new Vector3(length, 0.02f, width), new Vector3(-length * 0.5f, y, 0)),
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
                // A take-off ramp rising toward the exit. Positioned so the low lip meets the
                // road and the top edge is about 1.3 m up.
                const float angle = 15.0f;
                AddBox(new Vector3(9.0f, 0.4f, 5.0f), new Vector3(0, 0.44f, -1.0f),
                       RampMaterial(definition.Accent), rotationDegrees: new Vector3(angle, 0, 0));
                break;
            }

            case TileHazard.Bottleneck:
            {
                // Two intrusions leaving a ~4.5 m slot down the middle.
                StandardMaterial3D material = WallMaterial(definition.Accent);
                AddBox(new Vector3(WallThickness, WallHeight, 4.0f), new Vector3(2.5f, WallHeight * 0.5f, 0),
                       material);
                AddBox(new Vector3(WallThickness, WallHeight, 4.0f), new Vector3(-2.5f, WallHeight * 0.5f, 0),
                       material);
                break;
            }
        }
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
