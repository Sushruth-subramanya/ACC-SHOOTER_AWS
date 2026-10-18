using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player root. Server-authoritative hitscan rifle (NEW INPUT SYSTEM).
///  - Fire rate in rounds-per-minute, server enforces it
///  - Magazine + reserve ammo + timed reload (R key or "Player/Reload" action)
///  - Camera recoil (kick + recovery) and spread bloom
///  - Tracer / impact VFX broadcast to everyone
/// Right-click the component header (3 dots) > "Preset: AKM / M416" to load stats.
/// </summary>
public class WeaponController : NetworkBehaviour
{
    // ---------------------------------------------------------------- INPUT
    [Header("Input (New Input System)")]
    [SerializeField] private InputActionAsset inputActions;       // same asset PlayerController uses
    public string attackActionPath = "Player/Attack";
    public string reloadActionPath = "Player/Reload";             // optional - falls back to reloadKey if the action doesn't exist
    public Key reloadKey = Key.R;
    public bool automatic = true;                                 // hold to spray
    public bool requireLockedCursor = false;

    // ----------------------------------------------------------- REFERENCES
    [Header("References")]
    [SerializeField] private Transform muzzlePoint;               // first-person barrel tip (on assault1)
    [SerializeField] private Transform worldMuzzlePoint;          // barrel tip on the hand-held gun (seen by others)
    [SerializeField] private Camera playerCamera;
    [SerializeField] private MuzzleFlashController muzzleFlash;
    [SerializeField] private LineRenderer tracerPrefab;           // do NOT put TracerFade on this prefab
    [SerializeField] private ParticleSystem impactVFXPrefab;
    [SerializeField] private GameObject scorchDecalPrefab;

    // ------------------------------------------------------------ FIRE RATE
    [Header("Fire Rate & Damage")]
    [Tooltip("Rounds per minute. AKM ~600, M416 ~700-750, SCAR ~600.")]
    public float roundsPerMinute = 600f;
    [Tooltip("Damage per bullet. 100 HP players: 30 = 4 shots to kill.")]
    public float damage = 30f;
    public float range = 150f;
    public LayerMask hitMask = ~0;
    [Tooltip("OFF = bullets do no damage to players on your own team.")]
    public bool friendlyFire = false;
    [Tooltip("Server accepts a shot if at least this fraction of the fire interval has passed (absorbs network jitter).")]
    [Range(0.5f, 1f)] public float serverFireRateTolerance = 0.8f;

    // ----------------------------------------------------------------- AMMO
    [Header("Ammo & Reload")]
    public int magazineSize = 30;
    public int startingReserveAmmo = 120;                         // 4 spare mags
    [Tooltip("Reload with bullets still in the mag (seconds).")]
    public float reloadTime = 2.5f;
    [Tooltip("Reload with an empty mag (seconds) - a bit longer.")]
    public float emptyReloadTime = 3.0f;
    public bool autoReloadOnEmpty = true;

    // --------------------------------------------------------------- RECOIL
    [Header("Recoil (degrees)")]
    [Tooltip("Camera kick UP per shot.")]
    public float verticalRecoil = 0.9f;
    [Tooltip("Random left/right kick per shot (+/-).")]
    public float horizontalRecoil = 0.45f;
    public float maxVerticalRecoil = 12f;
    public float maxHorizontalRecoil = 5f;
    [Tooltip("How fast the camera follows the kick. Higher = snappier.")]
    public float recoilSnappiness = 25f;
    [Tooltip("Seconds after the last shot before the camera starts settling back.")]
    public float recoilRecoveryDelay = 0.12f;
    [Tooltip("Degrees per second the camera settles back.")]
    public float recoilRecoverySpeed = 10f;

    [Header("Spread / Bloom (degrees)")]
    public float baseSpread = 0.1f;
    public float spreadPerShot = 0.06f;
    public float maxSpread = 1.5f;
    public float spreadRecoverySpeed = 3f;

    [Header("Debug")]
    public bool debugLogs = true;
    public float maxOriginOffset = 3f;                            // server rejects shots from further than this from the player
    [Tooltip("Display only - editing does nothing.")]
    [SerializeField] private int debugAmmoInMag;
    [SerializeField] private int debugReserveAmmo;
    [SerializeField] private bool debugIsReloading;
    [SerializeField] private float debugCurrentSpread;

