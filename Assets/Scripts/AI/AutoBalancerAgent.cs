using System;
using System.Collections;
using System.IO;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

/// <summary>
/// AutoBalancerAgent: an ML-Agents Agent that runs balancing iterations above the game.
/// Each episode = apply one set of adjustments (continuous actions) to player stats,
/// run a batch of AI-vs-AI matches, observe the resulting P1 win-rate and death-cause counts,
/// and receive reward based on improvement toward the configured targetWinRate.
/// Attach to a scene object and assign GameManager + both PlayerConfig assets.
/// </summary>
public class AutoBalancerAgent : Agent
{
    [Header("Required")]
    public GameManager gameManager;
    public PlayerConfig player1Config; // corresponds to P1
    public PlayerConfig player2Config; // corresponds to P2

    [Header("Batch / Goal")]
    [Tooltip("Number of matches to gather after applying actions for reward calculation.")]
    public int matchesPerBatch = 30;
    [Tooltip("Target P1 win rate (0..1).")]
    public float targetWinRate = 0.5f;
    [Tooltip("Acceptable margin (±). If P1 rate is within target ± margin, the agent receives a large positive terminal reward.")]
    public float margin = 0.05f;

    [Header("Adjustment")]
    [Tooltip("Maximum fractional change applied when action = 1. Action values are in [-1,1] and scaled by this step.")]
    [Range(0.01f, 0.25f)]
    public float adjustStep = 0.05f;

    [Tooltip("If true, changes to ScriptableObject stats will be persisted to assets (Editor only).")]
    public bool persistChangesToAssets = false;

    [Header("Reward / Penalty")]
    [Tooltip("Small penalty proportional to magnitude of adjustments to encourage conservative edits.")]
    public float changeMagnitudePenalty = 0.01f;

    [Header("Debug / Output")]
    public string outputFileName = "AutoBalanceAgentStats.txt";
    public bool appendPerIteration = true;

    [Header("Agent Priority")]
    [Tooltip("If true the agent will prioritize minimizing win-rate error (targetWinRate) over applying automatic death-cause fixes.")]
    public bool prioritizeWinRate = true;
    [Tooltip("Weight applied to the improvement term in the reward calculation (higher -> stronger win-rate focus).")]
    public float improvementWeight = 15f;
    [Tooltip("Weight applied to the signed improvement shaping term.")]
    public float signedImprovementWeight = 0.2f;

    [Tooltip("Penalty weight applied when win-rate error increases compared to previous iteration.")]
    public float errorIncreasePenalty = 20f;

    // Internal bookkeeping for matchmaking polling
    private int _baselineP1Wins;
    private int _baselineP2Wins;
    private string _outputPath;

    // Health subscriptions and death-type counters (per-batch)
    private Health _p1Health;
    private Health _p2Health;
    private int _p1FallDeathsInBatch;
    private int _p2FallDeathsInBatch;
    private int _p1HpDeathsInBatch;
    private int _p2HpDeathsInBatch;

    // Last observed win-rate error (used to compute improvement)
    private float _lastError = 1f;

    // Lock to avoid re-entrancy when batch is running
    private bool _batchRunning = false;

    // Reward tracking for logging
    private float _lastCumulativeReward = 0f;
    private float _lastRewardDelta = 0f;

    // Iteration counter for autobalancer episodes
    private int _iteration = 0;

    void Start()
    {
        _outputPath = Path.Combine(Application.persistentDataPath, outputFileName);
    }

