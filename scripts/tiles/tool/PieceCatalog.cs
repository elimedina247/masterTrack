using System;
using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>One seam of a catalogued piece, as read from its scene file: where it is in the
/// piece's own space, and the contract it declares.</summary>
public sealed class PieceSeam
{
	public required Transform3D Local { get; init; }
	public required ConnectorRole Role { get; init; }
	public required float Width { get; init; }
	public required string Profile { get; init; }
}

/// <summary>One hazard slot of a catalogued piece, as read from its scene file: where it sits
/// in the <b>entry seam's frame</b> — the same frame the route speaks — and what mounting it
/// offers. See <see cref="TrackHazardSlot"/> for the authoring side.</summary>
public sealed class PieceSlot
{
	public required Transform3D Local { get; init; }
	public required HazardSlotKind Kind { get; init; }
}

/// <summary>Everything a builder needs to know about one authored piece without instancing it.</summary>
public sealed class PieceEntry
{
	public required string ScenePath { get; init; }

	/// <summary>The file's base name — "Straight", "CurveLeft" — which is also how a sequence
	/// refers to a piece, so renaming a file renames the piece everywhere at once.</summary>
	public required string Name { get; init; }

	/// <summary>Every seam on the piece, in scene order.</summary>
	public required IReadOnlyList<PieceSeam> Seams { get; init; }

	/// <summary>Every hazard slot on the piece, in scene order — which is also the order the
	/// slot index over the wire refers to, so it must be identical on every peer (it is: it
	/// comes from the same byte-identical scene file the seams do).</summary>
	public IReadOnlyList<PieceSlot> Slots { get; init; } = Array.Empty<PieceSlot>();

	/// <summary>How often the piece is dealt, straight off the scene root's
	/// <see cref="TrackPiece.DeckWeight"/>. 0 keeps it out of the deck.</summary>
	public required float DeckWeight { get; init; }

	/// <summary>Whether the scene carries a baked mesh, read from the file. An unbaked piece
	/// rebuilds its CSG every time it is instanced — a hitch per placement, mid-race.</summary>
	public required bool IsBaked { get; init; }

	/// <summary>The card's tooltip, off <see cref="TrackPiece.DeckDescription"/>.</summary>
	public required string DeckDescription { get; init; }

	/// <summary>
	/// The road's course through the piece, in the <b>entry seam's frame</b>: the spine's baked
	/// points read out of the scene file, or the straight line from entry to exit for a piece with
	/// no spine. What footprints are computed from, so "how much room does this piece take" follows
	/// the road a corkscrew actually sweeps rather than the chord it happens to end on.
	/// </summary>
	public required IReadOnlyList<Vector3> Route { get; init; }

	/// <summary>The seam the piece is placed by: its first entry.</summary>
	public PieceSeam? Entry
	{
		get
		{
			foreach (PieceSeam seam in Seams)
			{
				if (seam.Role == ConnectorRole.Entry)
					return seam;
			}

			return null;
		}
	}

	/// <summary>The seams the piece hands the track on through, in scene order.</summary>
	public IEnumerable<PieceSeam> Exits
	{
		get
		{
			foreach (PieceSeam seam in Seams)
			{
				if (seam.Role == ConnectorRole.Exit)
					yield return seam;
			}
		}
	}

	/// <summary>Load and instance the actual piece. The one place the catalog touches the scene.</summary>
	public TrackPiece? Instantiate()
		=> GD.Load<PackedScene>(ScenePath)?.Instantiate() as TrackPiece;

	/// <summary>
	/// The first exit expressed in the entry's frame: the piece's whole effect on a chain, as one
	/// transform. Identity when either seam is missing.
	/// </summary>
	public Transform3D ExitInEntry
	{
		get
		{
			if (Entry is not { } entry)
				return Transform3D.Identity;

			PieceSeam? exit = null;
			foreach (PieceSeam seam in Exits)
			{
				exit = seam;
				break;
			}

			return exit == null
				? Transform3D.Identity
				: entry.Local.Orthonormalized().AffineInverse() * exit.Local.Orthonormalized();
		}
	}

