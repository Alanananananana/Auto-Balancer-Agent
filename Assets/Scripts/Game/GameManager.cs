using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.MLAgents; // for StatsRecorder
using Unity.MLAgents.Policies; // for BehaviorParameters lookup

public class GameManager : MonoBehaviour
{
    public PlayerConfig player1;
    public PlayerConfig player2;

    public Transform p1Spawn;
    public Transform p2Spawn;

    public GameObject fighterPrefab;

    [Header("Respawn")]
    [Tooltip("Seconds to wait before respawning a dead fighter.")]
    public float respawnDelay = 0f;

    [Header("Fall-off")]
    [Tooltip("Y coordinate at or below which a fighter is considered to have fallen off and will be killed.")]
    public float fallDeathY = -10f;
    [Tooltip("Reward (negative) applied to an agent that falls off.")]
    public float fallPenalty = -1.0f;

    [Header("Training Randomization")]
    [Tooltip("Enable periodic slight randomization of player stats at runtime (does NOT persist assets unless you save them).")]
    public bool enableStatRandomization = true;
    [Tooltip("Number of completed matches between randomization events.")]
    public int randomizeEveryMatches = 100;

    [Tooltip("Initial max fractional jitter applied to each stat (e.g. 0.05 = ±5%).")]
    [Range(0.001f, 0.5f)]
    public float initialJitterFraction = 0.05f;

    [Tooltip("Minimum jitter fraction after decay; jitter will never go below this.")]
    [Range(0f, 0.05f)]
    public float minJitterFraction = 0.005f;

    [Tooltip("Amount jitterFraction is reduced by after each randomization event.")]
    [Range(0f, 0.02f)]
    public float jitterDecayPerRandomization = 0.005f;

    [Tooltip("If true, GameManager will create runtime copies of PlayerConfig.stats so original assets are not modified.")]
    public bool createRuntimeStatCopies = true;

    [Header("Runtime clamping")]
    [Tooltip("If true, per-class runtime clamps in FighterStats.Normalize(...) are applied. Turn OFF to allow free editing in the Inspector without runtime enforcement.")]
    public bool enforceRuntimeClamps = true;

    // Public static used by FighterStats.Normalize to honor the scene toggle.
    public static bool EnforceRuntimeClampsDefault = true;

    // Runtime-active jitter (initialized in Start)
    private float _runtimeJitter;

    // Runtime references to current instances and their spawn positions
    private GameObject _p1Instance;
    private GameObject _p2Instance;
    private Vector3 _p1SpawnPos;
    private Vector3 _p2SpawnPos;

    // Prevent re-entrant round end handling
    private bool _roundEnding;

    // Telemetry: match timing
    private Unity.MLAgents.StatsRecorder _statsRecorder;
    private float _roundStartTime;

    // Track which instances we've already applied a fall penalty to (avoid duplicates)
    private readonly HashSet<int> _fallPenalized = new HashSet<int>();

    // Track wins per side label ("P1", "P2") for clear TensorBoard series
    private readonly Dictionary<string, int> _winCounts = new Dictionary<string, int>();

    // Swap flag: when true, P1 spawns at p2Spawn and P2 at p1Spawn on next respawn
    private bool _swapSides = false;

    // Avoid reapplying the same randomization for the same match count
    private int _lastRandomizeMatchCount = -1;

    // Add near other private fields (runtime references)
    private FighterStats _runtimeP1Stats;
    private FighterStats _runtimeP2Stats;

    // Add this near other private fields (e.g. after _lastRandomizeMatchCount)
    private int _lastWinrateLoggedMatchCount = -1;

    void Start()
    {
        // Set global clamp toggle so FighterStats.Normalize honors the scene setting.
        EnforceRuntimeClampsDefault = enforceRuntimeClamps;

        _p1SpawnPos = p1Spawn.position;
        _p2SpawnPos = p2Spawn.position;

        // initialize runtime jitter
        _runtimeJitter = Mathf.Max(minJitterFraction, initialJitterFraction);

        // Optionally create runtime copies so asset files are not modified
        if (createRuntimeStatCopies) CreateRuntimeConfigCopies();

        // ensure both labels exist so TensorBoard shows two distinct series
        _winCounts["P1"] = 0;
        _winCounts["P2"] = 0;

        // initial spawn (no swap)
        _p1Instance = SpawnFighter(player1, _p1SpawnPos, true);
        _p2Instance = SpawnFighter(player2, _p2SpawnPos, false);

        // Wire AI targets if any (initial)
        WireAiTargets();

        // Hook centralized death handlers
        HookDeathHandlers();

        // Stats recorder if available
        if (Academy.Instance != null)
        {
            _statsRecorder = Academy.Instance.StatsRecorder;
        }

        // Start round timer
        _roundStartTime = Time.time;
    }

