using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// AutoBalancer: runs AI vs AI matches in Play mode to measure win rates and death causes,
/// then applies conservative stat tweaks to bring win-rate close to target (default 50%).
/// Attach to a scene object, assign GameManager + both PlayerConfig assets for P1/P2.
/// </summary>
public class AutoBalancer : MonoBehaviour
{
    [Header("Required")]
    public GameManager gameManager;
    public PlayerConfig player1Config; // corresponds to P1
    public PlayerConfig player2Config; // corresponds to P2

    [Header("Batch / Goal")]
    [Tooltip("Number of matches to gather before each balancing decision.")]
    public int matchesPerBatch = 30;
    [Tooltip("Target P1 win rate (0..1).")]
    public float targetWinRate = 0.5f;
    [Tooltip("Acceptable margin (±). If P1 rate is within target ± margin, balancing stops.")]
    public float margin = 0.05f;
    [Tooltip("Maximum number of balancing iterations (batches).")]
    public int maxIterations = 20;

    [Header("Adjustment")]
    [Tooltip("Fractional change applied per adjustment step (e.g. 0.05 = 5%).")]
    [Range(0.01f, 0.25f)]
    public float adjustStep = 0.05f;

    [Tooltip("If true, changes to ScriptableObject stats will be persisted to assets (Editor only).")]
    public bool persistChangesToAssets = false;

    [Header("Behaviour")]
    [Tooltip("If true, balancing starts automatically on Play.")]
    public bool autoStart = false;

    [Header("Output")]
    [Tooltip("Filename written to Application.persistentDataPath (example: AutoBalanceStats.txt).")]
    public string outputFileName = "AutoBalanceStats.txt";
    [Tooltip("If true, append an updated-stats snapshot after each balancing iteration.")]
    public bool appendPerIteration = true;

    // Internal state for monitoring
    private bool _running;
    private Coroutine _worker;
    private string _outputPath;

    // Runtime subscriptions per-instance
    private Health _p1Health;
    private Health _p2Health;
    private int _p1HitsThisMatch;
    private int _p2HitsThisMatch;
    private int _p1FallDeathsInBatch;
    private int _p2FallDeathsInBatch;
    private int _p1HpDeathsInBatch;
    private int _p2HpDeathsInBatch;

    // Saved default snapshots (string) so we write original values only once
    private string _defaultP1Snapshot;
    private string _defaultP2Snapshot;

    void OnEnable()
    {
        if (autoStart) StartBalancing();
    }

    void OnDisable()
    {
        StopBalancing();
    }

    public void StartBalancing()
    {
        if (_running) return;
        if (gameManager == null || player1Config == null || player2Config == null)
        {
            Debug.LogWarning("[AutoBalancer] Missing references. Assign GameManager and both PlayerConfig assets.");
            return;
        }

        // Prepare output path
        _outputPath = Path.Combine(Application.persistentDataPath, outputFileName);

        // Capture and write default stats snapshot before any adjustments
        SaveDefaultStatsSnapshot();

        _running = true;
        _worker = StartCoroutine(BalancerRoutine());
        Debug.Log("[AutoBalancer] Started balancing.");
    }

    public void StopBalancing()
    {
        if (!_running) return;
        _running = false;
        if (_worker != null) StopCoroutine(_worker);
        UnsubscribeInstances();
        Debug.Log("[AutoBalancer] Stopped balancing.");
    }

