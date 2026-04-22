using System;
using System.Collections;
using System.IO;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

/// <summary>
/// ML-Agents auto-balancer that iteratively adjusts player stats to achieve a target win rate.
/// Each episode applies stat adjustments, runs a batch of AI matches, then rewards based on improvement.
/// Attach to a scene object and assign GameManager + both PlayerConfig assets.
/// </summary>
public class AutoBalancerAgent : Agent
{
    [Header("Required")]
    public GameManager gameManager;
    public PlayerConfig player1Config; // P1 configuration
    public PlayerConfig player2Config; // P2 configuration

    [Header("Batch / Goal")]
    [Tooltip("Number of matches to run per balancing iteration for statistical measurement.")]
    public int matchesPerBatch = 30;
    [Tooltip("Target P1 win rate (0..1). Agent tries to achieve this balance.")]
    public float targetWinRate = 0.5f;
    [Tooltip("Acceptable margin (±). If P1 rate is within target ± margin, large terminal reward is given.")]
    public float margin = 0.05f;

    [Header("Adjustment")]
    [Tooltip("Maximum fractional change applied when action = 1. Actions are in [-1,1] and scaled by this step.")]
    [Range(0.01f, 0.25f)]
    public float adjustStep = 0.05f;

    [Tooltip("If true, changes to ScriptableObject stats will be persisted to assets (Editor only).")]
    public bool persistChangesToAssets = false;

    [Header("Reward / Penalty")]
    [Tooltip("Small penalty proportional to magnitude of adjustments to encourage minimal changes.")]
    public float changeMagnitudePenalty = 0.01f;

    [Header("Debug / Output")]
    public string outputFileName = "AutoBalanceAgentStats.txt";
    public bool appendPerIteration = true;

    [Header("Agent Priority")]
    [Tooltip("If true, agent prioritizes win-rate error over automatic death-cause fixes.")]
    public bool prioritizeWinRate = true;
    [Tooltip("Weight applied to the improvement term in reward (higher = stronger win-rate focus).")]
    public float improvementWeight = 15f;
    [Tooltip("Weight applied to signed improvement shaping term.")]
    public float signedImprovementWeight = 0.2f;

    [Tooltip("Penalty weight when win-rate error increases compared to previous iteration.")]
    public float errorIncreasePenalty = 20f;

    // Internal bookkeeping
    private int _baselineP1Wins;
    private int _baselineP2Wins;
    private string _outputPath;

    // Health subscriptions and death-type counters
    private Health _p1Health;
    private Health _p2Health;
    private int _p1FallDeathsInBatch;
    private int _p2FallDeathsInBatch;
    private int _p1HpDeathsInBatch;
    private int _p2HpDeathsInBatch;

    // Last observed win-rate error for delta calculation
    private float _lastError = 1f;

    // Lock to avoid re-entrancy during batch
    private bool _batchRunning = false;

    // Reward tracking for logging
    private float _lastCumulativeReward = 0f;
    private float _lastRewardDelta = 0f;

    // Iteration counter
    private int _iteration = 0;

    /// <summary>
    /// Unity Start: initialize output path.
    /// </summary>
    void Start()
    {
        _outputPath = Path.Combine(Application.persistentDataPath, outputFileName);
    }

    /// <summary>
    /// Called at the start of each episode. Validates references, resets counters, subscribes to health events.
    /// </summary>
    public override void OnEpisodeBegin()
    {
        // Validate references
        if (gameManager == null || player1Config == null || player2Config == null)
        {
            Debug.LogWarning("[AutoBalancerAgent] Missing references. Assign GameManager and both PlayerConfig assets.");
            EndEpisode();
            return;
        }

        // Ensure output path set
        if (string.IsNullOrEmpty(_outputPath))
        {
            _outputPath = Path.Combine(Application.persistentDataPath, outputFileName);
        }

        // Reset per-batch counters
        _p1FallDeathsInBatch = _p2FallDeathsInBatch = 0;
        _p1HpDeathsInBatch = _p2HpDeathsInBatch = 0;

        SubscribeInstancesSafe();

        // Capture baseline wins for polling
        _baselineP1Wins = gameManager.GetWins("P1");
        _baselineP2Wins = gameManager.GetWins("P2");

        // Request decision to get actions for this episode
        RequestDecision();
    }