    void OnDestroy()
    {
        // Restore default to avoid surprising static state after scene teardown
        EnforceRuntimeClampsDefault = true;

        UnsubscribeInstances();
    }

    void Update()
    {
        // Kill fighters that fall below the configured Y threshold
        if (_p1Instance != null)
        {
            var hp1 = _p1Instance.GetComponent<Health>();
            var id1 = _p1Instance.GetInstanceID();
            if (hp1 != null && hp1.currentHp > 0f && !hp1.IsInvulnerable && !_fallPenalized.Contains(id1) && _p1Instance.transform.position.y <= fallDeathY)
            {
                // Penalize the agent for falling so trainer learns to avoid it
                var agent = _p1Instance.GetComponent<FighterAgent>();
                if (agent != null)
                {
                    agent.AddReward(fallPenalty);
                    if (_statsRecorder != null) _statsRecorder.Add("falls", 1);
                }

                // Mark as penalized so we don't re-apply for the same fall
                _fallPenalized.Add(id1);

                hp1.TakeHit(hp1.currentHp);
            }
        }

        if (_p2Instance != null)
        {
            var hp2 = _p2Instance.GetComponent<Health>();
            var id2 = _p2Instance.GetInstanceID();
            if (hp2 != null && hp2.currentHp > 0f && !hp2.IsInvulnerable && !_fallPenalized.Contains(id2) && _p2Instance.transform.position.y <= fallDeathY)
            {
                var agent = _p2Instance.GetComponent<FighterAgent>();
                if (agent != null)
                {
                    agent.AddReward(fallPenalty);
                    if (_statsRecorder != null) _statsRecorder.Add("falls", 1);
                }

                // Mark as penalized so we don't re-apply for the same fall
                _fallPenalized.Add(id2);

                hp2.TakeHit(hp2.currentHp);
            }
        }
    }

    // Centralized death hook wiring (called after spawn / respawn)
    private void HookDeathHandlers()
    {
        if (_p1Instance != null)
        {
            var hp1 = _p1Instance.GetComponent<Health>();
            if (hp1 != null)
            {
                hp1.OnDeath -= OnP1Death;
                hp1.OnDeath += OnP1Death;
            }
        }

        if (_p2Instance != null)
        {
            var hp2 = _p2Instance.GetComponent<Health>();
            if (hp2 != null)
            {
                hp2.OnDeath -= OnP2Death;
                hp2.OnDeath += OnP2Death;
            }
        }
    }

    // Named handlers to avoid anonymous delegate removal issues
    private void OnP1Death() => OnFighterDeath(true);
    private void OnP2Death() => OnFighterDeath(false);

    // Called when either fighter's Health.OnDeath fires
    private void OnFighterDeath(bool leftSideDied)
    {
        if (_roundEnding) return; // guard against double-calls
        _roundEnding = true;

        // Determine winner/loser GameObjects
        var dead = leftSideDied ? _p1Instance : _p2Instance;
        var winner = leftSideDied ? _p2Instance : _p1Instance;
        var loser = dead;

        string winnerName = winner != null ? winner.name : (leftSideDied ? "Player2" : "Player1");
        //Debug.Log($"Round result: Winner = {winnerName}", this);

        // Central match-length telemetry: record elapsed seconds once per round
        if (_statsRecorder != null)
        {
            float matchSeconds = Time.time - _roundStartTime;
            _statsRecorder.Add("match_length_seconds", matchSeconds);
        }

        // Award terminal rewards and EndEpisode on both agents (if they are Agent instances)
        var winnerAgent = winner != null ? winner.GetComponent<FighterAgent>() : null;
        var loserAgent = loser != null ? loser.GetComponent<FighterAgent>() : null;

        if (winnerAgent != null)
        {
            winnerAgent.AddReward(1.0f);
            winnerAgent.EndEpisode();
        }
        if (loserAgent != null)
        {
            loserAgent.AddReward(-1.0f);
            loserAgent.EndEpisode();
        }

        // --- record per-side wins so TensorBoard shows P1 vs P2 clearly ---
        string winnerLabel = (winner == _p1Instance) ? "P1" : (winner == _p2Instance) ? "P2" : GetBehaviorName(winnerAgent);
        string loserLabel = (loser == _p1Instance) ? "P1" : (loser == _p2Instance) ? "P2" : GetBehaviorName(loserAgent);

        if (!string.IsNullOrEmpty(winnerLabel))
        {
            if (!_winCounts.ContainsKey(winnerLabel)) _winCounts[winnerLabel] = 0;
            _winCounts[winnerLabel]++;

            if (_statsRecorder != null)
            {
                // record cumulative win count per side (TensorBoard will show two separate series)
                _statsRecorder.Add($"win_count/{winnerLabel}", _winCounts[winnerLabel]);
            }
        }

        if (!string.IsNullOrEmpty(loserLabel) && _statsRecorder != null)
        {
            _statsRecorder.Add($"loss_count/{loserLabel}", 1);
        }

        // Update and log win rates (per-side fraction of matches)
        UpdateWinMetrics();

        //Debug.Log($"[GameManager] Updated win counts: {winnerLabel} +1 (total={GetWins(winnerLabel)})");

        // Start respawn coroutine that respawns BOTH fighters for a clean state
        StartCoroutine(RespawnBothRoutine());
    }