    IEnumerator BalancerRoutine()
    {
        int iteration = 0;

        // baseline wins (so we can measure per-batch increments)
        int baselineP1Wins = gameManager.GetWins("P1");
        int baselineP2Wins = gameManager.GetWins("P2");

        while (_running && iteration < maxIterations)
        {
            iteration++;

            // Reset batch stats
            _p1FallDeathsInBatch = _p2FallDeathsInBatch = 0;
            _p1HpDeathsInBatch = _p2HpDeathsInBatch = 0;
            int batchWinsP1 = 0;
            int batchWinsP2 = 0;

            // Ensure we're subscribed to the current instances (spawns can change each round)
            SubscribeInstancesSafe();

            // Run matchesPerBatch matches
            for (int m = 0; m < matchesPerBatch && _running; m++)
            {
                // Wait for the next round to complete: poll GameManager win counts increasing
                int waitStartP1 = gameManager.GetWins("P1");
                int waitStartP2 = gameManager.GetWins("P2");

                // Wait until one of the win counters increments
                yield return StartCoroutine(WaitForMatchEnd(waitStartP1, waitStartP2));

                // After match end, discover which side gained a win
                int nowP1 = gameManager.GetWins("P1");
                int nowP2 = gameManager.GetWins("P2");

                if (nowP1 > waitStartP1)
                {
                    batchWinsP1++;
                }
                else if (nowP2 > waitStartP2)
                {
                    batchWinsP2++;
                }
                else
                {
                    // Unexpected: no change, but continue
                    Debug.LogWarning("[AutoBalancer] Match end detected but no win increment found.");
                }

                // Aggregate deaths recorded by our Health.OnDeath handlers (they increment per-match counts into batch totals)
                // Note: handlers increment fall vs hp counters directly.
                // Clear per-match counters (hits) for next match
                _p1HitsThisMatch = 0;
                _p2HitsThisMatch = 0;

                // Ensure we re-subscribe in case GameManager respawned new instances
                SubscribeInstancesSafe();
            }

            // Compute batch win rate for P1
            int totalMatches = batchWinsP1 + batchWinsP2;
            if (totalMatches == 0)
            {
                Debug.LogWarning("[AutoBalancer] No matches recorded in batch; aborting.");
                break;
            }

            float batchP1Rate = (float)batchWinsP1 / totalMatches;
            Debug.Log($"[AutoBalancer] Iter {iteration}: P1 wins={batchWinsP1}, P2 wins={batchWinsP2}, P1 rate={batchP1Rate:P1}");

            // If within margin, done
            if (Mathf.Abs(batchP1Rate - targetWinRate) <= margin)
            {
                Debug.Log($"[AutoBalancer] Win-rate within margin ({margin:P}). Stopping. P1 rate={batchP1Rate:P1}");
                // Append a final updated snapshot
                AppendUpdatedStatsSnapshot(iteration, true);
                break;
            }

            // Decide adjustments by examining common causes
            // Determine which side is underperforming (losing more)
            bool p1Losing = batchP1Rate < targetWinRate;

            if (p1Losing)
            {
                // If P1 lost a lot, examine how P1 died
                int p1Falls = _p1FallDeathsInBatch;
                int p1HpDies = _p1HpDeathsInBatch;
                int p1Deaths = p1Falls + p1HpDies;
                if (p1Deaths == 0) p1Deaths = 1; // avoid divide by zero

                // If majority by fall -> knockback problem
                if (p1Falls >= (int)(0.6f * p1Deaths))
                {
                    Debug.Log("[AutoBalancer] P1 losing mostly by falling -> reducing P2 knockback dealt.");
                    AdjustKnockbackDealt(player2Config, 1f - adjustStep);
                }
                else
                {
                    Debug.Log("[AutoBalancer] P1 losing mostly by HP -> reducing P2 attack damage multiplier.");
                    AdjustAttackDamageMultiplier(player2Config, 1f - adjustStep);
                }
            }
            else
            {
                // P2 losing
                int p2Falls = _p2FallDeathsInBatch;
                int p2HpDies = _p2HpDeathsInBatch;
                int p2Deaths = p2Falls + p2HpDies;
                if (p2Deaths == 0) p2Deaths = 1;

                if (p2Falls >= (int)(0.6f * p2Deaths))
                {
                    Debug.Log("[AutoBalancer] P2 losing mostly by falling -> reducing P1 knockback dealt.");
                    AdjustKnockbackDealt(player1Config, 1f - adjustStep);
                }
                else
                {
                    Debug.Log("[AutoBalancer] P2 losing mostly by HP -> reducing P1 attack damage multiplier.");
                    AdjustAttackDamageMultiplier(player1Config, 1f - adjustStep);
                }
            }

            // Persist if requested (Editor-only)
#if UNITY_EDITOR
            if (persistChangesToAssets)
            {
                TrySaveAsset(player1Config?.stats);
                TrySaveAsset(player2Config?.stats);
            }
#endif
            // Append updated stats snapshot for this iteration if requested
            if (appendPerIteration)
                AppendUpdatedStatsSnapshot(iteration, false);

            // Wait one respawn cycle so new stats take effect on next spawns (GameManager respawns naturally)
            yield return new WaitForSeconds(Mathf.Clamp(gameManager != null ? gameManager.respawnDelay + 0.1f : 1f, 0.1f, 10f));
        }

        // If loop ended naturally and not already appended final, append final snapshot
        if (!_running && appendPerIteration)
        {
            AppendUpdatedStatsSnapshot(-1, true);
        }

        _running = false;
        Debug.Log("[AutoBalancer] Balancing routine finished.");
    }

