using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// Which way traffic crosses a connector. A seam is one or the other; a piece drivable in both
/// directions is a later idea and earns a third value when something needs it.
/// </summary>
public enum ConnectorRole
{
	/// <summary>The racer arrives through this seam.</summary>
	Entry,

	/// <summary>The racer leaves through this seam, and the next piece is placed here.</summary>
	Exit,
}

/// <summary>
/// A seam on a <see cref="TrackPiece"/>: where another piece attaches, and everything the joint
/// needs to know to attach one.
///
/// <b>The transform is most of the contract.</b> A Node3D's basis already carries forward, up,
/// pitch and roll — position is where the racer crosses, local <c>-Z</c> is the direction they are
/// travelling, and rolling the node banks the seam. None of that is duplicated into properties,
/// because two copies of an orientation is one copy that lies. What is exported here is only what a
/// transform cannot say:
///
/// <list type="bullet">
/// <item><see cref="Role"/> — whether the racer arrives or leaves through it.</item>
/// <item><see cref="Width"/> — how wide the road is at the seam, so two pieces meeting at
/// different widths can be warned about before a car finds the step.</item>
/// <item><see cref="Profile"/> — which cross-section family the seam is, so a half-pipe never
/// quietly butts onto a flat road.</item>
/// </list>
///
/// It extends <see cref="Marker3D"/> on purpose: every existing lookup that asks for a Marker3D
/// named Entry or Exit keeps finding it, and a plain Marker3D in an old piece scene keeps working
/// too — the piece just assumes this class's defaults for it. Upgrading a piece is replacing the
/// node type, not rebuilding the piece.
/// </summary>
[Tool]
[GlobalClass]
public partial class TrackConnector : Marker3D
{
	/// <summary>
	/// The profile every piece is unless it says otherwise: an ordinary flat road. Matching is by
	/// string so a new family — a half-pipe, a narrow lane — is a name somebody types on two
	/// pieces, not a code change.
	/// </summary>
	public const string DefaultProfile = "road";

	/// <summary>Whether the racer arrives or leaves through this seam.</summary>
	[Export]
	public ConnectorRole Role { get; set; } = ConnectorRole.Exit;

	private float _width = TileCatalog.TileSize;

	/// <summary>
	/// How wide the road is where it crosses this seam, in metres.
	///
	/// A seam's transform says where and which way; it says nothing about width, and two pieces of
	/// different widths meet with a step down each side at the exact place a car crosses between
	/// them. Declared here rather than measured from the geometry so the contract exists even for a
	/// piece whose shape is CSG boxes with no swept polygon to measure — the measurement stays as a
	/// warning that the geometry disagrees with the declaration, not as the source of truth.
	/// </summary>
	[Export(PropertyHint.Range, "1,200,0.5,or_greater")]
	public float Width
	{
		get => _width;
		set
		{
			_width = value;

			// The gizmo draws the seam bar at this width, so a change has to reach it now — the
			// fingerprint poll in TrackPiece only watches transforms.
			if (Engine.IsEditorHint() && IsInsideTree())
				UpdateGizmos();
		}
	}

	/// <summary>
	/// Which cross-section family this seam belongs to. Two connectors mate only when their
	/// profiles match, which is the whole compatibility system: a half-pipe end is
	/// <c>halfpipe</c>, an ordinary road is <see cref="DefaultProfile"/>, and the piece that joins
	/// one to the other is a transition piece with a different profile on each end.
	/// </summary>
	[Export(PropertyHint.PlaceholderText, DefaultProfile)]
	public string Profile { get; set; } = DefaultProfile;

	/// <summary>
	/// Whether two connectors can be joined: opposite roles and the same profile. Width is
	/// deliberately not checked here — a mismatch is a visible step, not an impossible joint, and
	/// the authoring warnings are where visible costs get named.
	/// </summary>
	public bool Mates(TrackConnector other)
		=> Role != other.Role
		   && ProfileOf(this) == ProfileOf(other);

	/// <summary>An empty profile reads as the default, so clearing the field never quietly makes a
	/// seam that mates with nothing.</summary>
	public static string ProfileOf(TrackConnector connector)
		=> string.IsNullOrWhiteSpace(connector.Profile) ? DefaultProfile : connector.Profile;
}
