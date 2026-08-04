using System;
using Godot;
using MasterTrack.Tiles;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The Track Master's hand: a fixed row of slots that fill themselves with random tiles over
/// time and empty as those tiles are spent on the track.
///
/// This is what turns building from "pick the perfect piece" into spending a resource. The
/// Track Master doesn't choose what they get — <see cref="TileCatalog.DrawIndex"/> does, by
/// weight — they choose what to do with it and when. Holding a Gap back for the corner where it
/// will hurt costs a slot the whole time it waits, and a hand that runs dry while the racers are
/// eating track is the pressure the role is built on.
///
/// The tiles are kept packed against the left of the row: spending one closes the gap behind it,
/// the way a hand of cards does. That keeps the empties in one run on the right, which is what
/// lets the countdown to the next tile live in a fixed spot at the end of the row.
///
/// The deal clock only runs when there is somewhere for a tile to go. A full hand is a stalled
/// hand, and it holds its progress rather than banking or losing it — spend a tile and the count
/// picks up where it stopped.
///
/// <b>One thing is not left to the draw: altitude.</b> The track is not allowed underground, so a
/// card that goes down is only a card at all once the Track Master has been given the climb to spend
/// on it. See <see cref="DescentBudget"/> — the deck is filtered by what the track and the hand
/// between them can pay for, rather than dealing drops at ground level and letting the placement
/// rules refuse them after the slot is already gone.
/// </summary>
public sealed class TileHand
{
	/// <summary>Value of a slot with no tile in it.</summary>
	public const int Empty = -1;

	private readonly int[] _slots;
	private readonly RandomNumberGenerator _rng = new();

	/// <summary>
	/// How high the track's head is standing right now, in metres, or null when nothing said. The
	/// one thing the hand asks about the track, and it asks it as a function because the answer
	/// changes under it — see <see cref="DescentBudget"/>.
	/// </summary>
	private readonly Func<float>? _headHeight;

	/// <summary>Seconds accumulated toward the next tile.</summary>
	private float _elapsed;

	/// <summary>Whether the always-available pieces are barred from the draw. See the constructor.</summary>
	private readonly bool _excludeStaples;

	/// <summary>
	/// Slack on the descent budget, in metres. The same tolerance <c>TrackGrid.GroundEpsilon</c>
	/// gives the ground plane, and for the same reason: a climb and the drop that undoes it are
	/// two separately authored pieces, and their rises should not have to agree to the last bit.
	/// </summary>
	private const float HeightSlack = 0.01f;

	/// <summary>
	/// A hand of <paramref name="slotCount"/> slots, dealing one tile every
	/// <paramref name="dealInterval"/> seconds, opening with <paramref name="startingTiles"/>
	/// already in it. The opening hand matters: with nothing to place, the Track Master would
	/// spend the first seconds of the race watching the cars drive off the end of the track.
	///
	/// <paramref name="headHeight"/> is how high the track currently ends, in metres. Left out,
	/// the hand deals as though the track were on the deck at all times, which is the safe reading
	/// rather than the permissive one — see <see cref="DescentBudget"/>.
	///
	/// <paramref name="excludeStaples"/> bars the always-available pieces from the draw. Set
	/// wherever the staples bar is on screen: a dealt straight is a slot spent on something the
	/// Track Master could already have had for nothing, which is strictly worse than an empty
	/// slot — it also occupies the hand until it is thrown away. What the deck is <i>for</i>, once
	/// the basics are free, is the pieces that make a track interesting.
	/// </summary>
	public TileHand(int slotCount, float dealInterval, int startingTiles = 0,
					Func<float>? headHeight = null, bool excludeStaples = false)
	{
		_slots = new int[Mathf.Max(1, slotCount)];
		DealInterval = Mathf.Max(0.05f, dealInterval);
		_headHeight = headHeight;
		_excludeStaples = excludeStaples;
		_rng.Randomize();

		// Drawn the same weighted way as everything after them, so an opening hand is a fair
		// sample of the deal rather than a scripted set.
		for (int i = 0; i < startingTiles && !IsFull; i++)
			Deal(null);
	}

	/// <summary>Seconds between one dealt tile and the next.</summary>
	public float DealInterval { get; set; }

	public int SlotCount => _slots.Length;

	/// <summary>How many tiles are in hand. They always occupy the first <c>Count</c> slots.</summary>
	public int Count { get; private set; }

	public bool IsFull => Count >= _slots.Length;

	/// <summary>Catalog index held in a slot, or <see cref="Empty"/> if that slot is empty.</summary>
	public int At(int slot) => slot >= 0 && slot < Count ? _slots[slot] : Empty;

	/// <summary>
	/// The rightmost empty slot — the end of the row — or -1 when the hand is full. This is
	/// where the countdown to the next tile is shown; because the tiles stay packed left it
	/// doesn't move around as the hand fills and empties.
	/// </summary>
	public int CooldownSlot => IsFull ? -1 : _slots.Length - 1;

	/// <summary>How far along the next tile is, 0 to 1. Frozen while the hand is full.</summary>
	public float DealProgress => Mathf.Clamp(_elapsed / DealInterval, 0.0f, 1.0f);

	/// <summary>Seconds until the next tile lands.</summary>
	public float TimeToNextDeal => Mathf.Max(0.0f, DealInterval - _elapsed);

