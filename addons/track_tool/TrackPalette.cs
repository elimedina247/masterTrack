#if TOOLS
using Godot;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Editor;

/// <summary>
/// The dock that lists every authored track piece and remembers which one is armed.
///
/// Arming is the whole of its job: the palette is where you say <i>what</i> the next piece is, and
/// the "+" handles in the viewport are where you say <i>where</i> it goes. Keeping the two apart is
/// what makes extending rapid — arm a corner once, then click, click, click along the frontier
/// without coming back to any UI.
///
/// The list is the contents of <see cref="PiecesFolder"/> — <see cref="PieceCatalog.ScenePaths"/>'s
/// order, so the dock, the deck and the specimen row always agree about what exists. It follows the
/// editor's own filesystem scanner, so a freshly saved or mirrored piece appears on its own; the
/// Refresh button stays for the cases the scanner has not noticed yet.
/// </summary>
public partial class TrackPalette : VBoxContainer
{
	/// <summary>Where authored pieces live. A piece is a scene in this folder; being here is what
	/// puts it on the palette, with no registration step to forget.</summary>
	public const string PiecesFolder = "res://scenes/tiles/pieces";

	private ItemList? _list;

	/// <summary>Raised when the armed piece changes, so the plugin can move its ghost previews to
	/// show the new piece without waiting for a click.</summary>
	public event System.Action? ArmedChanged;

	/// <summary>Resource path of the armed piece scene, or empty when nothing is armed.</summary>
	public string ArmedScenePath
		=> _list is { } list && list.GetSelectedItems() is { Length: > 0 } selected
			? list.GetItemMetadata(selected[0]).AsString()
			: "";

	public TrackPalette()
	{
		// The dock tab takes its label from the node name.
		Name = "Track Pieces";
	}

	public override void _Ready()
	{
		var refresh = new Button
		{
			Text = "Refresh",
			TooltipText = $"Re-scan {PiecesFolder} for piece scenes.",
		};
		refresh.Pressed += Rescan;
		AddChild(refresh);

		_list = new ItemList
		{
			SelectMode = ItemList.SelectModeEnum.Single,
			SizeFlagsVertical = SizeFlags.ExpandFill,
			TooltipText = "Click a piece to arm it, then click a + handle on an open seam in the "
						  + "viewport to build it there.",
		};
		_list.ItemSelected += _ => ArmedChanged?.Invoke();
		AddChild(_list);

		Rescan();
	}

	/// <summary>
	/// Follow the editor's filesystem scanner, and stop following it before being freed — the
	/// signal outlives the dock, and a handler on a freed control is a crash on the next save.
	/// </summary>
	public override void _EnterTree()
		=> EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged += OnFilesystemChanged;

	public override void _ExitTree()
		=> EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged -= OnFilesystemChanged;

	/// <summary>
	/// The scanner reports every change anywhere in the project, so this rebuilds only when the
	/// piece list actually differs — a rescan re-queues every thumbnail, which is not a price to
	/// pay for saving an unrelated script.
	/// </summary>
	private void OnFilesystemChanged()
	{
		if (_list == null)
			return;

		string[] paths = PieceCatalog.ScenePaths();

		if (paths.Length == _list.ItemCount)
		{
			var same = true;
			for (var i = 0; i < paths.Length && same; i++)
				same = _list.GetItemMetadata(i).AsString() == paths[i];

			if (same)
				return;
		}

		Rescan();
	}

	/// <summary>
	/// Rebuild the list from the folder — <see cref="PieceCatalog.ScenePaths"/>, so every consumer
	/// of the folder shares one idea of its contents. Every scene file counts: the folder is
	/// dedicated to pieces, and a stricter test — instancing each scene to type-check its root —
	/// would make opening the dock cost as much as opening every piece.
	/// </summary>
	private void Rescan()
	{
		if (_list == null)
			return;

		_list.Clear();

		// Disposed at the end of the scan on purpose. A RefCounted wrapper that nobody disposes
		// stays registered until .NET shuts down, and by then the editor has already torn the
		// native object down — unreffing it there is an access violation in the disposal tracker.
		// Everything the list needs it keeps its own native reference to; the wrapper is scaffolding.
		using Texture2D icon = GetThemeIcon("PackedScene", "EditorIcons");

		foreach (string path in PieceCatalog.ScenePaths())
		{
			int item = _list.AddItem(path.GetFile().GetBaseName(), icon);
			_list.SetItemMetadata(item, path);
			_list.SetItemTooltip(item, path);

			// The editor's own thumbnailer, asynchronously; the class icon holds the seat until a
			// real picture arrives, and simply stays if one never does. Never queued headless:
			// with no renderer the request would sit in the queue forever.
			if (DisplayServer.GetName() != "headless")
			{
				EditorInterface.Singleton.GetResourcePreviewer().QueueResourcePreview(
					path, this, MethodName.OnPreviewReady, item);
			}
		}
	}

	/// <summary>Callback from the editor's preview generator: swap the placeholder icon for the
	/// scene's actual thumbnail.</summary>
	public void OnPreviewReady(string path, Texture2D? preview, Texture2D? thumbnail, Variant userData)
	{
		// Same discipline as the scan's icon: the list keeps its own reference to the texture, and
		// wrappers that live to shutdown are how the editor crashes on exit.
		using (preview)
		using (thumbnail)
		{
			int item = userData.AsInt32();

			if (_list == null || preview == null || item < 0 || item >= _list.ItemCount)
				return;

			// The list may have been rebuilt while the preview was rendering; only decorate the
			// item if it is still the one that asked.
			if (_list.GetItemMetadata(item).AsString() == path)
				_list.SetItemIcon(item, preview);
		}
	}
}
#endif
