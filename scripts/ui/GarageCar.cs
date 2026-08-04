using Godot;

namespace MasterTrack.UI;

/// <summary>
/// One garage silhouette, drawn from parts so the player's picks show up on the car itself: the
/// body layer tinted with the chosen paint, the trim over it, and the whip antenna at whichever
/// mount is chosen.
///
/// The art in <c>assets/ui/garage/</c> is two files per body for exactly this reason. A single flat
/// SVG bakes the paint into the shell and the antenna into one spot, so the only thing a picker
/// could change was the caption under the car. Here <c>car_x.svg</c> is the shell alone in white —
/// tinting white gives back the palette colour unchanged — and <c>car_x_trim.svg</c> is everything
/// that is not paint: glass, rubber, wings, the panda's livery. Both share the art viewBox, so they
/// stack with no arithmetic. The antenna is not in either: it is three points per body and a line.
/// </summary>
public partial class GarageCar : Control
{
    /// <summary>The art's own coordinate space. Every garage SVG carries this viewBox, and the
    /// antenna mounts in <see cref="MainMenu"/> are written in it.</summary>
    public const float ArtWidth = 200f;

    /// <summary>Whip length and ball, in art units — the dimensions the antenna was drawn at back
    /// when it was part of the SVG, so it comes out the size it always was.</summary>
    private const float WhipLength = 19f;
    private const float WhipWidth = 1.6f;
    private const float BallRadius = 2.4f;

    private static readonly Color Ink = new(0.102f, 0.086f, 0.149f);
    private static readonly Color BallColour = new(0.941f, 0.239f, 0.239f);

    private Texture2D? _body;
    private Texture2D? _trim;
    private Color _paint = Colors.White;
    private Vector2 _antennaMount;
    private bool _showAntenna = true;

    public override void _Ready()
    {
        // Thumbnails put one of these inside a Button, and a car that ate the click would make the
        // whole row dead.
        MouseFilter = MouseFilterEnum.Ignore;

        // A Control is not redrawn on resize by itself, and the stage car is anchored.
        Resized += QueueRedraw;
    }

    /// <summary>Whether the antenna is drawn at all — off for anything too small to read it.</summary>
    public bool ShowAntenna
    {
        get => _showAntenna;
        set { _showAntenna = value; QueueRedraw(); }
    }

    /// <param name="antennaMount">Where the whip's foot sits on this body, in art units.</param>
    public void SetCar(Texture2D body, Texture2D trim, Color paint, Vector2 antennaMount)
    {
        _body = body;
        _trim = trim;
        _paint = paint;
        _antennaMount = antennaMount;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_body is null || _trim is null)
            return;

        Rect2 art = FitRect(_body.GetSize());
        DrawTextureRect(_body, art, false, _paint);
        DrawTextureRect(_trim, art, false);

        if (!_showAntenna)
            return;

        float scale = art.Size.X / ArtWidth;
        Vector2 foot = art.Position + _antennaMount * scale;

        // Off a roof there is not always 19 units of sky left — a whip standing on the bubble's
        // crown would put its ball above the frame and off the stage. Shortened to fit, which is
        // what the artwork did back when the antenna was drawn into it.
        float length = Mathf.Min(WhipLength, _antennaMount.Y - BallRadius - 1.5f);
        Vector2 tip = foot - new Vector2(0, length * scale);
        DrawLine(foot, tip, Ink, WhipWidth * scale, true);
        DrawCircle(tip - new Vector2(0, scale), BallRadius * scale, BallColour, true, -1, true);
    }

    /// <summary>The art letterboxed into this control, aspect kept — what a TextureRect set to
    /// KeepAspectCentered would have done, but as a rectangle the antenna can be placed against.</summary>
    private Rect2 FitRect(Vector2 texture)
    {
        float scale = Mathf.Min(Size.X / texture.X, Size.Y / texture.Y);
        Vector2 drawn = texture * scale;
        return new Rect2(((Size - drawn) * 0.5f).Floor(), drawn);
    }
}
