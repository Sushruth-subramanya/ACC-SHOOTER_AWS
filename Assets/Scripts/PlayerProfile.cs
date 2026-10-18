using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Put on the Player prefab root. Username (saved locally, synced to everyone) + kills / deaths
/// (written by the server's MatchManager).
/// </summary>
public class PlayerProfile : NetworkBehaviour
{
    const string Key = "TDM_PlayerName";
    const int MaxLen = 16;

    public NetworkVariable<FixedString32Bytes> PlayerName = new NetworkVariable<FixedString32Bytes>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<int> Kills = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<int> Deaths = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public string DisplayName
    {
        get
        {
            string n = PlayerName.Value.ToString();
            return string.IsNullOrEmpty(n) ? "Player " + OwnerClientId : n;
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner) SubmitNameServerRpc(LoadName());
    }

    [ServerRpc]
    void SubmitNameServerRpc(string raw)
    {
        var fs = new FixedString32Bytes();
        fs.CopyFromTruncated(Clean(raw));
        PlayerName.Value = fs;
    }

    /// <summary>Server only.</summary>
    public void ServerResetStats()
    {
        if (!IsServer) return;
        Kills.Value = 0;
        Deaths.Value = 0;
    }

    // ---- static helpers used by the menu ----

    public static string LoadName()
    {
        string n = PlayerPrefs.GetString(Key, "");
        if (string.IsNullOrWhiteSpace(n))
        {
            n = "Player" + Random.Range(1000, 9999);
            PlayerPrefs.SetString(Key, n);
        }
        return n;
    }

    public static void SaveName(string n)
    {
        PlayerPrefs.SetString(Key, Clean(n));
        PlayerPrefs.Save();
    }

    public static string Clean(string n)
    {
        n = (n ?? "").Replace("<", "").Replace(">", "").Trim();
        if (n.Length > MaxLen) n = n.Substring(0, MaxLen);
        if (n.Length == 0) n = "Player" + Random.Range(1000, 9999);
        return n;
    }
}