    // Respawn both fighters together
    IEnumerator RespawnBothRoutine()
    {
        if (respawnDelay > 0f)
        {
            //Debug.Log($"Respawning both fighters in {respawnDelay:F2}s", this);
            yield return new WaitForSeconds(respawnDelay);
        }
        else
        {
            // Wait one frame to allow Unity to complete any pending destruction/creation and for Start() on new objects to run.
            // This minimizes extra wall-clock delay while ensuring correct initialization ordering.
            yield return null;
        }

        // Destroy existing instances if still present
        if (_p1Instance != null) Destroy(_p1Instance);
        if (_p2Instance != null) Destroy(_p2Instance);

        // Decide spawn positions, swap if flag set
        Vector3 p1Pos = _swapSides ? _p2SpawnPos : _p1SpawnPos;
        Vector3 p2Pos = _swapSides ? _p1SpawnPos : _p2SpawnPos;

        // --- Periodic training-time stat randomization ---
        int totalMatches = _winCounts.Values.Sum();

        if (enableStatRandomization && randomizeEveryMatches > 0 && totalMatches > 0 &&
            totalMatches % randomizeEveryMatches == 0 && _lastRandomizeMatchCount != totalMatches)
        {
            // Apply slight random jitter to both player configs (runtime only)
            RandomizeStatsForTraining();
            _lastRandomizeMatchCount = totalMatches;
        }

        // Spawn fresh instances and update runtime references
        _p1Instance = SpawnFighter(player1, p1Pos, true);
        _p2Instance = SpawnFighter(player2, p2Pos, false);

        // Re-wire targets and death handlers
        WireAiTargets();
        HookDeathHandlers();

        // Restart round timer
        _roundStartTime = Time.time;

        //Debug.Log($"Respawned fighters at {_p1SpawnPos} and {_p2SpawnPos} (swapped: {_swapSides})", this);

        // Clear fall-penalized tracking so new instances can be penalized again if they fall
        _fallPenalized.Clear();

        // Toggle swap flag so next round uses the opposite sides
        _swapSides = !_swapSides;

        _roundEnding = false;
    }

    // Ensures AIInput.target for both fighters (if present) point to the other fighter.
    private void WireAiTargets()
    {
        if (_p1Instance != null)
        {
            var ai1 = _p1Instance.GetComponentInChildren<AIInput>(true);
            if (ai1 != null && _p2Instance != null) ai1.target = _p2Instance.transform;
        }
        if (_p2Instance != null)
        {
            var ai2 = _p2Instance.GetComponentInChildren<AIInput>(true);
            if (ai2 != null && _p1Instance != null) ai2.target = _p1Instance.transform;
        }
    }

