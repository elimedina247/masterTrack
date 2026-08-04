using System.Collections.Generic;
using Godot;

namespace MasterTrack.Audio;

/// <summary>
/// The board's one-shot sound effects: positional, fire-and-forget, and owned by nobody.
///
/// Exists because the things that make noise here mostly cannot hold a player for the length
/// of their own sound — a missile frees itself on the frame it detonates, and the bang has to
/// keep ringing over the crater. <see cref="PlayAt"/> parents the player to the scene root,
/// plays the sample once, and the player frees itself when the sample ends.
/// <see cref="Attach"/> is the other shape: a sound that should travel with a falling thing
/// and be cut off the instant that thing is gone.
///
/// Streams load once per path and stay cached for the life of the process. A missing file —
/// most likely an mp3 the editor hasn't imported yet — warns once and stays silent, the same
/// contract the copilot's callout clips follow.
///
/// Everything goes to the SFX bus, so all of it answers the pause menu's volume slider the
/// way the engine and tire sounds already do.
/// </summary>
public static class Sfx
{
	private static readonly Dictionary<string, AudioStream?> Cache = new();

	private static AudioStream? Load(string path)
	{
		if (Cache.TryGetValue(path, out AudioStream? stream))
			return stream;

		stream = ResourceLoader.Exists(path) ? GD.Load<AudioStream>(path) : null;
		if (stream == null)
			GD.PushWarning($"[Sfx] No audio at {path}; staying silent.");

		// Cached even as a miss, so one absent file is one warning rather than one per bang.
		Cache[path] = stream;
		return stream;
	}

	/// <summary>
	/// Play a sample once at a point in the world, outliving whatever fired it.
	/// <paramref name="context"/> is only the door into the scene tree — any node that is
	/// currently in it will do, including one that frees itself this same frame.
	/// </summary>
	/// <param name="unitSize">The distance, in metres, at which the sample plays at full
	/// volume before distance starts to thin it. Bigger events carry further.</param>
	/// <param name="pitchJitter">Half-width of a random pitch spread around 1.0, so the same
	/// sample twice in a row doesn't sound like a sampler.</param>
	public static void PlayAt(Node context, string path, Vector3 globalPosition,
							  float volumeDb = 0.0f, float unitSize = 10.0f,
							  float pitchJitter = 0.0f)
	{
		if (Load(path) is not { } stream || !context.IsInsideTree())
			return;

		var player = new AudioStreamPlayer3D
		{
			Stream = stream,
			Bus = "SFX",
			VolumeDb = volumeDb,
			UnitSize = unitSize,
			PitchScale = 1.0f + (float)GD.RandRange(-pitchJitter, pitchJitter),
			Position = globalPosition,
			Autoplay = true,
		};
		player.Finished += player.QueueFree;

		// Deferred, because bangs get fired from anywhere — including the middle of a physics
		// step, where the tree is not to be touched. Autoplay starts it as it lands.
		context.GetTree().Root.CallDeferred(Node.MethodName.AddChild, player);
	}

	/// <summary>
	/// Play a sample once with no position at all — the same volume wherever the camera is.
	///
	/// For interface sounds rather than world ones. A cash register answering a purchase is a
	/// fact about the builder's wallet, not an event at a place: putting it through
	/// <see cref="PlayAt"/> would quietly make it quieter whenever the board camera happened to
	/// be zoomed out, which is a volume that means nothing.
	/// </summary>
	public static void PlayUi(Node context, string path, float volumeDb = 0.0f,
							  float pitchJitter = 0.0f)
	{
		if (Load(path) is not { } stream || !context.IsInsideTree())
			return;

		var player = new AudioStreamPlayer
		{
			Stream = stream,
			Bus = "SFX",
			VolumeDb = volumeDb,
			PitchScale = 1.0f + (float)GD.RandRange(-pitchJitter, pitchJitter),
			Autoplay = true,
		};
		player.Finished += player.QueueFree;

		// Parented to the root and deferred, PlayAt's rule: whatever fired this may well be
		// rebuilding the control it lived on during this same frame.
		context.GetTree().Root.CallDeferred(Node.MethodName.AddChild, player);
	}

	/// <summary>
	/// A player that rides <paramref name="parent"/>, starts as soon as it enters the tree,
	/// and dies with it — the whistle on a falling missile, the wind under a falling tile.
	/// Returns null (after one warning) if the stream is missing.
	/// </summary>
	public static AudioStreamPlayer3D? Attach(Node3D parent, string path,
											  float volumeDb = 0.0f, float unitSize = 10.0f)
	{
		if (Load(path) is not { } stream)
			return null;

		var player = new AudioStreamPlayer3D
		{
			Stream = stream,
			Bus = "SFX",
			VolumeDb = volumeDb,
			UnitSize = unitSize,
			Autoplay = true,
		};
		parent.AddChild(player);
		return player;
	}
}