    IEnumerator WaitForMatchEnd(int startP1, int startP2)
    {
        // Poll until one of the counts increases
        while (_running)
        {
            int nowP1 = gameManager.GetWins("P1");
            int nowP2 = gameManager.GetWins("P2");
            if (nowP1 > startP1 || nowP2 > startP2) yield break;
            // Re-subscribe occasionally in case objects were recreated
            SubscribeInstancesSafe();
            yield return new WaitForSeconds(0.1f);
        }
    }

    // Attempt to find current P1/P2 instances and subscribe to their Health events.
    void SubscribeInstancesSafe()
    {
        // If already subscribed and still valid, keep
        if (_p1Health != null && _p2Health != null) return;

        // Try to find by name convention used by GameManager.SpawnFighter (P1_AI / P2_AI or P1 / P2)
        var allHealths = FindObjectsOfType<Health>();
        foreach (var h in allHealths)
        {
            if (h == null || h.gameObject == null) continue;
            string n = h.gameObject.name ?? "";
            // Prefer exact names used at spawn
            if ((n.StartsWith("P1") || n.StartsWith("P1_")) && _p1Health == null)
            {
                SubscribeP1(h);
            }
            else if ((n.StartsWith("P2") || n.StartsWith("P2_")) && _p2Health == null)
            {
                SubscribeP2(h);
            }
        }

        // If still not found, fall back to matching transforms near spawn positions in GameManager (best-effort)
        if (_p1Health == null || _p2Health == null)
        {
            var fighters = FindObjectsOfType<FighterController>();
            foreach (var f in fighters)
            {
                if (f == null || f.gameObject == null) continue;
                if (f.gameObject.name.StartsWith("P1") && _p1Health == null)
                {
                    var h = f.GetComponent<Health>();
                    if (h != null) SubscribeP1(h);
                }
                else if (f.gameObject.name.StartsWith("P2") && _p2Health == null)
                {
                    var h = f.GetComponent<Health>();
                    if (h != null) SubscribeP2(h);
                }
            }
        }
    }

    void UnsubscribeInstances()
    {
        if (_p1Health != null)
        {
            _p1Health.OnDamaged -= OnP1Damaged;
            _p1Health.OnDeath -= OnP1Death;
            _p1Health = null;
        }
        if (_p2Health != null)
        {
            _p2Health.OnDamaged -= OnP2Damaged;
            _p2Health.OnDeath -= OnP2Death;
            _p2Health = null;
        }
    }

    void SubscribeP1(Health h)
    {
        if (h == null) return;
        if (_p1Health == h) return;
        if (_p1Health != null)
        {
            _p1Health.OnDamaged -= OnP1Damaged;
            _p1Health.OnDeath -= OnP1Death;
        }
        _p1Health = h;
        _p1Health.OnDamaged += OnP1Damaged;
        _p1Health.OnDeath += OnP1Death;
        Debug.Log($"[AutoBalancer] Subscribed to P1 health on '{h.gameObject.name}'.");
    }

    void SubscribeP2(Health h)
    {
        if (h == null) return;
        if (_p2Health == h) return;
        if (_p2Health != null)
        {
            _p2Health.OnDamaged -= OnP2Damaged;
            _p2Health.OnDeath -= OnP2Death;
        }
        _p2Health = h;
        _p2Health.OnDamaged += OnP2Damaged;
        _p2Health.OnDeath += OnP2Death;
        Debug.Log($"[AutoBalancer] Subscribed to P2 health on '{h.gameObject.name}'.");
    }

    // When P1 is damaged, we infer attacker is P2 (two-player env)
    void OnP1Damaged(float remainingHp)
    {
        _p2HitsThisMatch++;
        // (optional) could record timestamps for hit-rate analytics
    }

    void OnP2Damaged(float remainingHp)
    {
        _p1HitsThisMatch++;
    }

    void OnP1Death()
    {
        bool fell = DidFall(_p1Health);
        if (fell) _p1FallDeathsInBatch++; else _p1HpDeathsInBatch++;
    }

    void OnP2Death()
    {
        bool fell = DidFall(_p2Health);
        if (fell) _p2FallDeathsInBatch++; else _p2HpDeathsInBatch++;
    }

