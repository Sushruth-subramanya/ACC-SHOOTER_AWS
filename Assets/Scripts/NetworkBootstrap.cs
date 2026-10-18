using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

/// <summary>
/// Session layer. Lives on the NetworkManager object.
/// - Create Room / Join Room with a join code (Unity Multiplayer Services "Sessions" + Relay under the hood)
/// - Connection approval (max players, "room full")
/// - Dedicated server mode (-server / -port N / Dedicated Server build) using direct IP, for AWS
/// Replaces the old NetworkBootstrap (and the earlier standalone-Relay version).
/// </summary>
[DefaultExecutionOrder(-100)]
public class NetworkBootstrap : MonoBehaviour
{
    public static NetworkBootstrap Instance { get; private set; }

    [Header("Room")]
    public int maxPlayers = 10;

    [Header("Direct / dedicated server (AWS)")]
    public string listenAddress = "0.0.0.0";
    public ushort defaultPort = 7777;

    [Header("Debug")]
    public bool showDebugHud = true;

    public string RoomCode { get; private set; } = "";
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public bool SessionActive => sessionActive;

    public event Action<string> OnStatus;
    public event Action OnSessionStarted;          // local player is in a room
    public event Action<string> OnSessionEnded;    // reason text

    ISession session;
    bool sessionActive;
    float fps = 60f;

    NetworkManager NM => NetworkManager.Singleton;
    UnityTransport Utp => NetworkManager.Singleton.GetComponent<UnityTransport>();

    // ---------------------------------------------------------------- setup

    void Awake()
    {
        Instance = this;
        var nm = GetComponent<NetworkManager>();
        if (nm != null)
        {
            nm.NetworkConfig.ConnectionApproval = true;
            nm.ConnectionApprovalCallback = Approve;
        }
    }

