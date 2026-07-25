using System.Collections.Generic;
using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// The rubber a vehicle's tires leave behind when they slip.
///
/// Each wheel lays down a <b>ribbon</b>: while the tire is sliding, its contact point is
/// sampled every physics step and turned into a pair of vertices either side of the contact
/// patch, stitched to the previous pair as two triangles. That is what makes the mark follow
/// the exact arc the tire took and sit flat on a sloped or banked tile — a particle system
/// emits at discrete points instead, which reads as a dotted line that floats off the road on
/// anything but flat ground.
///
/// All four wheels share this one node and one mesh, so the whole car costs a single draw
/// call. Point them at the vehicle with <see cref="VehicleNode"/>; the node makes itself
/// <see cref="Node3D.TopLevel"/> so its geometry stays in world space no matter where it is
/// parented — marks stay on the track rather than riding along with the car.
///
/// Nothing here is networked. Every peer already runs the full physics for every car (see
/// <c>RacerController._PhysicsProcess</c>), so each client draws its own marks from its own
/// simulation and there is nothing to replicate.
/// </summary>
[GlobalClass]
public partial class SkidMarks : MeshInstance3D
{
    /// <summary>The vehicle whose wheels to watch. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    // ---------------------------------------------------------------- When to draw

    /// <summary>Slip angle in radians past which a tire starts leaving rubber.</summary>
    [ExportGroup("Slip")]
    [Export] public float LateralSlipThreshold { get; set; } = 0.25f;

    /// <summary>Longitudinal slip ratio past which a tire starts leaving rubber.</summary>
    [Export] public float LongitudinalSlipThreshold { get; set; } = 0.2f;

    /// <summary>
    /// How far past the threshold slip has to go for a mark at full darkness. Below this the
    /// mark fades in with slip, so a tire that is only just letting go leaves a faint smear
    /// rather than the same black line as a full lock-up.
    /// </summary>
    [Export] public float FullOpacitySlip { get; set; } = 0.8f;

    /// <summary>Alpha of a mark laid at full slip.</summary>
    [Export] public float MaxOpacity { get; set; } = 0.7f;

    // ---------------------------------------------------------------- Shape

    /// <summary>
    /// How far the contact point must travel before a new pair of vertices is added. This is
    /// what stops a car sitting still with its wheels spinning from burning through the whole
    /// segment budget in a second.
    /// </summary>
    [ExportGroup("Shape")]
    [Export] public float MinSegmentLength { get; set; } = 0.2f;

    /// <summary>Scales the mark's width away from the tire's actual width.</summary>
    [Export] public float WidthScale { get; set; } = 1.0f;

    /// <summary>
    /// How far the mark is lifted along the surface normal, in metres. Without this the mark
    /// is coplanar with the road and z-fights it.
    /// </summary>
    [Export] public float GroundOffset { get; set; } = 0.02f;

    // ---------------------------------------------------------------- Lifetime

    /// <summary>
    /// Total segments across every mark this node will keep. Once the budget is used up the
    /// oldest marks fade out and their segments are recycled, so memory stays flat however
    /// long the race runs. Changing this at runtime reallocates the vertex buffers.
    /// </summary>
    [ExportGroup("Lifetime")]
    [Export] public int MaxSegments { get; set; } = 1200;

    /// <summary>Seconds an over-budget mark takes to fade out once it starts.</summary>
    [Export] public float FadeTime { get; set; } = 1.5f;

    /// <summary>
    /// Mark colour per surface group. A surface that isn't listed leaves no mark at all,
    /// which is how ice stays clean. Alpha here is ignored — <see cref="MaxOpacity"/> and the
    /// slip intensity set it.
    /// </summary>
    [Export] public Godot.Collections.Dictionary<string, Color> SurfaceColors { get; set; } = new()
    {
        { SurfaceGroups.Road, new Color(0.04f, 0.04f, 0.05f) },
        { SurfaceGroups.Dirt, new Color(0.34f, 0.26f, 0.17f) },
        { SurfaceGroups.Grass, new Color(0.17f, 0.19f, 0.10f) },
    };

    /// <summary>One continuous mark, from a tire letting go to it gripping again.</summary>
    private sealed class Strip
    {
        public readonly List<Vector3> Left = new();
        public readonly List<Vector3> Right = new();
        public readonly List<Vector3> Normal = new();
        public readonly List<float> Intensity = new();
        public Color Tint;

