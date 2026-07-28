using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// The chequered bar across the far end of the track: a strip painted on the road with a gantry
/// over it, and the thing a racer has to reach to win.
///
/// It lives at the head — the open cell the next tile would go in — so it is always sitting on the
/// far edge of the furthest tile, and it moves forward every time the Track Master lays another
/// one. That is the whole point of it: the Track Master is not building a course, they are holding
/// a line in front of cars that are trying to get to it, and the racers can always see exactly how
/// much track is left between them and winning.
///
/// Nothing here collides. A finish line that could be crashed into would decide races by
/// suspension geometry. Crossing it is <see cref="TrackController"/>'s business, worked out from
/// where the cars are rather than from a physics body.
///
/// <see cref="SetFinal"/> is the difference between "the track stops here for now" and "the track
/// stops here". Until the race has run out of tiles the bar is a moving target and is drawn in
/// plain racing white; once the Track Master has no tiles left to lay it is the actual finish, and
/// it says so.
/// </summary>
public partial class FinishLine : Node3D
{
    /// <summary>Racing white. What the bar is while the track can still grow past it.</summary>
    private static readonly Color Racing = new(0.94f, 0.94f, 0.90f);

    /// <summary>And once it is the real finish. Gold reads as an end from across the board.</summary>
    private static readonly Color Final = new(1.00f, 0.82f, 0.12f);

    /// <summary>Squares across the width of the bar. Even, so the chequer alternates cleanly.</summary>
    private const int Squares = 10;

    private const float PostHeight = 13.0f;
    private const float PostThickness = 1.8f;

    /// <summary>Height of the underside of the gantry beam. Well clear of a car on its roof.</summary>
    private const float BeamHeight = 9.0f;

    private const float BeamDepth = 2.2f;

    private readonly StandardMaterial3D _light = Flat(Racing);
    private readonly StandardMaterial3D _dark = Flat(new Color(0.07f, 0.07f, 0.09f));
    private readonly StandardMaterial3D _postMaterial = Flat(Racing);

    private Label3D _caption = null!;

    public override void _Ready()
    {
        BuildRoadStripe();
        BuildGantry();

        _caption = new Label3D
        {
            Text = "FINISH",
            Position = new Vector3(0.0f, PostHeight + 5.0f, 0.0f),
            PixelSize = 0.06f,
            FontSize = 96,
            OutlineSize = 28,
            Modulate = Final,
            OutlineModulate = new Color(0.0f, 0.0f, 0.0f, 0.85f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Visible = false,
        };
        AddChild(_caption);
    }

    /// <summary>
    /// Put the bar down across a cell boundary. <paramref name="facing"/> is the way the racers
    /// are travelling as they cross it, so the bar is laid at right angles to it.
    /// </summary>
    public void PlaceAt(Vector3 world, TrackDirection facing)
    {
        Position = world;
        Rotation = new Vector3(0.0f, facing.Yaw(), 0.0f);
    }

    /// <summary>
    /// Say whether this is the end of the race or only the end of the track so far. See the class
    /// summary — until the Track Master runs out of tiles the bar is somewhere they are pushing
    /// away from the racers, and calling it the finish would be a lie every time they place one.
    /// </summary>
    public void SetFinal(bool final)
    {
        _postMaterial.AlbedoColor = final ? Final : Racing;

        if (_caption != null)
            _caption.Visible = final;
    }

    /// <summary>
    /// The chequer painted across the road. Every square is drawn, light and dark alike, rather
    /// than half of them left as bare tarmac: the bar sits on the boundary between the last tile
    /// and thin air, so half of it would be painted on nothing.
    /// </summary>
    private void BuildRoadStripe()
    {
        const float depth = TileCatalog.TileSize * 0.075f;
        const float lift = 0.03f;
        const float square = TileCatalog.TileSize / Squares;

        for (int i = 0; i < Squares; i++)
        {
            float offset = (i - (Squares - 1) * 0.5f) * square;
            AddBox(new Vector3(square, lift * 2.0f, depth),
                   new Vector3(offset, lift, 0.0f),
                   i % 2 == 0 ? _light : _dark);
        }
    }

    /// <summary>A post either side and a chequered beam across the top of them.</summary>
    private void BuildGantry()
    {
        const float half = TileCatalog.TileSize * 0.5f;

        foreach (int side in new[] { -1, 1 })
        {
            AddBox(new Vector3(PostThickness, PostHeight, PostThickness),
                   new Vector3(side * half, PostHeight * 0.5f, 0.0f),
                   _postMaterial);
        }

        // The beam carries the same chequer as the road, so the bar reads as one thing whether it
        // is being looked at from the board straight down or through a windscreen.
        const float square = TileCatalog.TileSize / Squares;
        for (int i = 0; i < Squares; i++)
        {
            float offset = (i - (Squares - 1) * 0.5f) * square;
            AddBox(new Vector3(square, BeamDepth, BeamDepth),
                   new Vector3(offset, BeamHeight + BeamDepth * 0.5f, 0.0f),
                   i % 2 == 0 ? _light : _dark);
        }
    }

    private void AddBox(Vector3 size, Vector3 position, Material material)
        => AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size, Material = material },
            Position = position,
        });

    /// <summary>
    /// The same per-vertex, no-specular look every tile is built with — see
    /// <see cref="TrackTile"/> — so the bar sits in the world rather than on top of it.
    /// </summary>
    private static StandardMaterial3D Flat(Color color) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
        Metallic = 0.0f,
        Roughness = 1.0f,
    };
}