	/// <summary>
	/// Whether the legacy anchor chain can carry this piece: the exit, measured in the entry's
	/// frame, is level — no roll and no pitch beyond a couple of degrees. Heading, distance and
	/// rise are all fine; a position and a yaw can carry those exactly.
	///
	/// This is the gate on a piece being dealt to the Track Master while the grid chain exists.
	/// Banked-seam pieces stay authorable and assemblable; they just wait for the chain to speak
	/// full frames.
	/// </summary>
	public bool IsAnchorChainable
	{
		get
		{
			Basis basis = ExitInEntry.Basis;
			Vector3 forward = basis * Vector3.Forward;
			Vector3 up = basis * Vector3.Up;

			return Mathf.Abs(forward.Y) < 0.05f
				   && up.Y > 0.999f;
		}
	}

	/// <summary>Metres the piece climbs from entry to exit. Negative drops.</summary>
	public float RiseMeters => ExitInEntry.Origin.Y;

	/// <summary>How far along the ground the exit sits from the entry, in metres — the chord, not
	/// the road distance. What a grid tile's RunLength approximates.</summary>
	public float ChordMeters
	{
		get
		{
			Vector3 offset = ExitInEntry.Origin;
			return new Vector2(offset.X, offset.Z).Length();
		}
	}
}

/// <summary>
/// Every authored piece, read from <see cref="PiecesFolder"/> — the folder <i>is</i> the catalog,
/// so authoring a new piece is saving a scene and nothing else.
///
/// <b>Planned from the scene files, never from instances.</b> A builder deciding what fits where
/// needs each piece's seams — where they are, which way they face, how wide — and instancing a
/// scene to ask is paying for meshes, CSG and collision to read three transforms.
/// <see cref="PackedScene.GetState"/> hands over the saved node tree without instantiating any of
/// it, so the whole catalog costs a file parse per piece, once. It also cannot go stale the way a
/// baked cache can: the data is read from the same file the instance will be made from.
///
/// Replication note, same as the old catalog: peers refer to pieces by what
/// <see cref="Entries"/> contains, so every peer needs byte-identical piece files. The list is
/// sorted by name for the same reason.
/// </summary>
public static class PieceCatalog
{
	public const string PiecesFolder = "res://scenes/tiles/pieces";

	private static IReadOnlyList<PieceEntry>? _entries;

	/// <summary>The catalog, built on first use. <see cref="Refresh"/> throws it away.</summary>
	public static IReadOnlyList<PieceEntry> Entries => _entries ??= Scan();

	/// <summary>Forget the scanned catalog, so the next read picks up new or edited pieces.</summary>
	public static void Refresh() => _entries = null;

	/// <summary>A piece by its file base name, or null.</summary>
	public static PieceEntry? Named(string name)
	{
		foreach (PieceEntry entry in Entries)
		{
			if (entry.Name == name)
				return entry;
		}

		return null;
	}

	/// <summary>A piece by its scene path, or null — how a replicated
	/// <see cref="TileData.ScenePath"/> finds its way back to the seams.</summary>
	public static PieceEntry? AtPath(string scenePath)
	{
		foreach (PieceEntry entry in Entries)
		{
			if (entry.ScenePath == scenePath)
				return entry;
		}

		return null;
	}

	/// <summary>
	/// Every piece scene in the folder, in ordinal name order — sorted so the catalog is identical
	/// on every machine, which is what lets an index cross the wire.
	/// </summary>
	public static string[] ScenePaths()
	{
		using DirAccess? dir = DirAccess.Open(PiecesFolder);
		if (dir == null)
			return Array.Empty<string>();

		var paths = new List<string>();

		foreach (string file in dir.GetFiles())
		{
			// Exported projects hand back the imported name, so a scene arrives as .tscn.remap and
			// has to be asked for under its original name.
			string name = file.EndsWith(".remap", StringComparison.Ordinal)
				? file[..^".remap".Length]
				: file;

			if (name.EndsWith(".tscn", StringComparison.Ordinal)
				|| name.EndsWith(".scn", StringComparison.Ordinal))
				paths.Add($"{PiecesFolder}/{name}");
		}

		paths.Sort(StringComparer.Ordinal);
		return paths.ToArray();
	}

