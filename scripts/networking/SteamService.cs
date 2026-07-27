using Godot;
using Steamworks;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MasterTrack.Networking;

/// <summary>
/// Autoload. Owns the Steam client's lifetime and nothing else: bring it up, pump its callbacks,
/// shut it down. No transport code lives here — that is <see cref="NetworkManager"/>'s job — and
/// deliberately so, because Steam failing to initialise and a peer failing to connect are two
/// completely different problems and debugging them together is miserable.
///
/// <b>Failing to initialise is not fatal.</b> Steam may be shut, or the player may be on the
/// Tailscale path instead. The game carries on over ENet exactly as before; only the Steam
/// transport becomes unavailable.
/// </summary>
public partial class SteamService : Node
{
    /// <summary>
    /// Valve's Spacewar. The public app id every Steamworks developer tests against, which is
    /// what makes this work with no partner account. It is shared with everyone else testing
    /// right now, so anything discoverable through it is discoverable by strangers — see the
    /// note about lobby browsers in <c>next_steps.md</c>.
    /// </summary>
    public const uint SpacewarAppId = 480;

    /// <summary>
    /// How long to give the relay network to warm up before a connection should be attempted.
    /// Connecting immediately after startup fails in a way that looks exactly like a bug in your
    /// own code, which is the single most expensive way to lose an afternoon to this.
    /// </summary>
    public const double RelayWarmupSeconds = 3.0;

    public static SteamService Instance { get; private set; } = null!;

    /// <summary>Whether Steam came up. False means the Steam transport cannot be used.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>This machine's Steam id — what a friend types in to join you.</summary>
    public SteamId LocalSteamId { get; private set; }

    /// <summary>This machine's Steam persona name, or empty if Steam never came up.</summary>
    public string LocalName { get; private set; } = "";

    /// <summary>Why Steam is unavailable, for showing on the menu rather than only in a log.</summary>
    public string UnavailableReason { get; private set; } = "";

    private double _upFor;

    /// <summary>
    /// Teach the runtime where <c>steam_api64.dll</c> is.
    ///
    /// Godot builds game assemblies with <c>EnableDynamicLoading</c>, which loads them into their
    /// own <c>AssemblyLoadContext</c>. Native lookups from such a context go through the
    /// assembly's <c>deps.json</c> rather than probing the folder the assembly sits in — and a
    /// file copied in by the build is not described there, so it is invisible no matter how
    /// correctly it is placed. The symptom is a DllNotFoundException naming a library you can see
    /// sitting right next to the binary.
    ///
    /// Registered against Facepunch's assembly because that is where the <c>DllImport</c> lives;
    /// a resolver on ours would never be consulted.
    /// </summary>
    static SteamService()
    {
        NativeLibrary.SetDllImportResolver(typeof(SteamClient).Assembly, ResolveSteamApi);
    }

    private static nint ResolveSteamApi(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "steam_api64")
            return nint.Zero;

        // BaseDirectory first, and it is the one that actually works: Godot loads game assemblies
        // from memory so it can hot-reload them, which leaves Assembly.Location an empty string.
        // Location is kept only as a fallback in case that ever stops being true.
        foreach (string? dir in new[] { System.AppContext.BaseDirectory,
                                        Path.GetDirectoryName(assembly.Location) })
        {
            if (string.IsNullOrEmpty(dir))
                continue;

            if (NativeLibrary.TryLoad(Path.Combine(dir, "steam_api64.dll"), out nint handle))
                return handle;
        }

        // Zero hands it back to the default resolver rather than asserting failure.
        return nint.Zero;
    }

    /// <summary>
    /// Whether the relay has had long enough to be worth trying. Not a guarantee — there is no
    /// synchronous way to ask — but it is the difference between "usually works" and "usually
    /// fails the first time".
    /// </summary>
    public bool RelayReady => IsAvailable && _upFor >= RelayWarmupSeconds;

    public override void _Ready()
    {
        Instance = this;

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.Equals("--no-steam", System.StringComparison.OrdinalIgnoreCase))
            {
                UnavailableReason = "disabled with --no-steam";
                GD.Print("[Steam] Skipped: --no-steam.");
                return;
            }
        }

        Start();
    }

    private void Start()
    {
        try
        {
            // asyncCallbacks: false so callbacks are pumped from _Process below, on the main
            // thread. Left true, Facepunch runs them from its own thread and anything they touch
            // becomes a threading problem inside the engine.
            SteamClient.Init(SpacewarAppId, asyncCallbacks: false);
        }
        catch (System.Exception e)
        {
            // Overwhelmingly this is "Steam is not running". Not an error: the ENet path over
            // Tailscale is still there and still works.
            UnavailableReason = e.Message;
            GD.PushWarning($"[Steam] Unavailable, carrying on without it: {e.Message}");
            return;
        }

        if (!SteamClient.IsValid)
        {
            UnavailableReason = "SteamClient reported itself invalid after init";
            GD.PushWarning($"[Steam] {UnavailableReason}.");
            return;
        }

        IsAvailable = true;
        LocalSteamId = SteamClient.SteamId;
        LocalName = SteamClient.Name;

        // Asked for as early as possible: it is what has to be warm before the first connection,
        // and the wait is wall-clock, so starting it now is free.
        SteamNetworkingUtils.InitRelayNetworkAccess();

        GD.Print($"[Steam] Ready as '{LocalName}' ({LocalSteamId.Value}) on app {SpacewarAppId}.");
    }

    public override void _Process(double delta)
    {
        if (!IsAvailable)
            return;

        _upFor += delta;
        SteamClient.RunCallbacks();
    }

    public override void _ExitTree()
    {
        if (!IsAvailable)
            return;

        SteamClient.Shutdown();
        IsAvailable = false;
    }
}