    // --------------------------------------------------------------- STATE
    public NetworkVariable<int> AmmoInMag = new NetworkVariable<int>(30,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> ReserveAmmo = new NetworkVariable<int>(120,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<bool> IsReloading = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>(ammoInMag, reserve) - hook your ammo UI here.</summary>
    public event Action<int, int> OnAmmoChanged;
    public event Action OnDryFire;
    public float FireInterval => 60f / Mathf.Max(1f, roundsPerMinute);
    public float CurrentSpread => baseSpread + _bloom;

    private InputAction _attack, _reload;
    private Health _ownHealth;
    private PlayerTeam _ownTeam;

    // owner-side
    private float _nextFireTime;
    private float _lastShotTime = -999f;
    private float _reloadGuardUntil;
    private int _predictedAmmo;
    private Vector2 _recoilTarget, _recoilCurrent;   // x = pitch up, y = yaw
    private float _bloom;
    private Quaternion _camBaseRot = Quaternion.identity;

    // server-side
    private float _serverLastShotTime = -999f;
    private float _reloadEndTime;

    // ------------------------------------------------------------ LIFECYCLE
    public override void OnNetworkSpawn()
    {
        _ownHealth = GetComponent<Health>();
        _ownTeam = GetComponent<PlayerTeam>();
        AmmoInMag.OnValueChanged += HandleAmmoChanged;
        ReserveAmmo.OnValueChanged += HandleReserveChanged;

        if (IsServer) RefillAmmo();

        if (!IsOwner) return;

        _predictedAmmo = AmmoInMag.Value;
        if (playerCamera != null) _camBaseRot = playerCamera.transform.localRotation;

        if (inputActions != null)
        {
            _attack = inputActions.FindAction(attackActionPath, false);
            _reload = inputActions.FindAction(reloadActionPath, false);
            _attack?.Enable();
            _reload?.Enable();
            if (_attack == null) Debug.LogWarning($"[Weapon] Action '{attackActionPath}' not found - using Mouse left button.");
            if (_reload == null) Debug.Log($"[Weapon] Action '{reloadActionPath}' not found - using key {reloadKey}.");
        }
        else Debug.LogWarning("[Weapon] No InputActionAsset assigned - using Mouse left button + reload key.");
    }

    public override void OnNetworkDespawn()
    {
        AmmoInMag.OnValueChanged -= HandleAmmoChanged;
        ReserveAmmo.OnValueChanged -= HandleReserveChanged;
        if (IsOwner && playerCamera != null) playerCamera.transform.localRotation = _camBaseRot;
    }

    private void Update()
    {
        if (!IsSpawned) return;

        if (IsServer) ServerReloadTick();

        debugAmmoInMag = AmmoInMag.Value;
        debugReserveAmmo = ReserveAmmo.Value;
        debugIsReloading = IsReloading.Value;
        debugCurrentSpread = CurrentSpread;
    }

    // Camera recoil + input run in LateUpdate so the shot direction is exactly what the player is looking at.
    private void LateUpdate()
    {
        if (!IsOwner || !IsSpawned || playerCamera == null) return;

        float dt = Time.deltaTime;
        UpdateRecoil(dt);
        _bloom = Mathf.MoveTowards(_bloom, 0f, spreadRecoverySpeed * dt);

        // Once shots have settled, trust the server's ammo count again.
        if (Time.time - _lastShotTime > 0.35f) _predictedAmmo = AmmoInMag.Value;

        HandleInput();
    }

    // -------------------------------------------------------------- INPUT
    private void HandleInput()
    {
        if (requireLockedCursor && Cursor.lockState != CursorLockMode.Locked) return;
        if (_ownHealth != null && _ownHealth.IsDead) return;

        if (ReloadPressed()) TryRequestReload();

        if (!FirePressed()) return;
        if (IsReloading.Value || Time.time < _reloadGuardUntil) return;
        if (Time.time < _nextFireTime) return;

        if (_predictedAmmo <= 0)
        {
            if (autoReloadOnEmpty) TryRequestReload();
            else OnDryFire?.Invoke();
            return;
        }

        Fire();
    }

    private bool FirePressed()
    {
        if (_attack != null) return automatic ? _attack.IsPressed() : _attack.WasPressedThisFrame();
        if (Mouse.current == null) return false;
        return automatic ? Mouse.current.leftButton.isPressed : Mouse.current.leftButton.wasPressedThisFrame;
    }

    private bool ReloadPressed()
    {
        if (_reload != null) return _reload.WasPressedThisFrame();
        return Keyboard.current != null && Keyboard.current[reloadKey].wasPressedThisFrame;
    }

    // --------------------------------------------------------------- FIRING
    private void Fire()
    {
        float interval = FireInterval;
        // Keep a steady cadence even though frames don't line up with the fire interval.
        _nextFireTime = (Time.time - _nextFireTime > interval) ? Time.time + interval : _nextFireTime + interval;
        _lastShotTime = Time.time;
        _predictedAmmo--;

        if (muzzleFlash != null) muzzleFlash.Play();

        // Direction = what the crosshair points at right now (includes current recoil), plus spread bloom.
        Vector2 r = UnityEngine.Random.insideUnitCircle * CurrentSpread;
        Vector3 direction = playerCamera.transform.rotation * Quaternion.Euler(r.y, r.x, 0f) * Vector3.forward;
        Vector3 origin = playerCamera.transform.position;

        ApplyRecoilKick();
        _bloom = Mathf.Min(_bloom + spreadPerShot, maxSpread - baseSpread);

        FireServerRpc(origin, direction);
    }

    private void ApplyRecoilKick()
    {
        _recoilTarget.x = Mathf.Min(_recoilTarget.x + verticalRecoil, maxVerticalRecoil);
        _recoilTarget.y = Mathf.Clamp(
            _recoilTarget.y + UnityEngine.Random.Range(-horizontalRecoil, horizontalRecoil),
            -maxHorizontalRecoil, maxHorizontalRecoil);
    }

    private void UpdateRecoil(float dt)
    {
        if (Time.time - _lastShotTime > recoilRecoveryDelay)
            _recoilTarget = Vector2.MoveTowards(_recoilTarget, Vector2.zero, recoilRecoverySpeed * dt);

        _recoilCurrent = Vector2.Lerp(_recoilCurrent, _recoilTarget, 1f - Mathf.Exp(-recoilSnappiness * dt));

        // Applied to the Camera child (not CameraRoot), so PlayerController's look pitch is never disturbed.
        playerCamera.transform.localRotation = _camBaseRot * Quaternion.Euler(-_recoilCurrent.x, _recoilCurrent.y, 0f);
    }

    // --------------------------------------------------------------- RELOAD
    private void TryRequestReload()
    {
        if (IsReloading.Value || Time.time < _reloadGuardUntil) return;
        if (_predictedAmmo >= magazineSize || ReserveAmmo.Value <= 0) return;

        _reloadGuardUntil = Time.time + 0.3f;   // blocks firing until the server confirms (covers network delay)
        ReloadServerRpc();
    }

    [ServerRpc]
    private void ReloadServerRpc()
    {
        if (IsReloading.Value) return;
        if (AmmoInMag.Value >= magazineSize || ReserveAmmo.Value <= 0) return;
        if (_ownHealth != null && _ownHealth.IsDead) return;

        _reloadEndTime = Time.time + (AmmoInMag.Value == 0 ? emptyReloadTime : reloadTime);
        IsReloading.Value = true;
        if (debugLogs) Debug.Log($"[Weapon] Client {OwnerClientId} reloading ({AmmoInMag.Value}/{ReserveAmmo.Value})");
    }

    private void ServerReloadTick()
    {
        if (!IsReloading.Value) return;

        if (_ownHealth != null && _ownHealth.IsDead) { IsReloading.Value = false; return; }
        if (Time.time < _reloadEndTime) return;

        int take = Mathf.Min(magazineSize - AmmoInMag.Value, ReserveAmmo.Value);
        AmmoInMag.Value += take;
        ReserveAmmo.Value -= take;
        IsReloading.Value = false;
    }

    /// <summary>Server only. Call on respawn or from an ammo pickup.</summary>
    public void RefillAmmo()
    {
        if (!IsServer) return;
        AmmoInMag.Value = magazineSize;
        ReserveAmmo.Value = startingReserveAmmo;
        IsReloading.Value = false;
    }

    private void HandleAmmoChanged(int previous, int current)
    {
        if (IsOwner && current > previous) _predictedAmmo = current;   // reload finished / refill
        OnAmmoChanged?.Invoke(current, ReserveAmmo.Value);
    }

    private void HandleReserveChanged(int previous, int current)
    {
        OnAmmoChanged?.Invoke(AmmoInMag.Value, current);
    }

    // ------------------------------------------------------- SERVER: HITSCAN
    [ServerRpc]
    private void FireServerRpc(Vector3 origin, Vector3 direction)
    {
        direction.Normalize();

        if ((origin - transform.position).sqrMagnitude > maxOriginOffset * maxOriginOffset)
        {
            Debug.LogWarning($"[Weapon] Rejected shot from client {OwnerClientId}: origin too far from player.");
            return;
        }
        if (_ownHealth != null && _ownHealth.IsDead) return;
        if (IsReloading.Value || AmmoInMag.Value <= 0) return;
        if (Time.time < _serverLastShotTime + FireInterval * serverFireRateTolerance) return;

        _serverLastShotTime = Time.time;
        AmmoInMag.Value--;

        Vector3 endPoint = origin + direction * range;
        Vector3 hitNormal = -direction;
        bool didHit = false;

        if (Physics.Raycast(origin, direction, out RaycastHit hit, range, hitMask, QueryTriggerInteraction.Ignore))
        {
            endPoint = hit.point;
            hitNormal = hit.normal;
            didHit = true;

            Health health = hit.collider.GetComponentInParent<Health>();
            if (health != null && health != _ownHealth)
            {
                PlayerTeam targetTeam = health.GetComponent<PlayerTeam>();
                bool sameTeam = _ownTeam != null && targetTeam != null
                                && targetTeam.Team.Value == _ownTeam.Team.Value;

                if (sameTeam && !friendlyFire)
                {
                    if (debugLogs) Debug.Log("[Weapon] Teammate hit - no damage (friendly fire off).");
                }
                else
                {
                    health.TakeDamage(damage, OwnerClientId);
                }
            }

            if (debugLogs)
                Debug.Log($"[Weapon] Client {OwnerClientId} hit '{hit.collider.name}' (Health: {health != null}) ammo left {AmmoInMag.Value}");
        }

        Debug.DrawLine(origin, endPoint, didHit ? Color.red : Color.yellow, 1f);
        FireClientRpc(origin, endPoint, didHit, hitNormal);
    }

    [ClientRpc]
    private void FireClientRpc(Vector3 origin, Vector3 endPoint, bool didHit, Vector3 hitNormal)
    {
        Vector3 start = origin;
        if (IsOwner && muzzlePoint != null) start = muzzlePoint.position;
        else if (!IsOwner && worldMuzzlePoint != null) start = worldMuzzlePoint.position;

        SpawnTracer(start, endPoint);
        if (didHit) SpawnImpactVFX(endPoint, hitNormal);
    }

    private void SpawnTracer(Vector3 start, Vector3 end)
    {
        if (tracerPrefab == null) return;

        LineRenderer tracer = Instantiate(tracerPrefab, Vector3.zero, Quaternion.identity);
        tracer.positionCount = 2;
        tracer.SetPosition(0, start);
        tracer.SetPosition(1, end);

        var runner = tracer.GetComponent<TracerFade>();
        if (runner == null) runner = tracer.gameObject.AddComponent<TracerFade>();
        runner.Init(tracer, 0.05f);
    }

    private void SpawnImpactVFX(Vector3 point, Vector3 normal)
    {
        Quaternion rot = Quaternion.LookRotation(normal);

        if (impactVFXPrefab != null)
        {
            ParticleSystem vfx = Instantiate(impactVFXPrefab, point, rot);
            Destroy(vfx.gameObject, 3f);
        }

        if (scorchDecalPrefab != null)
        {
            Quaternion spin = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), normal);
            GameObject decal = Instantiate(scorchDecalPrefab, point + normal * 0.01f, spin * rot);
            Destroy(decal, 8f);
        }
    }

    // -------------------------------------------------------------- PRESETS
    [ContextMenu("Preset: AKM (600 RPM, hard-hitting, strong kick)")]
    private void PresetAKM()
    {
        roundsPerMinute = 600f; damage = 30f; magazineSize = 30; startingReserveAmmo = 120;
        reloadTime = 2.5f; emptyReloadTime = 3.0f;
        verticalRecoil = 0.9f; horizontalRecoil = 0.45f; spreadPerShot = 0.06f;
    }

    [ContextMenu("Preset: M416 (720 RPM, faster, controllable)")]
    private void PresetM416()
    {
        roundsPerMinute = 720f; damage = 26f; magazineSize = 30; startingReserveAmmo = 120;
        reloadTime = 2.2f; emptyReloadTime = 2.7f;
        verticalRecoil = 0.6f; horizontalRecoil = 0.3f; spreadPerShot = 0.04f;
    }
}