	private static IReadOnlyList<PieceEntry> Scan()
	{
		var entries = new List<PieceEntry>();

		foreach (string path in ScenePaths())
		{
			if (GD.Load<PackedScene>(path) is not { } scene)
			{
				GD.PushWarning($"[PieceCatalog] Could not load {path}; it is not in the catalog.");
				continue;
			}

			SceneState state = scene.GetState();

			List<PieceSeam> seams = ReadSeams(state);
			if (seams.Count == 0)
			{
				GD.PushWarning($"[PieceCatalog] {path} declares no seams — no TrackConnector and "
							   + "no Entry/Exit markers — so nothing can chain it. Skipped.");
				continue;
			}

			(float weight, string description) = ReadDeck(state);

			entries.Add(new PieceEntry
			{
				ScenePath = path,
				Name = path.GetFile().GetBaseName(),
				Seams = seams,
				Slots = ReadSlots(state, seams),
				Route = ReadRoute(state, seams),
				DeckWeight = weight,
				DeckDescription = description,
				IsBaked = HasNodeNamed(state, "BakedMesh"),
			});
		}

		return entries;
	}

	private static bool HasNodeNamed(SceneState state, string name)
	{
		for (var i = 0; i < state.GetNodeCount(); i++)
		{
			if (state.GetNodeName(i) == name)
				return true;
		}

		return false;
	}

	/// <summary>
	/// The deck settings off the scene's root node. A scene file only stores what differs from the
	/// class defaults, so the fallbacks here are exactly <see cref="TrackPiece"/>'s own.
	/// </summary>
	private static (float Weight, string Description) ReadDeck(SceneState state)
	{
		var weight = 4.0f;
		var description = "";

		if (state.GetNodeCount() == 0)
			return (weight, description);

		// The root is always the scene state's first node — no path spelling to second-guess.
		for (var p = 0; p < state.GetNodePropertyCount(0); p++)
		{
			switch (state.GetNodePropertyName(0, p))
			{
				case "DeckWeight":
					weight = state.GetNodePropertyValue(0, p).AsSingle();
					break;

				case "DeckDescription":
					description = state.GetNodePropertyValue(0, p).AsString();
					break;
			}
		}

		return (weight, description);
	}

	/// <summary>
	/// The road's course, in the entry seam's frame.
	///
	/// Read from the Spine's saved <see cref="Curve3D"/> when the piece has one — the curve is an
	/// ordinary resource inside the file, so its baked points are available without instancing
	/// anything. A piece with no spine contributes its entry-to-exit chord, which is exact for a
	/// straight and the best available guess for a piece built out of boxes.
	/// </summary>
	private static IReadOnlyList<Vector3> ReadRoute(SceneState state, List<PieceSeam> seams)
	{
		Transform3D intoEntry = Transform3D.Identity;
		foreach (PieceSeam seam in seams)
		{
			if (seam.Role == ConnectorRole.Entry)
			{
				intoEntry = seam.Local.Orthonormalized().AffineInverse();
				break;
			}
		}

		for (var i = 0; i < state.GetNodeCount(); i++)
		{
			if (state.GetNodeName(i) != "Spine"
				|| state.GetNodePath(i, forParent: true).ToString() != ".")
				continue;

			var spineTransform = Transform3D.Identity;
			Curve3D? curve = null;

			for (var p = 0; p < state.GetNodePropertyCount(i); p++)
			{
				switch (state.GetNodePropertyName(i, p))
				{
					case "transform":
						spineTransform = state.GetNodePropertyValue(i, p).AsTransform3D();
						break;

					case "curve":
						curve = state.GetNodePropertyValue(i, p).As<Curve3D>();
						break;
				}
			}

			if (curve is not { PointCount: >= 2 })
				break;

			Vector3[] baked = curve.GetBakedPoints();
			if (baked.Length < 2)
				break;

			var route = new List<Vector3>(baked.Length);
			foreach (Vector3 point in baked)
				route.Add(intoEntry * (spineTransform * point));

			return route;
		}

		// No spine: the chord. Both endpoints in the entry's frame, where the entry itself is the
		// origin by construction.
		var exitLocal = Transform3D.Identity;
		foreach (PieceSeam seam in seams)
		{
			if (seam.Role == ConnectorRole.Exit)
			{
				exitLocal = seam.Local;
				break;
			}
		}

		return new List<Vector3> { Vector3.Zero, intoEntry * exitLocal.Origin };
	}