	/// <summary>
	/// Advance the deal clock, dealing a tile if one is due. Returns true when the contents of
	/// the hand actually changed, so the caller only refreshes the tray when there's a reason.
	/// </summary>
	public bool Tick(float delta, Func<int, bool>? placeable = null)
	{
		// Nowhere to put it: the clock stops rather than running on and losing the progress.
		if (IsFull)
			return false;

		_elapsed += delta;
		if (_elapsed < DealInterval)
			return false;

		// Carried rather than zeroed, so a long frame doesn't quietly stretch the interval.
		_elapsed -= DealInterval;
		Deal(placeable);
		return true;
	}

	/// <summary>
	/// Metres of descent the hand can pay for: how high the track ends now, plus every metre the
	/// cards already in hand would climb, minus every metre they would drop.
	///
	/// <b>The track cannot go below the ground plane</b> — <c>TrackGrid.Fits</c> refuses it outright
	/// — so a card that goes down is only worth anything to a Track Master who has been up. Dealing
	/// one to a hand at ground level hands them a card they can never legally play; it sits in a slot
	/// until it is thrown away, and while it sits there it is one fewer tile of road in a game about
	/// laying road faster than the cars eat it.
	///
	/// Counted against the hand and not only against the track, because a climb the Track Master is
	/// <i>holding</i> is a climb they have been dealt: the drop that follows it is a card worth
	/// having. The reverse matters as much — two drops already in hand have spent the altitude twice
	/// over, and a third is dead weight however high the track currently stands.
	/// </summary>
	private float DescentBudget()
	{
		float budget = _headHeight?.Invoke() ?? 0.0f;

		for (int i = 0; i < Count; i++)
			budget += TileCatalog.RiseOf(_slots[i]);

		return budget;
	}

	/// <summary>
	/// Whether a card is one the hand is allowed to be dealt right now. Anything level or climbing
	/// always is; a descent has to fit inside <paramref name="budget"/>.
	/// </summary>
	private static bool CanAfford(float budget, int index)
	{
		float rise = TileCatalog.RiseOf(index);
		return rise >= 0.0f || budget + rise >= -HeightSlack;
	}

	/// <summary>Whether any catalog entry at all passes a filter.</summary>
	private static bool AnyAllowed(Func<int, bool> allow)
	{
		for (int i = 0; i < TileCatalog.All.Count; i++)
		{
			if (allow(i))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Put one weighted-random tile in the next free slot. Callers check for room.
	///
	/// Every draw is filtered by <see cref="CanAfford"/>, so the hand never fills with descents the
	/// track has no altitude to spend on. The weights are renormalised across whatever survives, so
	/// this stays a weighted draw rather than turning into a flat one whenever the track is down on
	/// the deck.
	///
	/// If <paramref name="placeable"/> is given and the hand currently holds nothing that can go on
	/// the track, the draw is narrowed again to what can — the backstop that stops a Track Master
	/// sitting on a full hand of tiles they are not allowed to play while the racers eat the road in
	/// front of them. That filter is only reached for when the hand is already stuck, so a healthy
	/// hand is dealt exactly as it was and the rescue never shows up as a bias in what gets dealt.
	///
	/// When the two filters have nothing in common, the height rule is the one left standing. A card
	/// that is placeable this instant but unaffordable is a card whose altitude has already been
	/// promised to a drop in hand; dealing it would clear the jam by digging a hole.
	/// </summary>
	private void Deal(Func<int, bool>? placeable)
	{
		float budget = DescentBudget();

		// The staple bar is folded into the affordability filter rather than checked separately,
		// so it is carried by every draw below — including the stuck-hand rescue, which would
		// otherwise reach for a straight and hand back a card the builder already has.
		bool Affordable(int index)
			=> CanAfford(budget, index) && !(_excludeStaples && TileCatalog.IsStaple(index));

		if (placeable != null && !HasPlaceable(placeable))
		{
			bool Rescue(int index) => Affordable(index) && placeable(index);

			if (AnyAllowed(Rescue))
			{
				_slots[Count++] = TileCatalog.DrawIndex(_rng, Rescue);
				return;
			}
		}

		_slots[Count++] = TileCatalog.DrawIndex(_rng, Affordable);
	}

	/// <summary>Whether any tile in hand could go on the track as it stands.</summary>
	public bool HasPlaceable(Func<int, bool> placeable)
	{
		for (int i = 0; i < Count; i++)
		{
			if (placeable(_slots[i]))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Deal a placeable tile straight away if the hand has none and there is room for one. Returns
	/// true when it did, so the caller knows to refresh the tray.
	///
	/// The filtered deal in <see cref="Deal"/> already guarantees the hand recovers, but only at the
	/// next interval — and at a 2.4 second deal that is over a hundred metres of road the racers are
	/// already driving into. This is what makes the recovery immediate.
	///
	/// It leaves the deal clock alone. The tile is a rescue, not an advance on the next one, so the
	/// countdown the Track Master is watching does not jump.
	/// </summary>
	public bool TopUp(Func<int, bool> placeable)
	{
		if (IsFull || HasPlaceable(placeable))
			return false;

		Deal(placeable);
		return true;
	}

	/// <summary>
	/// Spend the tile in a slot and close the hand up behind it. Returns the catalog index that
	/// was taken, or <see cref="Empty"/> if that slot held nothing.
	/// </summary>
	public int Take(int slot)
	{
		int index = At(slot);
		if (index == Empty)
			return Empty;

		for (int i = slot; i < Count - 1; i++)
			_slots[i] = _slots[i + 1];

		Count--;
		return index;
	}
}