    void Start()
    {
        NM.OnClientDisconnectCallback += HandleClientDisconnect;

        if (IsDedicatedServerMode())
            StartDedicated(ParsePort(defaultPort));
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f) fps = Mathf.Lerp(fps, 1f / dt, 0.05f);
    }

    void Approve(NetworkManager.ConnectionApprovalRequest req, NetworkManager.ConnectionApprovalResponse res)
    {
        bool full = NM.ConnectedClientsIds.Count >= maxPlayers;
        res.Approved = !full;
        res.CreatePlayerObject = !full;
        res.Pending = false;
        if (full) res.Reason = "Room is full.";
    }

    // ---------------------------------------------------------------- room code API

    public async Task<bool> CreateRoomAsync(int players)
    {
        if (Busy) return false;
        Busy = true;
        try
        {
            maxPlayers = Mathf.Clamp(players, 2, 10);
            await WaitForShutdown();
            SetStatus("Creating room...");
            if (!await TryServices()) return false;

            // MaxPlayers here counts everyone in the room, host included.
            var options = new SessionOptions
            {
                Name = "Room" + UnityEngine.Random.Range(1000, 9999),
                MaxPlayers = maxPlayers
            }.WithRelayNetwork();

            session = await MultiplayerService.Instance.CreateSessionAsync(options);

            // The Sessions SDK starts the NetworkManager as host for us. Wait until it is up.
            float t = 0f;
            while (!NM.IsHost && t < 10f)
            {
                await Task.Yield();
                t += Time.unscaledDeltaTime;
            }
            if (!NM.IsHost)
            {
                SetStatus("The room was created but the host failed to start.");
                await AbandonSession(true);
                return false;
            }

            RoomCode = session.Code;
            BeginSession();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[Room] Create failed: " + e);
            SetStatus("Could not create the room. See Console.");
            await AbandonSession(NM.IsHost);
            return false;
        }
        finally { Busy = false; }
    }

    public async Task<bool> JoinRoomAsync(string code)
    {
        if (Busy) return false;
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) { SetStatus("Enter a room code."); return false; }

        Busy = true;
        try
        {
            await WaitForShutdown();
            SetStatus("Joining room...");
            if (!await TryServices()) return false;

            try { session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code); }
            catch (Exception e)
            {
                Debug.LogWarning("[Room] Join rejected: " + e.Message);
                SetStatus("Could not join that room. Check the code, or the room may be full.");
                return false;
            }

            // The Sessions SDK starts the NetworkManager client for us. Wait until connected.
            float t = 0f;
            while (!NM.IsConnectedClient && t < 15f)
            {
                await Task.Yield();
                t += Time.unscaledDeltaTime;
            }

            if (!NM.IsConnectedClient)
            {
                string reason = NM.DisconnectReason;
                if (NM.IsListening) NM.Shutdown();
                await AbandonSession(false);
                SetStatus(string.IsNullOrEmpty(reason) ? "Could not connect to the room." : reason);
                return false;
            }

            RoomCode = code;
            BeginSession();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[Room] Join failed: " + e);
            SetStatus("Could not join the room. See Console.");
            await AbandonSession(false);
            return false;
        }
        finally { Busy = false; }
    }

    public void Leave()
    {
        bool was = sessionActive;
        bool wasHost = NM != null && NM.IsHost;
        sessionActive = false;
        RoomCode = "";
        if (NM != null && NM.IsListening) NM.Shutdown();
        _ = AbandonSession(wasHost);
        if (was) OnSessionEnded?.Invoke("You left the room.");
    }

    // ---------------------------------------------------------------- direct IP (dev / AWS)

    public void JoinDirect(string ip, ushort port)
    {
        Utp.SetConnectionData(ip, port);
        if (NM.StartClient()) BeginSession();
    }

    public void HostDirect(ushort port)
    {
        Utp.SetConnectionData("127.0.0.1", port, listenAddress);
        if (NM.StartHost()) BeginSession();
    }

    void StartDedicated(ushort port)
    {
        Application.targetFrameRate = 60;
        Utp.SetConnectionData("127.0.0.1", port, listenAddress);
        if (NM.StartServer())
            Debug.Log($"[Server] Dedicated server listening on {listenAddress}:{port} (max {maxPlayers})");
        else
            Debug.LogError("[Server] StartServer failed.");
    }

    public static bool IsDedicatedServerMode()
    {
#if UNITY_SERVER
        return true;
#else
        foreach (var a in Environment.GetCommandLineArgs())
            if (a == "-server") return true;
        return false;
#endif
    }

    static ushort ParsePort(ushort fallback)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-port" && ushort.TryParse(args[i + 1], out var p)) return p;
        return fallback;
    }

    // ---------------------------------------------------------------- internals

    void BeginSession()
    {
        if (sessionActive) return;
        sessionActive = true;
        OnSessionStarted?.Invoke();
    }

    void HandleClientDisconnect(ulong clientId)
    {
        // Only react when WE (a pure client) lost the connection.
        if (NM.IsServer || clientId != NM.LocalClientId || !sessionActive) return;
        sessionActive = false;
        RoomCode = "";
        _ = AbandonSession(false);
        string reason = NM.DisconnectReason;
        OnSessionEnded?.Invoke(string.IsNullOrEmpty(reason) ? "Disconnected from the room." : reason);
    }

    // Leaves (or, for the host, deletes) the backend session. Never throws.
    async Task AbandonSession(bool asHost)
    {
        var s = session;
        session = null;
        if (s == null) return;
        try
        {
            if (asHost) await s.AsHost().DeleteAsync();
            else await s.LeaveAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Room] Leaving session: " + e.Message);
        }
    }

    async Task WaitForShutdown()
    {
        while (NM.ShutdownInProgress) await Task.Yield();
    }

    async Task<bool> TryServices()
    {
        try
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                // Random profile = a separate anonymous account per running instance,
                // so two editor windows on one PC don't share one identity.
                var options = new InitializationOptions();
                options.SetProfile("p" + UnityEngine.Random.Range(100000, 999999));
                await UnityServices.InitializeAsync(options);
            }
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[Room] Unity Services failed: " + e);
            SetStatus("Online services unavailable. Is the Unity project linked? See Console.");
            return false;
        }
    }

    void SetStatus(string s)
    {
        Status = s;
        OnStatus?.Invoke(s);
    }

    void OnGUI()
    {
        if (!showDebugHud || !sessionActive || NM == null) return;
        string s = $"FPS {fps:0}";
        if (NM.IsClient && !NM.IsHost)
            s += $"   Ping {Utp.GetCurrentRtt(NetworkManager.ServerClientId)} ms";
        GUI.Label(new Rect(10, Screen.height - 30, 320, 24), s);
    }
}