    /// <summary>
    /// Collect observations: current P1 win-rate error, player stats, and death-cause counts.
    /// </summary>
    public override void CollectObservations(VectorSensor sensor)
    {
        // Observe current P1 win-rate estimate
        int nowP1 = gameManager != null ? gameManager.GetWins("P1") : 0;
        int nowP2 = gameManager != null ? gameManager.GetWins("P2") : 0;
        int total = (nowP1 - _baselineP1Wins) + (nowP2 - _baselineP2Wins);
        float currentP1Rate = total > 0 ? (float)(nowP1 - _baselineP1Wins) / total : targetWinRate;

        // Signed error: positive means P1 > target
        float signedError = currentP1Rate - targetWinRate;
        sensor.AddObservation(Mathf.Clamp(signedError, -1f, 1f));

        // Add stat observations for context
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

        // Death-cause counts (normalized by matchesPerBatch)
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
            // Refuse to reapply changes while batch is running
            AddReward(-0.01f);
            return;
        }

        // Increment iteration counter
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

        // Convert actions to multiplicative scales
        float scaleP1Atk = 1f + aP1Atk * adjustStep;
        float scaleP1Kb = 1f + aP1Kb * adjustStep;
        float scaleP2Atk = 1f + aP2Atk * adjustStep;
        float scaleP2Kb = 1f + aP2Kb * adjustStep;

        float scaleP1AtkSpd = 1f + aP1AtkSpd * adjustStep;
        float scaleP1MaxHp = 1f + aP1MaxHp * adjustStep;
        float scaleP2AtkSpd = 1f + aP2AtkSpd * adjustStep;
        float scaleP2MaxHp = 1f + aP2MaxHp * adjustStep;