    public override void OnEpisodeBegin()
    {
        // Validate references
        if (gameManager == null || player1Config == null || player2Config == null)
        {
            Debug.LogWarning("[AutoBalancerAgent] Missing references. Assign GameManager and both PlayerConfig assets.");
            EndEpisode();
            return;
        }

        // Ensure output path set in case Start() hasn't run
        if (string.IsNullOrEmpty(_outputPath))
        {
            _outputPath = Path.Combine(Application.persistentDataPath, outputFileName);
        }

        // Reset per-batch counters
        _p1FallDeathsInBatch = _p2FallDeathsInBatch = 0;
        _p1HpDeathsInBatch = _p2HpDeathsInBatch = 0;

        SubscribeInstancesSafe();

        // Baseline wins used for polling matches
        _baselineP1Wins = gameManager.GetWins("P1");
        _baselineP2Wins = gameManager.GetWins("P2");

        // RequestDecision to get an action to apply this episode.
        RequestDecision();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // Observe current P1 win-rate estimate over last recorded wins (if any)
        int nowP1 = gameManager != null ? gameManager.GetWins("P1") : 0;
        int nowP2 = gameManager != null ? gameManager.GetWins("P2") : 0;
        int total = (nowP1 - _baselineP1Wins) + (nowP2 - _baselineP2Wins);
        float currentP1Rate = total > 0 ? (float)(nowP1 - _baselineP1Wins) / total : targetWinRate;

        // Error to target (signed): positive means P1 > target
        float signedError = currentP1Rate - targetWinRate;

        // Normalize and add
        sensor.AddObservation(Mathf.Clamp(signedError, -1f, 1f));

        // Add simple stat observations to give agent context
        if (player1Config?.stats != null)
        {
            sensor.AddObservation(NormalizeStat(player1Config.stats.attackDamageMultiplier, 0.2f, 3f));
            sensor.AddObservation(NormalizeStat(player1Config.stats.knockbackDealtMultiplier, 0.3f, 3f));
            sensor.AddObservation(NormalizeStat(player1Config.stats.attackSpeed, 0.1f, 5f));
            sensor.AddObservation(NormalizeStat(player1Config.stats.maxHp, 10f, 800f));
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        if (player2Config?.stats != null)
        {
            sensor.AddObservation(NormalizeStat(player2Config.stats.attackDamageMultiplier, 0.2f, 3f));
            sensor.AddObservation(NormalizeStat(player2Config.stats.knockbackDealtMultiplier, 0.3f, 3f));
            sensor.AddObservation(NormalizeStat(player2Config.stats.attackSpeed, 0.1f, 5f));
            sensor.AddObservation(NormalizeStat(player2Config.stats.maxHp, 10f, 800f));
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        // Provide death-cause counts (normalized by matchesPerBatch)
        sensor.AddObservation(Mathf.Clamp01((float)_p1FallDeathsInBatch / Mathf.Max(1, matchesPerBatch)));
        sensor.AddObservation(Mathf.Clamp01((float)_p1HpDeathsInBatch / Mathf.Max(1, matchesPerBatch)));
        sensor.AddObservation(Mathf.Clamp01((float)_p2FallDeathsInBatch / Mathf.Max(1, matchesPerBatch)));
        sensor.AddObservation(Mathf.Clamp01((float)_p2HpDeathsInBatch / Mathf.Max(1, matchesPerBatch)));
    }

    // Actions: 8 continuous values in [-1,1]:
    // 0 = delta for P1 attackDamageMultiplier (scale = 1 + action*adjustStep)
    // 1 = delta for P1 knockbackDealtMultiplier
    // 2 = delta for P2 attackDamageMultiplier
    // 3 = delta for P2 knockbackDealtMultiplier
    // 4 = delta for P1 attackSpeed (scale = 1 + action*adjustStep)
    // 5 = delta for P1 maxHp (scale = 1 + action*adjustStep)
    // 6 = delta for P2 attackSpeed
    // 7 = delta for P2 maxHp
    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_batchRunning)
        {
            // refuse to reapply changes while batch running
            AddReward(-0.01f);
            return;
        }

        // Count this as one autobalancer iteration (one set of edits + a measurement batch)
        _iteration++;

        var cont = actions.ContinuousActions;
        float aP1Atk = Mathf.Clamp(cont[0], -1f, 1f);
        float aP1Kb = Mathf.Clamp(cont[1], -1f, 1f);
        float aP2Atk = Mathf.Clamp(cont[2], -1f, 1f);
        float aP2Kb = Mathf.Clamp(cont[3], -1f, 1f);
        float aP1AtkSpd = cont.Length > 4 ? Mathf.Clamp(cont[4], -1f, 1f) : 0f;
        float aP1MaxHp = cont.Length > 5 ? Mathf.Clamp(cont[5], -1f, 1f) : 0f;
        float aP2AtkSpd = cont.Length > 6 ? Mathf.Clamp(cont[6], -1f, 1f) : 0f;
        float aP2MaxHp = cont.Length > 7 ? Mathf.Clamp(cont[7], -1f, 1f) : 0f;

        // Convert actions -> multiplicative scales
        float scaleP1Atk = 1f + aP1Atk * adjustStep;
        float scaleP1Kb = 1f + aP1Kb * adjustStep;
        float scaleP2Atk = 1f + aP2Atk * adjustStep;
        float scaleP2Kb = 1f + aP2Kb * adjustStep;

        float scaleP1AtkSpd = 1f + aP1AtkSpd * adjustStep;
        float scaleP1MaxHp = 1f + aP1MaxHp * adjustStep;
        float scaleP2AtkSpd = 1f + aP2AtkSpd * adjustStep;
        float scaleP2MaxHp = 1f + aP2MaxHp * adjustStep;

        // Apply adjustments immediately (to stats so they persist to next spawns)
        AdjustAttackDamageMultiplier(player1Config, scaleP1Atk);
        AdjustKnockbackDealt(player1Config, scaleP1Kb);
        AdjustAttackDamageMultiplier(player2Config, scaleP2Atk);
        AdjustKnockbackDealt(player2Config, scaleP2Kb);

        AdjustAttackSpeed(player1Config, scaleP1AtkSpd);
        AdjustMaxHp(player1Config, scaleP1MaxHp);
        AdjustAttackSpeed(player2Config, scaleP2AtkSpd);
        AdjustMaxHp(player2Config, scaleP2MaxHp);

#if UNITY_EDITOR
        if (persistChangesToAssets)
        {
            TrySaveAsset(player1Config?.stats);
            TrySaveAsset(player2Config?.stats);
        }
#endif

        // Small penalty proportional to magnitude of changes to encourage minimal edits
        float mag = Mathf.Abs(aP1Atk) + Mathf.Abs(aP1Kb) + Mathf.Abs(aP2Atk) + Mathf.Abs(aP2Kb)
                    + Mathf.Abs(aP1AtkSpd) + Mathf.Abs(aP1MaxHp) + Mathf.Abs(aP2AtkSpd) + Mathf.Abs(aP2MaxHp);
        AddReward(-changeMagnitudePenalty * mag);

        // Start batch measurement (as a coroutine) which will compute the reward and end the episode.
        StartCoroutine(RunBatchAndAssignReward());
    }