	/// <summary>
	/// Pull the hazard slots out of a saved scene: every direct child of the root carrying the
	/// <see cref="TrackHazardSlot"/> script, expressed in the entry seam's frame the same way
	/// the route is — so a placed tile's slot transform is <c>EntryFrame * slot.Local</c>, the
	/// exact composition <see cref="PlacedTile.PieceFootprint"/> already uses for the road.
	/// </summary>
	private static IReadOnlyList<PieceSlot> ReadSlots(SceneState state, List<PieceSeam> seams)
	{
		Transform3D intoEntry = Transform3D.Identity;
		foreach (PieceSeam seam in seams)
		{
			if (seam.Role == ConnectorRole.Entry)
			{
				intoEntry = seam.Local.Orthonormalized().AffineInverse();
				break;
			}
		}

		var slots = new List<PieceSlot>();

		for (var i = 0; i < state.GetNodeCount(); i++)
		{
			// Direct children of the root only — a slot is part of the piece's contract, the
			// seams' rule.
			if (state.GetNodePath(i, forParent: true).ToString() != ".")
				continue;

			var transform = Transform3D.Identity;
			var kind = HazardSlotKind.Surface;
			var isSlot = false;

			for (var p = 0; p < state.GetNodePropertyCount(i); p++)
			{
				switch (state.GetNodePropertyName(i, p))
				{
					case "script":
						isSlot = state.GetNodePropertyValue(i, p).As<Resource>()?.ResourcePath
							.EndsWith("TrackHazardSlot.cs", StringComparison.Ordinal) == true;
						break;

					case "transform":
						transform = state.GetNodePropertyValue(i, p).AsTransform3D();
						break;

					case "Kind":
						kind = (HazardSlotKind)state.GetNodePropertyValue(i, p).AsInt32();
						break;
				}
			}

			if (!isSlot)
				continue;

			slots.Add(new PieceSlot
			{
				Local = intoEntry * transform,
				Kind = kind,
			});
		}

		return slots;
	}

	/// <summary>
	/// Pull the seams out of a saved scene: every direct child of the root that is a
	/// <see cref="TrackConnector"/>, plus plain Marker3Ds named Entry or Exit so pieces from
	/// before the connector type keep chaining without being touched.
	///
	/// A scene file only records properties that differ from their defaults, so every read here
	/// falls back to exactly the default the live node would have.
	/// </summary>
	private static List<PieceSeam> ReadSeams(SceneState state)
	{
		var seams = new List<PieceSeam>();

		for (var i = 0; i < state.GetNodeCount(); i++)
		{
			// Only the root's direct children: a seam is part of the piece's contract, and the
			// contract lives at the top of the piece — anything deeper is shape. Asked for as
			// "whose parent is the root" because the parent path is unambiguous, where the node's
			// own path spells the same child differently across engine versions.
			if (state.GetNodePath(i, forParent: true).ToString() != ".")
				continue;

			string name = state.GetNodeName(i);

			var transform = Transform3D.Identity;
			ConnectorRole? role = null;
			float width = TileCatalog.TileSize;
			string profile = TrackConnector.DefaultProfile;
			var isConnector = false;

			for (var p = 0; p < state.GetNodePropertyCount(i); p++)
			{
				switch (state.GetNodePropertyName(i, p))
				{
					case "script":
						isConnector = state.GetNodePropertyValue(i, p).As<Resource>()?.ResourcePath
							.EndsWith("TrackConnector.cs", StringComparison.Ordinal) == true;
						break;

					case "transform":
						transform = state.GetNodePropertyValue(i, p).AsTransform3D();
						break;

					case "Role":
						role = (ConnectorRole)state.GetNodePropertyValue(i, p).AsInt32();
						break;

					case "Width":
						width = state.GetNodePropertyValue(i, p).AsSingle();
						break;

					case "Profile":
						profile = state.GetNodePropertyValue(i, p).AsString();
						break;
				}
			}

			bool legacySeam = state.GetNodeType(i) == "Marker3D"
							  && name is "Entry" or "Exit";

			if (!isConnector && !legacySeam)
				continue;

			seams.Add(new PieceSeam
			{
				Local = transform,
				// An unsaved Role is the class default — Exit — unless the node is named Entry,
				// which is what the name has meant since before roles existed.
				Role = role ?? (name == "Entry" ? ConnectorRole.Entry : ConnectorRole.Exit),
				Width = width,
				Profile = string.IsNullOrWhiteSpace(profile) ? TrackConnector.DefaultProfile : profile,
			});
		}

		return seams;
	}
}