    GameObject SpawnFighter(PlayerConfig cfg, Vector3 pos, bool leftSide)
    {
        // Use per-player prefab if provided, otherwise fall back to the default GameManager.fighterPrefab
        var prefabToUse = cfg != null && cfg.fighterPrefab != null ? cfg.fighterPrefab : fighterPrefab;
        var go = Instantiate(prefabToUse, pos, Quaternion.identity);
        go.name = cfg.isAI ? (leftSide ? "P1_AI" : "P2_AI") : (leftSide ? "P1" : "P2");

        var fc = go.GetComponent<FighterController>();
        var hp = go.GetComponent<Health>();

        // Use runtime clone if requested, otherwise use the original asset stats.
        FighterStats statsToUse = cfg?.stats;
        if (createRuntimeStatCopies)
        {
            if (cfg == player1 && _runtimeP1Stats != null) statsToUse = _runtimeP1Stats;
            else if (cfg == player2 && _runtimeP2Stats != null) statsToUse = _runtimeP2Stats;
        }

        // Apply options overrides (OptionsManager.CreateStatsOverride expects a base stats object)
        if (OptionsManager.Instance != null)
        {
            statsToUse = OptionsManager.Instance.CreateStatsOverride(statsToUse, cfg.applyOptionsOverrides);
        }
        fc.stats = statsToUse;
        fc.weapon = cfg.weapon;

        // IMPORTANT: apply stats-derived attack modifiers (damage/cooldown) now that stats & weapon are assigned
        fc.ApplyStats();

        // Plug input source
        IFighterInput i; // to force compile
        if (cfg.isAI)
        {
            var agent = go.GetComponent<FighterAgent>() ?? go.AddComponent<FighterAgent>();
            fc.inputSource = agent; // FighterAgent.Initialize also sets this, but do it explicitly
        }
        else
        {
            var hi = go.AddComponent<HumanInput>();
            if (leftSide)
            {
                // Player 1: A/D for left/right, W to jump, Space to attack, LeftShift to block
                hi.left = KeyCode.A;
                hi.right = KeyCode.D;
                hi.jump = KeyCode.W;
                hi.attack = KeyCode.Space;
                hi.block = KeyCode.LeftShift;
            }
            else
            {
                // Player 2: Left/Right arrows, Up to jump, L to attack, K to block
                hi.left = KeyCode.LeftArrow;
                hi.right = KeyCode.RightArrow;
                hi.jump = KeyCode.UpArrow;
                hi.attack = KeyCode.L;
                hi.block = KeyCode.K;
            }
            fc.inputSource = hi;
            Debug.Log($"Spawned '{go.name}': inputSource='{fc.inputSource?.GetType().Name ?? "null"}' isAI={cfg.isAI}", go);
        }

        // Damage reduction while blocking: intercept Health.TakeHit via wrapper (simple example)
        var block = go.AddComponent<BlockState>();
        hp.OnDamaged += _ => { /* UI hook point */ };

        // Store runtime instance + spawn pos
        if (leftSide)
        {
            _p1Instance = go;
            _p1SpawnPos = pos;
        }
        else
        {
            _p2Instance = go;
            _p2SpawnPos = pos;
        }

        return go;
    }

    // Helper: tries to read a behavior name from the agent's BehaviorParameters; falls back to GameObject name.
    private string GetBehaviorName(FighterAgent agent)
    {
        if (agent == null) return "Unknown";
        var bp = agent.GetComponent<BehaviorParameters>();
        if (bp != null && !string.IsNullOrEmpty(bp.BehaviorName)) return bp.BehaviorName;
        return agent.gameObject.name;
    }

    // Public helpers to access runtime win counts / rates
    public int GetWins(string behavior)
    {
        return _winCounts.TryGetValue(behavior, out var v) ? v : 0;
    }

    public float GetWinRate(string behaviorA, string behaviorB)
    {
        int a = GetWins(behaviorA);
        int b = GetWins(behaviorB);
        int sum = a + b;
        if (sum == 0) return 0f;
        return (float)a / sum;
    }

    // Update TensorBoard scalars for win rates and counts
    private void UpdateWinMetrics()
    {
        if (_statsRecorder == null) return;

        int totalMatches = _winCounts.Values.Sum();
        if (totalMatches == 0) return;

        // Ensure both P1 and P2 keys are present for consistent plotting
        int p1Wins = _winCounts.ContainsKey("P1") ? _winCounts["P1"] : 0;
        int p2Wins = _winCounts.ContainsKey("P2") ? _winCounts["P2"] : 0;

        float p1Rate = (float)p1Wins / totalMatches;
        float p2Rate = (float)p2Wins / totalMatches;

        _statsRecorder.Add("win_rate/P1", p1Rate);
        _statsRecorder.Add("win_rate/P2", p2Rate);

        _statsRecorder.Add("win_count/P1", p1Wins);
        _statsRecorder.Add("win_count/P2", p2Wins);

        _statsRecorder.Add("matches/total", totalMatches);

        // Print a concise line to the Console every 500 matches (once per milestone)
        const int logInterval = 100;
        if (totalMatches % logInterval == 0 && totalMatches != 0 && _lastWinrateLoggedMatchCount != totalMatches)
        {
            Debug.Log($"[GameManager] Matches={totalMatches} P1 win rate={p1Rate:P2} P2 win rate={p2Rate:P2}");
            _lastWinrateLoggedMatchCount = totalMatches;
        }
    }