    // Heuristic for manual testing (optional)
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var cont = actionsOut.ContinuousActions;
        // No-op heuristic (zero => no change)
        for (int i = 0; i < cont.Length; i++) cont[i] = 0f;
    }

    IEnumerator RunBatchAndAssignReward()
    {
        _batchRunning = true;

        // Reset per-batch death counters
        _p1FallDeathsInBatch = _p2FallDeathsInBatch = 0;
        _p1HpDeathsInBatch = _p2HpDeathsInBatch = 0;

        int batchWinsP1 = 0;
        int batchWinsP2 = 0;

        // Refresh baseline so we only count wins that occur during this batch
        int waitStartP1 = gameManager.GetWins("P1");
        int waitStartP2 = gameManager.GetWins("P2");

        // Capture cumulative reward before measurement so we can compute delta for logging
        float beforeReward = GetCumulativeReward();

        // Run matchesPerBatch matches
        for (int m = 0; m < matchesPerBatch; m++)
        {
            // Wait for next match end
            yield return StartCoroutine(WaitForMatchEnd(waitStartP1, waitStartP2));

            int nowP1 = gameManager.GetWins("P1");
            int nowP2 = gameManager.GetWins("P2");

            if (nowP1 > waitStartP1) batchWinsP1++;
            else if (nowP2 > waitStartP2) batchWinsP2++;
            else /* warning suppressed */ { /* no-op */ }

            // advance baseline for next match
            waitStartP1 = nowP1;
            waitStartP2 = nowP2;

            // small yield to allow spawns to refresh and health subscriptions to re-bind
            yield return new WaitForSeconds(Mathf.Clamp(gameManager != null ? gameManager.respawnDelay + 0.01f : 0.1f, 0.01f, 5f));
        }

        int total = batchWinsP1 + batchWinsP2;
        if (total == 0)
        {
            // no data: small negative reward and end
            AddReward(-0.1f);
            float afterRewardNoData = GetCumulativeReward();
            _lastRewardDelta = afterRewardNoData - beforeReward;
            _lastCumulativeReward = afterRewardNoData;
            EndEpisode();
            _batchRunning = false;
            yield break;
        }

        float batchP1Rate = (float)batchWinsP1 / total;
        float error = Mathf.Abs(batchP1Rate - targetWinRate);
        float signedError = batchP1Rate - targetWinRate;

        // NEW: detect if stage knockouts dominate and apply conservative global bias toward making HP matter
        int totalDeaths = _p1FallDeathsInBatch + _p2FallDeathsInBatch + _p1HpDeathsInBatch + _p2HpDeathsInBatch;
        float fallFraction = 0f;
        if (totalDeaths > 0)
        {
            fallFraction = (float)(_p1FallDeathsInBatch + _p2FallDeathsInBatch) / totalDeaths;
            if (fallFraction >= 0.6f)
            {
                // If user asked to prioritize win-rate, skip the automatic HP-focused corrective edits.
                if (!prioritizeWinRate)
                {
                    //Debug.Log($"[AutoBalancerAgent][Iter {_iteration}] High fall-rate detected ({fallFraction:P1}). Applying conservative HP-focused bias: decrease maxHp and increase damage on both fighters.");
                    // Decrease maxHp slightly and increase attack damage slightly for both players to make HP more relevant.
                    AdjustMaxHp(player1Config, 1f - adjustStep);
                    AdjustMaxHp(player2Config, 1f - adjustStep);
                    AdjustAttackDamageMultiplier(player1Config, 1f + adjustStep);
                    AdjustAttackDamageMultiplier(player2Config, 1f + adjustStep);

#if UNITY_EDITOR
                    if (persistChangesToAssets)
                    {
                        TrySaveAsset(player1Config?.stats);
                        TrySaveAsset(player2Config?.stats);
                    }
#endif
                }

                // Always append an iteration snapshot so training logs include the fall fraction context.
                if (appendPerIteration) AppendUpdatedStatsSnapshot(batchP1Rate, false, fallFraction, _iteration);
            }
        }

        // Reward = improvement in error relative to previous error (positive if closer to target).
        // Use configurable weights so we can prioritize pure win-rate improvement.
        //AddReward((1f - error) * improvementWeight);
        // Use delta-based improvement reward (positive when error decreased compared to last iteration)
        float improvementDelta = _lastError - error;
        AddReward(improvementDelta * improvementWeight);

        // Small shaping: reward moving signed error toward 0 (sign-sensitive).
        float signedImprovement = Mathf.Sign(targetWinRate - batchP1Rate) * Mathf.Clamp(1f - error, 0f, 1f);
        AddReward(signedImprovement * signedImprovementWeight);

        // Penalize increases in error to discourage edits that make balance worse
        if (error > _lastError)
        {
            float increase = error - _lastError;
            AddReward(-increase * errorIncreasePenalty);
        }

        // Terminal success bonus if within margin
        if (error <= margin)
        {
            AddReward(2.0f);
            // Optionally persist final snapshot
            float afterRewardFinal = GetCumulativeReward();
            _lastRewardDelta = afterRewardFinal - beforeReward;
            _lastCumulativeReward = afterRewardFinal;
            if (appendPerIteration) AppendUpdatedStatsSnapshot(batchP1Rate, true, fallFraction, _iteration);
            _lastError = error;
            EndEpisode();
            _batchRunning = false;
            yield break;
        }

        // If not terminal, small negative for oscillation (optional). Log snapshot.
        if (appendPerIteration) AppendUpdatedStatsSnapshot(batchP1Rate, false, fallFraction, _iteration);

        // Update last error for next episode
        _lastError = error;

        // Small time penalty to prefer faster convergence
        AddReward(-0.01f);

        // Compute final reward delta for logging
        float afterReward = GetCumulativeReward();
        _lastRewardDelta = afterReward - beforeReward;
        _lastCumulativeReward = afterReward;

        EndEpisode();
        _batchRunning = false;
    }

    // Polling helper: wait until either P1 or P2 wins increases
    IEnumerator WaitForMatchEnd(int startP1, int startP2)
    {
        while (true)
        {
            int nowP1 = gameManager.GetWins("P1");
            int nowP2 = gameManager.GetWins("P2");
            if (nowP1 > startP1 || nowP2 > startP2) yield break;
            SubscribeInstancesSafe();
            // Poll every frame instead of waiting 50ms — reduces added latency when respawnDelay is zero.
            yield return null;
        }
    }

    // --- Subscriptions & death detection (copied/adapted from AutoBalancer) ---
    void SubscribeInstancesSafe()
    {
        if (_p1Health != null && _p2Health != null) return;

        var allHealths = FindObjectsOfType<Health>();
        foreach (var h in allHealths)
        {
            if (h == null || h.gameObject == null) continue;
            string n = h.gameObject.name ?? "";
            if ((n.StartsWith("P1") || n.StartsWith("P1_")) && _p1Health == null)
            {
                SubscribeP1(h);
            }
            else if ((n.StartsWith("P2") || n.StartsWith("P2_")) && _p2Health == null)
            {
                SubscribeP2(h);
            }
        }

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
        //Debug.Log($"[AutoBalancerAgent] Subscribed to P1 health on '{h.gameObject.name}'.");
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
        //Debug.Log($"[AutoBalancerAgent] Subscribed to P2 health on '{h.gameObject.name}'.");
    }

    void OnP1Damaged(float remainingHp)
    {
        // counts can be expanded if needed (currently only death causes are used)
    }

    void OnP2Damaged(float remainingHp)
    {
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

    // --- Stat adjustment helpers (copied/adapted) ---
    void AdjustAttackDamageMultiplier(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        float old = cfg.stats.attackDamageMultiplier;
        bool heavy = IsHeavy(cfg);
        float max = heavy ? 2.0f : 1.5f;
        float next = Mathf.Clamp(old * scale, 0.2f, max);
        cfg.stats.attackDamageMultiplier = next;
        cfg.stats.Normalize();
        //Debug.Log($"[AutoBalancerAgent] {cfg.name}: attackDamageMultiplier {old:F3} -> {next:F3}");
    }

    void AdjustKnockbackDealt(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        bool heavy = IsHeavy(cfg);
        float min = heavy ? 0.5f : 0.3f;
        float max = heavy ? 2.0f : 1.5f;
        float old = cfg.stats.knockbackDealtMultiplier;
        float next = Mathf.Clamp(old * scale, min, max);
        cfg.stats.knockbackDealtMultiplier = next;
        cfg.stats.Normalize();
        //Debug.Log($"[AutoBalancerAgent] {cfg.name}: knockbackDealtMultiplier {old:F3} -> {next:F3}");
    }

    void AdjustAttackSpeed(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        bool heavy = IsHeavy(cfg);
        float min = heavy ? 0.5f : 0.1f;
        float max = heavy ? 1.0f : 3.0f;
        float old = cfg.stats.attackSpeed;
        float next = Mathf.Clamp(old * scale, min, max);
        cfg.stats.attackSpeed = next;
        // apply to any live instance
        ApplyInstanceStatsIfPresent(cfg);
        cfg.stats.Normalize();
        //Debug.Log($"[AutoBalancerAgent] {cfg.name}: attackSpeed {old:F3} -> {next:F3}");
    }

    void AdjustMoveSpeed(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        bool heavy = IsHeavy(cfg);
        float min = heavy ? 2f : 2f;
        float max = heavy ? 6f : 10f;
        float old = cfg.stats.moveSpeed;
        float next = Mathf.Clamp(old * scale, min, max);
        cfg.stats.moveSpeed = next;
        ApplyInstanceStatsIfPresent(cfg);
        cfg.stats.Normalize();
        //Debug suppressed
    }

    void AdjustMaxHp(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        bool heavy = IsHeavy(cfg);
        // per-class HP floor/cap
        float min = heavy ? 100f : 50f;
        float max = heavy ? 800f : 500f;
        float old = cfg.stats.maxHp;
        float next = Mathf.Clamp(old * scale, min, max);
        cfg.stats.maxHp = next;
        //Apply immediate effect to currently subscribed instances so changes are visible without waiting for respawn
        if (cfg == player1Config && _p1Health != null)
        {
            _p1Health.maxHp = next;
            _p1Health.HealFull();
        }
        else if (cfg == player2Config && _p2Health != null)
        {
            _p2Health.maxHp = next;
            _p2Health.HealFull();
        }

        // Ensure the per-class clamps are satisfied
        cfg.stats.Normalize();

        // Enforce cross-class relationship: Default.maxHp <= 75% * Heavy.maxHp (if both present)
        EnforceHpRatio();

        //Debug.Log($"[AutoBalancerAgent] {cfg.name}: maxHp {old:F3} -> {next:F3}");
    }

    //// Helper: apply small class-role bias nudges
    //void ApplyClassBias(PlayerConfig cfg, float biasFactor)
    //{
    //    if (cfg == null || cfg.stats == null) return;
    //    if (IsHeavy(cfg))
    //    {
    //        // Favor heavier role: slightly more HP and knockback
    //        AdjustMaxHp(cfg, 1f + biasFactor);
    //        AdjustKnockbackDealt(cfg, 1f + biasFactor);
    //    }
    //    else
    //    {
    //        // Default fighters: favor attack speed and move speed
    //        AdjustAttackSpeed(cfg, 1f + biasFactor);
    //        AdjustMoveSpeed(cfg, 1f + biasFactor);
    //    }
    //}

    bool IsHeavy(PlayerConfig cfg)
    {
        if (cfg == null || cfg.stats == null) return false;
        return cfg.stats is HeavyFighterStats;
    }

    // Ensure that when both player configs exist, the Default fighter's HP is never more than 75% of Heavy's.
    // If necessary, this will raise heavy HP to satisfy the relationship (but never below heavy minimum).
    private void EnforceHpRatio()
    {
        if (player1Config == null || player2Config == null) return;
        if (player1Config.stats == null || player2Config.stats == null) return;

        // Identify heavy vs default (if both heavy or both default, nothing to enforce)
        PlayerConfig heavyCfg = IsHeavy(player1Config) ? player1Config : (IsHeavy(player2Config) ? player2Config : null);
        PlayerConfig defaultCfg = (!IsHeavy(player1Config)) ? player1Config : ((!IsHeavy(player2Config)) ? player2Config : null);

        if (heavyCfg == null || defaultCfg == null) return;

        // Local short-hands
        var heavyStats = heavyCfg.stats;
        var defStats = defaultCfg.stats;

        // Enforce minima first
        heavyStats.maxHp = Mathf.Max(heavyStats.maxHp, 100f);
        defStats.maxHp = Mathf.Max(defStats.maxHp, 50f);

        // --- Cross-class constraints ---

        // 1) Ensure default's maxHp is not greater than heavy's maxHp.
        //    If it is, raise heavy to match default (prefer preserving relative designer intent).
        if (defStats.maxHp > heavyStats.maxHp)
        {
            heavyStats.maxHp = Mathf.Clamp(defStats.maxHp, 100f, 800f);
        }

        // 2) Ensure default's attackDamageMultiplier <= heavy's attackDamageMultiplier.
        //    If default exceeds heavy, raise heavy to match default.
        if (defStats.attackDamageMultiplier > heavyStats.attackDamageMultiplier)
        {
            heavyStats.attackDamageMultiplier = Mathf.Clamp(defStats.attackDamageMultiplier, 0.2f, 2.0f);
        }

        // 3) Ensure default's knockbackDealtMultiplier <= heavy's knockbackDealtMultiplier.
        if (defStats.knockbackDealtMultiplier > heavyStats.knockbackDealtMultiplier)
        {
            heavyStats.knockbackDealtMultiplier = Mathf.Clamp(defStats.knockbackDealtMultiplier, 0.3f, 2.0f);
        }

        // 4) Ensure heavy's attackSpeed does not exceed half of the default's attackSpeed.
        //    If heavy is too fast, cap it to 50% of default's attackSpeed (respecting heavy bounds).
        float maxAllowedHeavyAttackSpeed = defStats.attackSpeed * 0.5f;
        // Determine heavy bounds for attackSpeed
        float heavyMin = 0.5f;
        float heavyMax = 1.0f;
        float cappedHeavy = Mathf.Clamp(heavyStats.attackSpeed, heavyMin, heavyMax);
        if (cappedHeavy > maxAllowedHeavyAttackSpeed)
        {
            heavyStats.attackSpeed = Mathf.Clamp(maxAllowedHeavyAttackSpeed, heavyMin, heavyMax);
        }

        // Apply per-class Normalize to enforce per-field bounds and sanitization
        heavyStats.Normalize();
        defStats.Normalize();

        // Apply to instances if present
        ApplyInstanceStatsIfPresent(player1Config);
        ApplyInstanceStatsIfPresent(player2Config);
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
            string title = isFinal ? "AGENT FINAL SNAPSHOT" : $"AGENT ITER SNAPSHOT";
            string content = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {title}\n";
            content += $"  iteration = {iteration}\n";
            content += $"  p1_rate = {p1Rate:F3}\n";
            content += $"  fall_death_fraction = {fallFraction:P1} (p1_falls={_p1FallDeathsInBatch}, p2_falls={_p2FallDeathsInBatch}, p1_hp={_p1HpDeathsInBatch}, p2_hp={_p2HpDeathsInBatch})\n";
            content += FormatStatsSnapshot("P1", player1Config) + "\n" + FormatStatsSnapshot("P2", player2Config) + "\n";
            content += $"  cumulative_reward = {_lastCumulativeReward:F4}\n";
            content += $"  last_reward_delta = {_lastRewardDelta:F4}\n";
            content += new string('-', 60) + "\n";
            File.AppendAllText(_outputPath, content);

            // Also print concise line to console for quick inspection
            //Debug.Log($"[AutoBalancerAgent][Iter {iteration}] p1_rate={p1Rate:F3} fall%={fallFraction:P1} reward_delta={_lastRewardDelta:F4}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AutoBalancerAgent] Failed to append stats: " + ex);
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
        catch (Exception ex)
        {
            Debug.LogWarning("[AutoBalancerAgent] Could not persist asset changes: " + ex);
        }
    }
#endif

    void OnDestroy()
    {
        UnsubscribeInstances();
    }

    // Apply derived stats immediately to a live FighterController instance if present for the given PlayerConfig.
    void ApplyInstanceStatsIfPresent(PlayerConfig cfg)
    {
        if (cfg == null) return;

        if (cfg == player1Config && _p1Health != null)
        {
            var fc = _p1Health.GetComponent<FighterController>();
            if (fc != null)
            {
                // Ensure the live instance uses the updated PlayerConfig.stats reference so multipliers (knockback etc.) take effect immediately.
                fc.stats = cfg.stats;
                fc.ApplyStats();
            }
        }
        else if (cfg == player2Config && _p2Health != null)
        {
            var fc = _p2Health.GetComponent<FighterController>();
            if (fc != null)
            {
                fc.stats = cfg.stats;
                fc.ApplyStats();
            }
        }
    }
}