        // Apply adjustments to stats
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
        for (int i = 0; i < cont.Length; i++) cont[i] = 0f;
    }

    /// <summary>
    /// Run a batch of matches, measure P1 win-rate, compute reward based on improvement, then end episode.
    /// </summary>
    IEnumerator RunBatchAndAssignReward()
    {
        _batchRunning = true;

        // Reset per-batch death counters
        _p1FallDeathsInBatch = _p2FallDeathsInBatch = 0;
        _p1HpDeathsInBatch = _p2HpDeathsInBatch = 0;

        int batchWinsP1 = 0;
        int batchWinsP2 = 0;

        // Refresh baseline
        int waitStartP1 = gameManager.GetWins("P1");
        int waitStartP2 = gameManager.GetWins("P2");

        // Capture cumulative reward before measurement
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

            // Small yield to allow spawns and subscriptions to refresh
            yield return new WaitForSeconds(Mathf.Clamp(gameManager != null ? gameManager.respawnDelay + 0.01f : 0.1f, 0.01f, 5f));
        }

        int total = batchWinsP1 + batchWinsP2;
        if (total == 0)
        {
            // No data: small negative reward
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

        // Detect if stage knockouts dominate and apply conservative global bias
        int totalDeaths = _p1FallDeathsInBatch + _p2FallDeathsInBatch + _p1HpDeathsInBatch + _p2HpDeathsInBatch;
        float fallFraction = 0f;
        if (totalDeaths > 0)
        {
            fallFraction = (float)(_p1FallDeathsInBatch + _p2FallDeathsInBatch) / totalDeaths;
            if (fallFraction >= 0.6f)
            {
                // If prioritizing win-rate, skip automatic HP-focused corrections
                if (!prioritizeWinRate)
                {
                    // Decrease maxHp and increase damage to make HP more relevant
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

                // Always log iteration snapshot
                if (appendPerIteration) AppendUpdatedStatsSnapshot(batchP1Rate, false, fallFraction, _iteration);
            }
        }

        // Reward based on improvement in error
        float improvementDelta = _lastError - error;
        AddReward(improvementDelta * improvementWeight);

        // Small shaping: reward moving signed error toward 0
        float signedImprovement = Mathf.Sign(targetWinRate - batchP1Rate) * Mathf.Clamp(1f - error, 0f, 1f);
        AddReward(signedImprovement * signedImprovementWeight);

        // Penalize increases in error
        if (error > _lastError)
        {
            float increase = error - _lastError;
            AddReward(-increase * errorIncreasePenalty);
        }

        // Terminal success bonus if within margin
        if (error <= margin)
        {
            AddReward(2.0f);
            float afterRewardFinal = GetCumulativeReward();
            _lastRewardDelta = afterRewardFinal - beforeReward;
            _lastCumulativeReward = afterRewardFinal;
            if (appendPerIteration) AppendUpdatedStatsSnapshot(batchP1Rate, true, fallFraction, _iteration);
            _lastError = error;
            EndEpisode();
            _batchRunning = false;
            yield break;
        }

        // Log snapshot if not terminal
        if (appendPerIteration) AppendUpdatedStatsSnapshot(batchP1Rate, false, fallFraction, _iteration);

        // Update last error
        _lastError = error;

        // Small time penalty
        AddReward(-0.01f);

        // Compute final reward delta
        float afterReward = GetCumulativeReward();
        _lastRewardDelta = afterReward - beforeReward;
        _lastCumulativeReward = afterReward;

        EndEpisode();
        _batchRunning = false;
    }

    /// <summary>
    /// Poll until either P1 or P2 wins increase (match end detected).
    /// </summary>
    IEnumerator WaitForMatchEnd(int startP1, int startP2)
    {
        while (true)
        {
            int nowP1 = gameManager.GetWins("P1");
            int nowP2 = gameManager.GetWins("P2");
            if (nowP1 > startP1 || nowP2 > startP2) yield break;
            SubscribeInstancesSafe();
            yield return null;
        }
    }

    /// <summary>
    /// Subscribe to Health instances for death-cause tracking. Safe to call repeatedly.
    /// </summary>
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

    /// <summary>
    /// Unsubscribe from all health instances.
    /// </summary>
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

    /// <summary>
    /// Subscribe to P1 health events.
    /// </summary>
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
    }

    /// <summary>
    /// Subscribe to P2 health events.
    /// </summary>
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
    }

    void OnP1Damaged(float remainingHp)
    {
        // Reserved for future use
    }

    void OnP2Damaged(float remainingHp)
    {
        // Reserved for future use
    }

    /// <summary>
    /// Track P1 death cause (fall vs HP).
    /// </summary>
    void OnP1Death()
    {
        bool fell = DidFall(_p1Health);
        if (fell) _p1FallDeathsInBatch++; else _p1HpDeathsInBatch++;
    }

    /// <summary>
    /// Track P2 death cause (fall vs HP).
    /// </summary>
    void OnP2Death()
    {
        bool fell = DidFall(_p2Health);
        if (fell) _p2FallDeathsInBatch++; else _p2HpDeathsInBatch++;
    }

    /// <summary>
    /// Check if a fighter died by falling below the fallDeathY threshold.
    /// </summary>
    bool DidFall(Health h)
    {
        if (h == null || h.gameObject == null || gameManager == null) return false;
        return h.gameObject.transform.position.y <= gameManager.fallDeathY;
    }

    /// <summary>
    /// Adjust attackDamageMultiplier for a player config, clamping to per-class limits.
    /// </summary>
    void AdjustAttackDamageMultiplier(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        float old = cfg.stats.attackDamageMultiplier;
        bool heavy = IsHeavy(cfg);
        float max = heavy ? 2.0f : 1.5f;
        float next = Mathf.Clamp(old * scale, 0.2f, max);
        cfg.stats.attackDamageMultiplier = next;
        cfg.stats.Normalize();
    }

    /// <summary>
    /// Adjust knockbackDealtMultiplier for a player config.
    /// </summary>
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
    }

    /// <summary>
    /// Adjust attackSpeed for a player config and apply to live instance.
    /// </summary>
    void AdjustAttackSpeed(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        bool heavy = IsHeavy(cfg);
        float min = heavy ? 0.5f : 0.1f;
        float max = heavy ? 1.0f : 3.0f;
        float old = cfg.stats.attackSpeed;
        float next = Mathf.Clamp(old * scale, min, max);
        cfg.stats.attackSpeed = next;
        ApplyInstanceStatsIfPresent(cfg);
        cfg.stats.Normalize();
    }

    /// <summary>
    /// Adjust moveSpeed for a player config and apply to live instance.
    /// </summary>
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
    }

    /// <summary>
    /// Adjust maxHp for a player config and apply to live instance (with full heal).
    /// </summary>
    void AdjustMaxHp(PlayerConfig cfg, float scale)
    {
        if (cfg == null || cfg.stats == null) return;
        bool heavy = IsHeavy(cfg);
        float min = heavy ? 100f : 50f;
        float max = heavy ? 800f : 500f;
        float old = cfg.stats.maxHp;
        float next = Mathf.Clamp(old * scale, min, max);
        cfg.stats.maxHp = next;

        // Apply immediate effect to live instances
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

        cfg.stats.Normalize();
        EnforceHpRatio();
    }

    /// <summary>
    /// Check if a PlayerConfig uses HeavyFighterStats.
    /// </summary>
    bool IsHeavy(PlayerConfig cfg)
    {
        if (cfg == null || cfg.stats == null) return false;
        return cfg.stats is HeavyFighterStats;
    }

    /// <summary>
    /// Enforce cross-class HP ratio: Default maxHp should not exceed Heavy maxHp.
    /// Also enforces attack/knockback/speed cross-class constraints.
    /// </summary>
    private void EnforceHpRatio()
    {
        if (player1Config == null || player2Config == null) return;
        if (player1Config.stats == null || player2Config.stats == null) return;

        // Identify heavy vs default
        PlayerConfig heavyCfg = IsHeavy(player1Config) ? player1Config : (IsHeavy(player2Config) ? player2Config : null);
        PlayerConfig defaultCfg = (!IsHeavy(player1Config)) ? player1Config : ((!IsHeavy(player2Config)) ? player2Config : null);

        if (heavyCfg == null || defaultCfg == null) return;

        var heavyStats = heavyCfg.stats;
        var defStats = defaultCfg.stats;

        // Enforce minima
        heavyStats.maxHp = Mathf.Max(heavyStats.maxHp, 100f);
        defStats.maxHp = Mathf.Max(defStats.maxHp, 50f);

        // Cross-class constraints
        // 1) Ensure default's maxHp <= heavy's maxHp
        if (defStats.maxHp > heavyStats.maxHp)
        {
            heavyStats.maxHp = Mathf.Clamp(defStats.maxHp, 100f, 800f);
        }

        // 2) Ensure default's attackDamageMultiplier <= heavy's
        if (defStats.attackDamageMultiplier > heavyStats.attackDamageMultiplier)
        {
            heavyStats.attackDamageMultiplier = Mathf.Clamp(defStats.attackDamageMultiplier, 0.2f, 2.0f);
        }

        // 3) Ensure default's knockbackDealtMultiplier <= heavy's
        if (defStats.knockbackDealtMultiplier > heavyStats.knockbackDealtMultiplier)
        {
            heavyStats.knockbackDealtMultiplier = Mathf.Clamp(defStats.knockbackDealtMultiplier, 0.3f, 2.0f);
        }

        // 4) Ensure heavy's attackSpeed <= 50% of default's
        float maxAllowedHeavyAttackSpeed = defStats.attackSpeed * 0.5f;
        float heavyMin = 0.5f;
        float heavyMax = 1.0f;
        float cappedHeavy = Mathf.Clamp(heavyStats.attackSpeed, heavyMin, heavyMax);
        if (cappedHeavy > maxAllowedHeavyAttackSpeed)
        {
            heavyStats.attackSpeed = Mathf.Clamp(maxAllowedHeavyAttackSpeed, heavyMin, heavyMax);
        }

        // Apply per-class normalization
        heavyStats.Normalize();
        defStats.Normalize();

        // Apply to live instances
        ApplyInstanceStatsIfPresent(player1Config);
        ApplyInstanceStatsIfPresent(player2Config);
    }

    /// <summary>
    /// Normalize a stat value to [-1, 1] range for observations.
    /// </summary>
    float NormalizeStat(float val, float min, float max)
    {
        float denom = Mathf.Max(1e-6f, max - min);
        return Mathf.Clamp01((val - min) / denom) * 2f - 1f;
    }

    /// <summary>
    /// Append a stats snapshot to the output file for logging.
    /// </summary>
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
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AutoBalancerAgent] Failed to append stats: " + ex);
        }
    }

    /// <summary>
    /// Format a PlayerConfig's stats as a string for logging.
    /// </summary>
    string FormatStatsSnapshot(string header, PlayerConfig cfg)
    {
        if (cfg == null) return $"{header}\n  <PlayerConfig null>\n";
        var s = cfg.stats;
        if (s == null) return $"{header}\n  <FighterStats null>\n";
        return $"{header}\n  attackDamageMultiplier = {s.attackDamageMultiplier:F3}\n  knockbackDealtMultiplier = {s.knockbackDealtMultiplier:F3}\n  attackSpeed = {s.attackSpeed:F3}\n  moveSpeed = {s.moveSpeed:F3}\n  maxHp = {s.maxHp:F3}";
    }

#if UNITY_EDITOR
    /// <summary>
    /// Persist ScriptableObject changes to disk (Editor only).
    /// </summary>
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

    /// <summary>
    /// Clean up subscriptions on destroy.
    /// </summary>
    void OnDestroy()
    {
        UnsubscribeInstances();
    }

    /// <summary>
    /// Apply updated stats immediately to a live FighterController instance if present.
    /// </summary>
    void ApplyInstanceStatsIfPresent(PlayerConfig cfg)
    {
        if (cfg == null) return;

        if (cfg == player1Config && _p1Health != null)
        {
            var fc = _p1Health.GetComponent<FighterController>();
            if (fc != null)
            {
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