    // --- NEW: training-time stat randomization helpers ---
    private void RandomizeStatsForTraining()
    {
        Debug.Log($"[GameManager] Applying runtime stat randomization (jitter={_runtimeJitter:P1}) at match count {_winCounts.Values.Sum()}");

        RandomizeConfigStats(player1);
        RandomizeConfigStats(player2);

        // Decay jitter so randomization becomes smaller over time during Phase 2
        _runtimeJitter = Mathf.Max(minJitterFraction, _runtimeJitter - jitterDecayPerRandomization);
    }

    private void RandomizeConfigStats(PlayerConfig cfg)
    {
        if (cfg == null) return;

        // Choose the stats object to mutate (runtime clone if present, otherwise the asset)
        FighterStats s = cfg.stats;
        if (createRuntimeStatCopies)
        {
            if (cfg == player1 && _runtimeP1Stats != null) s = _runtimeP1Stats;
            else if (cfg == player2 && _runtimeP2Stats != null) s = _runtimeP2Stats;
        }
        if (s == null) return;

        bool heavy = s is HeavyFighterStats;

        // Helper: create multiplicative jitter in [1 - jitter, 1 + jitter]
        float JitterScale() => 1f + Random.Range(-_runtimeJitter, _runtimeJitter);

        float atkMin = 0.2f;
        float atkMax = heavy ? 2.0f : 1.5f;
        float kbMin = heavy ? 0.5f : 0.3f;
        float kbMax = heavy ? 2.0f : 1.5f;
        float spdMin = heavy ? 0.5f : 0.1f;
        float spdMax = heavy ? 1.0f : 3.0f;
        float hpMin = heavy ? 100f : 50f;
        float hpMax = heavy ? 800f : 500f;

        s.attackDamageMultiplier = Mathf.Clamp(s.attackDamageMultiplier * JitterScale(), atkMin, atkMax);
        s.knockbackDealtMultiplier = Mathf.Clamp(s.knockbackDealtMultiplier * JitterScale(), kbMin, kbMax);
        s.attackSpeed = Mathf.Clamp(s.attackSpeed * JitterScale(), spdMin, spdMax);
        s.maxHp = Mathf.Clamp(s.maxHp * JitterScale(), hpMin, hpMax);

        // Ensure minimal sanity remains
        s.Normalize();

        // Apply to currently spawned instance (if any) so effects are visible immediately for the next match
        if (cfg == player1 && _p1Instance != null)
        {
            var fc = _p1Instance.GetComponent<FighterController>();
            if (fc != null) { fc.stats = s; fc.ApplyStats(); }
            var hp = _p1Instance.GetComponent<Health>();
            if (hp != null) { hp.maxHp = s.maxHp; hp.HealFull(); }
        }
        else if (cfg == player2 && _p2Instance != null)
        {
            var fc = _p2Instance.GetComponent<FighterController>();
            if (fc != null) { fc.stats = s; fc.ApplyStats(); }
            var hp = _p2Instance.GetComponent<Health>();
            if (hp != null) { hp.maxHp = s.maxHp; hp.HealFull(); }
        }
    }

    // Create runtime ScriptableObject copies so runtime jitter does not persist to the original assets
    private void CreateRuntimeConfigCopies()
    {
        // Create in-memory clones and mark them DontSave so they cannot be persisted to the AssetDatabase.
        if (player1 != null && player1.stats != null)
        {
            _runtimeP1Stats = Instantiate(player1.stats);
            _runtimeP1Stats.name = player1.stats.name + "_runtime";
            _runtimeP1Stats.hideFlags = HideFlags.DontSave;
        }
        if (player2 != null && player2.stats != null)
        {
            _runtimeP2Stats = Instantiate(player2.stats);
            _runtimeP2Stats.name = player2.stats.name + "_runtime";
            _runtimeP2Stats.hideFlags = HideFlags.DontSave;
        }
    }