        /// <summary>Whole-strip multiplier, driven down by <see cref="FadeTime"/> when over budget.</summary>
        public float Fade = 1.0f;
        public bool IsFading;

        public int SegmentCount => Mathf.Max(0, Left.Count - 1);

        public void DropOldestPoint()
        {
            Left.RemoveAt(0);
            Right.RemoveAt(0);
            Normal.RemoveAt(0);
            Intensity.RemoveAt(0);
        }
    }

    private readonly List<Strip> _strips = new();

    /// <summary>The strip each wheel is currently extending, indexed alongside <c>WheelArray</c>.</summary>
    private readonly List<Strip?> _activeStrips = new();
    private readonly List<Vector3> _lastPoints = new();

    private ArrayMesh _mesh = null!;
    private Vector3[] _vertices = System.Array.Empty<Vector3>();
    private Vector3[] _normals = System.Array.Empty<Vector3>();
    private Color[] _colors = System.Array.Empty<Color>();
    private bool _dirty;

    public override void _Ready()
    {
        // World space: the marks belong to the track, not to the car this node hangs off.
        TopLevel = true;
        GlobalTransform = Transform3D.Identity;

        _mesh = new ArrayMesh();
        Mesh = _mesh;

        MaterialOverride ??= new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            VertexColorUseAsAlbedo = true,
            // Marks overlap each other constantly; writing depth would make them flicker
            // against one another as the camera moves.
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            // Camber tilts a wheel's basis, so a strip's winding isn't reliably up-facing.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Metallic = 0.0f,
            Roughness = 1.0f,
        };
    }

    /// <summary>Wipe every mark. Call this when the track is rebuilt or the race resets.</summary>
    public void Clear()
    {
        _strips.Clear();
        for (int i = 0; i < _activeStrips.Count; i++)
            _activeStrips[i] = null;
        _dirty = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (VehicleNode is not { } vehicle || !IsInstanceValid(vehicle) || !vehicle.IsVehicleReady)
            return;

        // WheelArray isn't populated until the vehicle initialises, so the per-wheel state
        // is sized here rather than in _Ready.
        while (_activeStrips.Count < vehicle.WheelArray.Count)
        {
            _activeStrips.Add(null);
            _lastPoints.Add(Vector3.Zero);
        }

        for (int i = 0; i < vehicle.WheelArray.Count; i++)
            ProcessWheel(vehicle.WheelArray[i], i);
    }

    private void ProcessWheel(Wheel wheel, int index)
    {
        float intensity = TireSlip.Intensity(
            wheel, LateralSlipThreshold, LongitudinalSlipThreshold, FullOpacitySlip);

        // Off the ground, gripping, or on a surface that doesn't mark: end the current strip
        // so the next slide starts a fresh one instead of drawing a line across the gap.
        // TireSlip.Intensity already reads 0 for an airborne wheel, which is also what makes
        // the collision point and normal below safe to use.
        if (intensity <= 0.0f || !SurfaceColors.TryGetValue(wheel.SurfaceType, out Color tint))
        {
            _activeStrips[index] = null;
            return;
        }

        Vector3 point = wheel.LastCollisionPoint;
        Strip? strip = _activeStrips[index];

        if (strip == null)
        {
            strip = new Strip { Tint = tint };
            _strips.Add(strip);
            _activeStrips[index] = strip;
        }
        else if (point.DistanceSquaredTo(_lastPoints[index]) < MinSegmentLength * MinSegmentLength)
        {
            // Hasn't moved far enough to be worth a segment. Keep the tip's intensity current
            // so a slide that deepens on the spot still darkens.
            strip.Intensity[^1] = Mathf.Max(strip.Intensity[^1], intensity);
            return;
        }

        Vector3 normal = wheel.LastCollisionNormal;

        // The wheel's own X axis is its axle, which is exactly the direction the contact patch
        // is wide in. Flattening it against the surface normal keeps the ribbon on the ground
        // rather than tipping it with camber.
        Vector3 lateral = wheel.GlobalTransform.Basis.X;
        lateral -= normal * lateral.Dot(normal);
        if (lateral.LengthSquared() < 0.0001f)
            return;
        lateral = lateral.Normalized() * (wheel.TireWidth * 0.0005f * WidthScale);

        Vector3 lifted = point + normal * GroundOffset;
        strip.Left.Add(lifted - lateral);
        strip.Right.Add(lifted + lateral);
        strip.Normal.Add(normal);
        strip.Intensity.Add(intensity);

        // A single unbroken slide can outrun the whole budget; trim its tail rather than let
        // it grow without bound. At this length the dropped end is far behind the player.
        while (strip.SegmentCount > MaxSegments)
            strip.DropOldestPoint();

        _lastPoints[index] = point;
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        UpdateFade((float)delta);

        if (_dirty)
            RebuildMesh();
    }

    /// <summary>
    /// Retire marks once the segment budget is spent. Strips are held in the order they were
    /// started, so walking from the front marks the oldest as fading first; a strip a wheel is
    /// still extending is never retired out from under it.
    /// </summary>
    private void UpdateFade(float delta)
    {
        int liveSegments = 0;
        foreach (Strip strip in _strips)
        {
            if (!strip.IsFading)
                liveSegments += strip.SegmentCount;
        }

        for (int i = 0; i < _strips.Count && liveSegments > MaxSegments; i++)
        {
            Strip strip = _strips[i];
            if (strip.IsFading || _activeStrips.Contains(strip))
                continue;

            strip.IsFading = true;
            liveSegments -= strip.SegmentCount;
        }

        for (int i = _strips.Count - 1; i >= 0; i--)
        {
            Strip strip = _strips[i];
            if (!strip.IsFading)
                continue;

            strip.Fade -= delta / Mathf.Max(FadeTime, 0.001f);
            _dirty = true;

            if (strip.Fade <= 0.0f)
                _strips.RemoveAt(i);
        }
    }

    /// <summary>
    /// Rewrite the whole mesh from the strip list.
    ///
    /// The vertex buffers are allocated once at the segment budget and always submitted whole,
    /// with the unused tail collapsed onto a single point so those triangles are degenerate
    /// and cost nothing. That keeps this to one bulk call into the engine per rebuild instead
    /// of one per vertex, and allocates nothing per frame.
    /// </summary>
    private void RebuildMesh()
    {
        _dirty = false;

        int capacity = Mathf.Max(MaxSegments, 1) * 6;
        if (_vertices.Length != capacity)
        {
            _vertices = new Vector3[capacity];
            _normals = new Vector3[capacity];
            _colors = new Color[capacity];
        }

        int v = 0;
        foreach (Strip strip in _strips)
        {
            for (int i = 0; i + 1 < strip.Left.Count && v + 6 <= capacity; i++)
            {
                Color near = strip.Tint;
                near.A = strip.Intensity[i] * strip.Fade * MaxOpacity;
                Color far = strip.Tint;
                far.A = strip.Intensity[i + 1] * strip.Fade * MaxOpacity;

                // Wound counter-clockwise seen from above: (L0, R0, L1) and (R0, R1, L1).
                Write(ref v, strip.Left[i], strip.Normal[i], near);
                Write(ref v, strip.Right[i], strip.Normal[i], near);
                Write(ref v, strip.Left[i + 1], strip.Normal[i + 1], far);

                Write(ref v, strip.Right[i], strip.Normal[i], near);
                Write(ref v, strip.Right[i + 1], strip.Normal[i + 1], far);
                Write(ref v, strip.Left[i + 1], strip.Normal[i + 1], far);
            }
        }

        _mesh.ClearSurfaces();
        if (v == 0)
            return;

        // Collapse the unused tail onto the last real vertex: zero-area triangles that also
        // keep the mesh's bounding box tight, so culling still works.
        Vector3 collapse = _vertices[v - 1];
        for (int i = v; i < capacity; i++)
        {
            _vertices[i] = collapse;
            _normals[i] = Vector3.Up;
            _colors[i] = new Color(0, 0, 0, 0);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices;
        arrays[(int)Mesh.ArrayType.Normal] = _normals;
        arrays[(int)Mesh.ArrayType.Color] = _colors;
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    private void Write(ref int v, Vector3 position, Vector3 normal, Color color)
    {
        _vertices[v] = position;
        _normals[v] = normal;
        _colors[v] = color;
        v++;
    }
}