    bool DidFall(Health h)
    {
        if (h == null || h.gameObject == null || gameManager == null) return false;
        return h.gameObject.transform.position.y <= gameManager.fallDeathY;
    }

    // Adjustment helpers ------------------------------------------------

    void AdjustAttackDamageMultiplier(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        float old = cfg.stats.attackDamageMultiplier;
        float next = Mathf.Clamp(old * scale, 0.2f, 3.0f);
        cfg.stats.attackDamageMultiplier = next;
        Debug.Log($"[AutoBalancer] {cfg.name}: attackDamageMultiplier {old:F3} -> {next:F3}");
    }

    void AdjustKnockbackDealt(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        float old = cfg.stats.knockbackDealtMultiplier;
        float next = Mathf.Clamp(old * scale, 0.3f, 3.0f);
        cfg.stats.knockbackDealtMultiplier = next;
        Debug.Log($"[AutoBalancer] {cfg.name}: knockbackDealtMultiplier {old:F3} -> {next:F3}");
    }

    // --- File output helpers ------------------------------------------

    void SaveDefaultStatsSnapshot()
    {
        try
        {
            _defaultP1Snapshot = FormatStatsSnapshot("DEFAULT STATS - P1", player1Config);
            _defaultP2Snapshot = FormatStatsSnapshot("DEFAULT STATS - P2", player2Config);

            string header = $"AutoBalancer Snapshot Start - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
            string content = header + _defaultP1Snapshot + "\n" + _defaultP2Snapshot + "\n" + new string('-', 60) + "\n\n";
            File.WriteAllText(_outputPath, content);
            Debug.Log($"[AutoBalancer] Wrote default stats to {_outputPath}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AutoBalancer] Failed to write default stats file: " + ex);
        }
    }

    void AppendUpdatedStatsSnapshot(int iteration, bool isFinal)
    {
        try
        {
            string title = isFinal ? "FINAL UPDATED STATS" : $"UPDATED STATS - Iteration {iteration}";
            string p1 = FormatStatsSnapshot($"P1 ({player1Config?.name ?? "P1"})", player1Config);
            string p2 = FormatStatsSnapshot($"P2 ({player2Config?.name ?? "P2"})", player2Config);
            string stamp = $"{title} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
            string content = stamp + p1 + "\n" + p2 + "\n" + new string('-', 60) + "\n\n";
            File.AppendAllText(_outputPath, content);
            Debug.Log($"[AutoBalancer] Appended updated stats to {_outputPath}: {title}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AutoBalancer] Failed to append updated stats file: " + ex);
        }
    }

    string FormatStatsSnapshot(string header, PlayerConfig cfg)
    {
        if (cfg == null) return $"{header}\n  <PlayerConfig null>\n";
        var s = cfg.stats;
        if (s == null) return $"{header}\n  <FighterStats null>\n";

        return string.Join("\n", new[]
        {
            $"{header}",
            $"  moveSpeed = {s.moveSpeed:F3}",
            $"  acceleration = {s.acceleration:F3}",
            $"  deceleration = {s.deceleration:F3}",
            $"  jumpForce = {s.jumpForce:F3}",
            $"  canJump = {s.canJump}",
            $"  stunDuration = {s.stunDuration:F3}",
            $"  invulnAfterHit = {s.invulnAfterHit:F3}",
            $"  blockDamageReduction = {s.blockDamageReduction:F3}",
            $"  knockbackResistance = {s.knockbackResistance:F3}",
            $"  attackSpeed = {s.attackSpeed:F3}",
            $"  attackDamageMultiplier = {s.attackDamageMultiplier:F3}",
            $"  knockbackDealtMultiplier = {s.knockbackDealtMultiplier:F3}",
            $"  knockbackTakenMultiplier = {s.knockbackTakenMultiplier:F3}"
        });
    }

#if UNITY_EDITOR
    void TrySaveAsset(ScriptableObject so)
    {
        if (so == null) return;
        try
        {
            // Editor-only persistence: mark dirty and save
            UnityEditor.EditorUtility.SetDirty(so);
            UnityEditor.AssetDatabase.SaveAssets();
            Debug.Log($"[AutoBalancer] Persisted asset changes for '{so.name}'.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AutoBalancer] Could not persist asset changes: " + ex);
        }
    }
#endif
}