    // --- Utilities: normalization, file output, editor persistence ---
    float NormalizeStat(float val, float min, float max)
    {
        // map to [-1,1], protect against div by zero
        float denom = Mathf.Max(1e-6f, max - min);
        return Mathf.Clamp01((val - min) / denom) * 2f - 1f;
    }

    void AppendUpdatedStatsSnapshot(float p1Rate, bool isFinal, float fallFraction, int iteration)
    {
        try
        {
            string title = isFinal ? "GAME MANAGER FINAL SNAPSHOT" : $"GAME MANAGER ITER SNAPSHOT";
            string content = $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} - {title}\n";
            content += $"  iteration = {iteration}\n";
            content += $"  p1_rate = {p1Rate:F3}\n";
            content += $"  fall_death_fraction = {fallFraction:P1}\n";
            content += FormatStatsSnapshot("P1", player1) + "\n" + FormatStatsSnapshot("P2", player2) + "\n";
            content += new string('-', 60) + "\n";

            var outputPath = System.IO.Path.Combine(Application.persistentDataPath, "GameManagerStats.txt");
            System.IO.File.AppendAllText(outputPath, content);

            // Also log a concise line for quick inspection
            Debug.Log($"[GameManager][Iter {iteration}] p1_rate={p1Rate:F3} fall%={fallFraction:P1}");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[GameManager] Failed to append stats: " + ex);
        }
    }

    string FormatStatsSnapshot(string header, PlayerConfig cfg)
    {
        if (cfg == null) return $"{header}\n  <PlayerConfig null>\n";
        var s = cfg.stats;
        if (s == null) return $"{header}\n  <FighterStats null>\n";
        return $"{header}\n  attackDamageMultiplier = {s.attackDamageMultiplier:F3}\n  knockbackDealtMultiplier = {s.knockbackDealtMultiplier:F3}\n  attackSpeed = {s.attackSpeed:F3}\n  moveSpeed = {s.moveSpeed:F3}\n  maxHp = {s.maxHp:F3}";
    }

#if UNITY_EDITOR
    void TrySaveAsset(ScriptableObject so)
    {
        if (so == null) return;
        try
        {
            UnityEditor.EditorUtility.SetDirty(so);
            UnityEditor.AssetDatabase.SaveAssets();
            Debug.Log($"[AutoBalancerAgent] Persisted asset changes for '{so.name}'.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[AutoBalancerAgent] Could not persist asset changes: " + ex);
        }
    }
#endif



    private void UnsubscribeInstances()
    {
        if (_p1Instance != null)
        {
            var hp1 = _p1Instance.GetComponent<Health>();
            if (hp1 != null)
            {
                hp1.OnDeath -= OnP1Death;
            }
        }

        if (_p2Instance != null)
        {
            var hp2 = _p2Instance.GetComponent<Health>();
            if (hp2 != null)
            {
                hp2.OnDeath -= OnP2Death;
            }
        }
    }

    // Apply derived stats immediately to a live FighterController instance if present for the given PlayerConfig.
    void ApplyInstanceStatsIfPresent(PlayerConfig cfg)
    {
        if (cfg == null) return;

        if (cfg == player1 && _p1Instance != null)
        {
            var fc = _p1Instance.GetComponent<FighterController>();
            if (fc != null)
            {
                // Ensure the live instance uses the updated PlayerConfig.stats reference so multipliers (knockback etc.) take effect immediately.
                fc.stats = cfg.stats;
                fc.ApplyStats();
            }
        }
        else if (cfg == player2 && _p2Instance != null)
        {
            var fc = _p2Instance.GetComponent<FighterController>();
            if (fc != null)
            {
                fc.stats = cfg.stats;
                fc.ApplyStats();
            }
        }
    }
}

// BlockState unchanged...
public class BlockState : MonoBehaviour
{
    public FighterController fighter;
    public Health health;
    void Awake()
    {
        fighter = GetComponent<FighterController>();
        health = GetComponent<Health>();
    }

    // Placeholder for centralized damage reduction / block handling if you want to migrate logic later.
    public void ApplyDamage(float dmg)
    {
        if (fighter && fighter.enabled)
        {
            // Intentionally left blank: hitbox currently handles block reduction.
        }
    }
}
