using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using System.Collections.Generic;

namespace MasterTrack.UI;

/// <summary>
/// The game-over board: who won, who completed the race in what order, who was still driving
/// when it ended, and — greyed out at the bottom — who died on the way.
///
/// Winning used to be one line of text on the waiting label. This is the moment the whole match
/// was for, so it gets the screen: a dimmed backdrop and a leaderboard in the players' own
/// colours, up for the victory linger until the manager takes everyone back to the lobby.
///
/// Three endings share the board, told apart by the manager's ledger rather than by extra
/// plumbing:
/// <list type="bullet">
/// <item>A racer crossed the line — their name in headline, finishers listed by place.</item>
/// <item>Every racer died in Sentry mode — the sentry's name in headline: <c>{NAME} WON !!!</c>.
/// The kill list <i>is</i> the leaderboard that time.</item>
/// <item>Every racer died in Live Build — <c>UNFINISHED</c>, and nobody's name on it.</item>
/// </list>
///
/// Built entirely from replicated state (<see cref="GameManager.FinishOrder"/>,
/// <see cref="GameManager.EliminatedOrder"/>, names, appearances), so every peer draws the same
/// board without a packet being added.
/// </summary>
public partial class MatchResults : Control
{
	private static readonly Color HeadlineGold = new(1.0f, 0.82f, 0.12f);
	private static readonly Color DeadGrey = new(0.55f, 0.55f, 0.58f);

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		// The dim: the race is over, the board is the subject now.
		var dim = new ColorRect { Color = new Color(0.0f, 0.0f, 0.0f, 0.55f) };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		dim.MouseFilter = MouseFilterEnum.Ignore;
		AddChild(dim);

		var column = new VBoxContainer
		{
			Alignment = BoxContainer.AlignmentMode.Center,
		};
		column.AddThemeConstantOverride("separation", 10);
		column.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		column.OffsetLeft = -420;
		column.OffsetRight = 420;
		column.OffsetTop = -320;
		column.OffsetBottom = 320;
		AddChild(column);

		GameManager manager = GameManager.Instance;

		AddHeadline(column, Headline(manager));
		AddSpacer(column, 18);

		BuildRows(column, manager);
	}

	/// <summary>The one line at the top: who this match belonged to.</summary>
	private static string Headline(GameManager manager)
	{
		int winner = manager.WinnerPeerId;

		// Nobody finished and nobody is left. In Sentry mode the winner IS the sentry — the
		// manager crowned them when the last racer went down — so 0 here only ever means a
		// Live Build race that outlived its whole grid.
		if (winner == 0)
			return "UNFINISHED";

		if (!NetworkManager.Instance.IsNetworked)
			return "You win!";

		string name = manager.NameOf(winner);

		// The sentry cannot cross a finish line: their name at the top means everyone died on
		// their board, which deserves the louder sentence.
		return winner == manager.TrackMasterPeerId && manager.TrackMasterPeerId != 0
			? $"{name.ToUpperInvariant()} WON !!!"
			: $"{name} wins!";
	}

	/// <summary>
	/// The leaderboard: finishers by place, then whoever was still driving (by how far up the
	/// track they got), then the dead — greyed, at the bottom, latest death first, because
	/// outliving the rest of the graveyard is the one ranking the dead still have.
	/// </summary>
	private void BuildRows(VBoxContainer column, GameManager manager)
	{
		var listed = new HashSet<int>();
		int place = 0;

		foreach (int peerId in manager.FinishOrder)
		{
			place++;
			listed.Add(peerId);
			AddRow(column, $"{place}.", DisplayName(manager, peerId), PaintOf(manager, peerId));
		}

		// Still on the road when the match concluded: ranked by progress, because "was ahead"
		// is the only fair tiebreak a race that just ended can offer them.
		var driving = new List<(int PeerId, int Progress)>();
		foreach (RacerController car in LiveCars())
		{
			if (!listed.Contains(car.OwnerPeerId) && !manager.IsEliminated(car.OwnerPeerId))
				driving.Add((car.OwnerPeerId, car.CurrentTrackIndex));
		}

		driving.Sort((a, b) => b.Progress.CompareTo(a.Progress));

		foreach ((int peerId, _) in driving)
		{
			place++;
			listed.Add(peerId);
			AddRow(column, $"{place}.", DisplayName(manager, peerId), PaintOf(manager, peerId));
		}

		if (manager.EliminatedOrder.Count == 0)
			return;

		AddSpacer(column, 12);

		for (int i = manager.EliminatedOrder.Count - 1; i >= 0; i--)
		{
			int peerId = manager.EliminatedOrder[i];
			if (!listed.Add(peerId))
				continue;

			AddRow(column, "✕", DisplayName(manager, peerId), DeadGrey, dead: true);
		}
	}

	// ---- Row construction ----

	private static void AddHeadline(VBoxContainer column, string text)
	{
		var label = new Label
		{
			Text = text,
			HorizontalAlignment = HorizontalAlignment.Center,
			SelfModulate = HeadlineGold,
		};
		label.AddThemeFontSizeOverride("font_size", 64);
		label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
		label.AddThemeConstantOverride("outline_size", 14);
		column.AddChild(label);
	}

	private static void AddRow(VBoxContainer column, string rank, string name, Color colour,
							   bool dead = false)
	{
		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 14);
		column.AddChild(row);

		Label lead = RowLabel(rank, dead);
		lead.CustomMinimumSize = new Vector2(44, 0);
		lead.HorizontalAlignment = HorizontalAlignment.Right;
		row.AddChild(lead);

		Label who = RowLabel(name, dead);
		who.SelfModulate = colour;
		row.AddChild(who);
	}

	private static Label RowLabel(string text, bool dead)
	{
		var label = new Label { Text = text };
		label.AddThemeFontSizeOverride("font_size", dead ? 24 : 30);
		label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
		label.AddThemeConstantOverride("outline_size", 8);

		if (dead)
			label.SelfModulate = DeadGrey;

		return label;
	}

	private static void AddSpacer(VBoxContainer column, int height)
		=> column.AddChild(new Control { CustomMinimumSize = new Vector2(0, height) });

	// ---- Reading the session ----

	private static string DisplayName(GameManager manager, int peerId)
	{
		if (!NetworkManager.Instance.IsNetworked)
			return "You";

		return manager.NameOf(peerId);
	}

	private static Color PaintOf(GameManager manager, int peerId)
	{
		Color paint = manager.AppearanceOf(peerId).Paint;

		// The stock grey a peer wears before being dealt a colour is unreadable on the dim.
		return paint.Luminance < 0.2f ? Colors.White : paint;
	}

	/// <summary>Every car still standing in the world, dead ones excluded — they left the
	/// racers group when they were switched off.</summary>
	private IEnumerable<RacerController> LiveCars()
	{
		foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
		{
			if (node is RacerController { OwnerPeerId: > 0 } car && car.IsInsideTree())
				yield return car;
		}
	}
}
