using Godot;
using MasterTrack.Networking;

namespace MasterTrack.Racer;

/// <summary>
/// The dead player's window back into the race: a chase camera on somebody who is still in it.
///
/// Being knocked out must not mean staring at a void until the match ends — especially in Sentry
/// mode, where the last racer standing is the whole show and everyone the sentry already caught
/// is the audience. So elimination hands the screen here: the camera picks a living racer, rides
/// behind them the way their own driver sees them, and a click steps to the next one.
///
/// Purely local. Who this machine is watching is nobody else's business, so nothing here touches
/// the wire — the cars it follows are the same replicated puppets the board's markers read.
///
/// Follows by pose rather than by parenting, because the target is a puppet being slid toward
/// network updates — a camera parented to it would inherit every correction as a jolt, where a
/// lagged chase reads as camerawork.
/// </summary>
public partial class SpectateCamera : Node3D
{
	/// <summary>Metres behind the watched car.</summary>
	private const float FollowDistance = 14.0f;

	/// <summary>Metres above it.</summary>
	private const float FollowHeight = 6.5f;

	/// <summary>How quickly the camera closes on its chase pose, per second. Deliberately looser
	/// than a driving camera: a spectator is watching a car, not steering one, and a bit of float
	/// is what says so.</summary>
	private const float FollowSpeed = 5.0f;

	private Camera3D _camera = null!;
	private Label _caption = null!;

	private RacerController? _target;

	public override void _Ready()
	{
		_camera = new Camera3D { Name = "SpectateEye", Far = 4000.0f };
		AddChild(_camera);

		// The caption rides its own layer so it needs nothing from the match scene's HUD tree.
		var layer = new CanvasLayer { Name = "SpectateHud", Layer = 20 };
		AddChild(layer);

		_caption = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		_caption.AddThemeFontSizeOverride("font_size", 22);
		_caption.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
		_caption.AddThemeConstantOverride("outline_size", 6);
		_caption.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
		_caption.OffsetTop = 26;
		_caption.OffsetBottom = 62;
		_caption.OffsetLeft = -400;
		_caption.OffsetRight = 400;
		layer.AddChild(_caption);

		PickTarget(1);

		// Cut straight to the first target rather than flying there from wherever the player
		// died — the fall is over, and a half-map glide would replay it.
		if (_target != null)
			GlobalTransform = ChasePose(_target);

		_camera.Current = true;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true } mouse)
		{
			switch (mouse.ButtonIndex)
			{
				case MouseButton.Left:
					PickTarget(1);
					break;

				case MouseButton.Right:
					PickTarget(-1);
					break;
			}
		}
	}

	public override void _Process(double delta)
	{
		// The watched car can die or vanish too; move along rather than staring at where it was.
		if (_target == null || !IsInstanceValid(_target) || !_target.IsInsideTree()
			|| !_target.Visible)
		{
			PickTarget(1);
			if (_target == null)
				return;

			GlobalTransform = ChasePose(_target);
		}

		float t = 1.0f - Mathf.Exp(-FollowSpeed * (float)delta);
		Transform3D pose = ChasePose(_target);

		GlobalPosition = GlobalPosition.Lerp(pose.Origin, t);
		GlobalBasis = GlobalBasis.Orthonormalized().Slerp(pose.Basis.Orthonormalized(), t);
	}

	/// <summary>
	/// Behind and above the target along its own heading, looking at it. Yaw only — a spectated
	/// car mid-barrel-roll should tumble in frame, not take the horizon with it.
	/// </summary>
	private static Transform3D ChasePose(RacerController target)
	{
		Vector3 forward = target.GlobalBasis.Z;
		forward.Y = 0.0f;
		forward = forward.LengthSquared() > 0.001f ? forward.Normalized() : Vector3.Forward;

		Vector3 eye = target.GlobalPosition + forward * FollowDistance
					  + Vector3.Up * FollowHeight;

		return new Transform3D(Basis.LookingAt(target.GlobalPosition - eye, Vector3.Up), eye);
	}

	/// <summary>
	/// Step to the next living racer, in a stable order (peer id) so clicking cycles the same
	/// circuit every time. Eliminated cars have already left the racers group, so the group is
	/// the whole answer to "who is still alive".
	/// </summary>
	private void PickTarget(int step)
	{
		var alive = new System.Collections.Generic.List<RacerController>();

		foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
		{
			if (node is RacerController racer && racer.OwnerPeerId > 0
				&& racer.IsInsideTree() && racer.Visible)
				alive.Add(racer);
		}

		if (alive.Count == 0)
		{
			_target = null;
			_caption.Text = "Nobody left to watch.";
			return;
		}

		alive.Sort((a, b) => a.OwnerPeerId.CompareTo(b.OwnerPeerId));

		int index = _target != null ? alive.IndexOf(_target) : -1;
		index = index < 0 ? 0 : Mathf.PosMod(index + step, alive.Count);

		_target = alive[index];

		string name = GameManager.Instance.NameOf(_target.OwnerPeerId);
		_caption.Text = alive.Count > 1
			? $"Spectating {name} — click to cycle"
			: $"Spectating {name}";
	}
}
