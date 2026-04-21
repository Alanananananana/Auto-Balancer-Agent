using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

// ML-Agent wrapper that implements IFighterInput so FighterController can read commands.
// Contains observation collection, action handling, reward shaping and simple telemetry.
public class FighterAgent : Agent, IFighterInput
{
    [Header("Agent")]
    public Transform target;                  // Opponent - auto-found if null
    public float maxObsDistance = 10f;        // Used to normalize position obs

    [Header("Training Tuning")]
    [Tooltip("Small negative time penalty each episode start to discourage stalling.")]
    public float timePenalty = -0.001f;
    [Tooltip("Scale applied to reward for closing horizontal distance to the target.")]
    public float distanceRewardScale = 0.05f;
    [Tooltip("Enable runtime logging to Academy stats (visible in TensorBoard).")]
    public bool logStats = true;

    [Header("Movement Smoothing")]
    [Tooltip("Maximum change in move axis per second. Set to 0 for instant (dangerous).")]
    public float moveChangePerSecond = 8f;

    [Header("Wall / Corner Penalty (optional)")]
    [Tooltip("Small periodic penalty when pinned to a wall and not escaping.")]
    public float wallPenalty = 2.0f;
    [Tooltip("Seconds of continuous wall contact before penalties begin.")]
    public float wallGraceSeconds = 0.0f;
    [Tooltip("Minimum horizontal speed toward arena center to avoid penalty.")]
    public float minEscapeSpeed = 0.5f;
    [Tooltip("How often (seconds) to apply wall penalty while pinned.")]
    public float wallPenaltyInterval = 0.0f;
    [Tooltip("Optional arena center transform used to compute escape direction. If null, world origin is used.")]
    public Transform arenaCenter;

    [Header("Center-Seeking Reward")]
    [Tooltip("Small shaping reward scale for moving toward arena center (encourages staying central vs hugging walls).")]
    public float centerRewardScale = 0.05f;

    // IFighterInput state read by FighterController
    private float _moveAxis;
    public float MoveAxis => _moveAxis;
    private float _targetMoveAxis;
    private bool _jumpPressed;
    public bool JumpPressed { get { return _jumpPressed; } }
    private bool _attackPressed;
    public bool AttackPressed { get { return _attackPressed; } }
    private bool _blockHeld;
    public bool BlockHeld => _blockHeld;

    // Component refs
    private FighterController _fighter;
    private Health _health;
    private Health _targetHealth;

    // Reward shaping bookkeeping
    private float _lastHp;
    private float _targetLastHp;
    private float _lastHorizontalDist;

    // Center shaping bookkeeping
    private float _lastCenterDist;

    // Telemetry
    private Unity.MLAgents.StatsRecorder _statsRecorder;
    private int _stepCount; // decision steps (existing metric)

    // NEW: environment steps and episode timer (env steps = FixedUpdate count)
    private int _envStepCount;
    private float _episodeStartTime;

    // Wall / corner penalty timers
    private float _wallTouchTimer;
    private float _lastWallPenaltyTime = -999f;

    // Reset one-frame actions after FighterController reads them
    void LateUpdate()
    {
        _jumpPressed = false;
        _attackPressed = false;
    }

    // Smooth movement in physics timestep
    void FixedUpdate()
    {
        // Count environment (physics) steps for this episode
        if (_episodeStartTime > 0f)
            _envStepCount++;

        if (!Mathf.Approximately(_moveAxis, _targetMoveAxis))
        {
            float maxDelta = moveChangePerSecond * Time.fixedDeltaTime;
            _moveAxis = Mathf.MoveTowards(_moveAxis, _targetMoveAxis, maxDelta);
        }

        // Optional wall penalty handling (uses FighterController.IsTouchingWall)
        if (_fighter != null)
        {
            bool touching = _fighter.IsTouchingWall;
            if (touching) _wallTouchTimer += Time.fixedDeltaTime;
            else _wallTouchTimer = 0f;

            if (_wallTouchTimer >= wallGraceSeconds)
            {
                var rb = GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    Vector3 center = (arenaCenter != null) ? arenaCenter.position : Vector3.zero;
                    float dirToCenter = Mathf.Sign(center.x - transform.position.x);
                    float speedTowardCenter = dirToCenter * rb.linearVelocity.x;
                    bool escaping = speedTowardCenter >= minEscapeSpeed;
                    if (!escaping && Time.time - _lastWallPenaltyTime >= wallPenaltyInterval)
                    {
                        AddReward(-wallPenalty);
                        _lastWallPenaltyTime = Time.time;
                        if (logStats && _statsRecorder != null) _statsRecorder.Add("wall_penalties", 1);
                    }
                }
            }
        }
    }

    public override void Initialize()
    {
        _fighter = GetComponent<FighterController>();
        _health = GetComponent<Health>();
        if (_fighter != null) _fighter.inputSource = this;

        FindTargetIfNeeded();

        if (_health != null) _lastHp = _health.currentHp;
        if (_targetHealth != null) _targetLastHp = _targetHealth.currentHp;

        if (_targetHealth != null) _targetHealth.OnDamaged += OnTargetDamaged;
        if (_health != null) _health.OnDamaged += OnSelfDamaged;

        if (logStats && Academy.Instance != null) _statsRecorder = Academy.Instance.StatsRecorder;
    }

    // Try to find opponent if not assigned (useful in setups with many envs)
    private void FindTargetIfNeeded()
    {
        if (target != null) return;
        var fighters = FindObjectsOfType<FighterController>();
        foreach (var f in fighters)
        {
            if (f.gameObject != gameObject)
            {
                target = f.transform;
                _targetHealth = f.GetComponent<Health>();
                if (_targetHealth != null) _targetLastHp = _targetHealth.currentHp;
                break;
            }
        }
    }

    public override void OnEpisodeBegin()
    {
        FindTargetIfNeeded();

        if (_health != null) _health.HealFull();
        if (_targetHealth != null) _targetLastHp = _targetHealth.currentHp;
        _lastHp = _health != null ? _health.currentHp : 0f;

        // Time penalty to reduce stalling (small)
        AddReward(timePenalty);

        // baseline distance for shaping
        if (target != null) _lastHorizontalDist = Mathf.Abs((target.position - transform.position).x);
        else _lastHorizontalDist = 0f;

        // baseline center distance for center shaping
        Vector3 center = (arenaCenter != null) ? arenaCenter.position : Vector3.zero;
        _lastCenterDist = Mathf.Abs(transform.position.x - center.x);

        // reset telemetry counters / timers
        _stepCount = 0;
        _envStepCount = 0;
        _episodeStartTime = Time.time;

        _targetMoveAxis = _moveAxis;
        _wallTouchTimer = 0f;
        _lastWallPenaltyTime = -999f;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        FindTargetIfNeeded();

        // Relative position normalized
        Vector2 toTarget = Vector2.zero;
        if (target != null) toTarget = (target.position - transform.position);
        sensor.AddObservation(Mathf.Clamp(SafeDiv(toTarget.x, maxObsDistance), -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(SafeDiv(toTarget.y, maxObsDistance), -1f, 1f));

        // Velocity approx normalized
        var rb = GetComponent<Rigidbody2D>();
        float moveSpeed = (_fighter != null && _fighter.stats != null) ? _fighter.stats.moveSpeed : 1f;
        float jumpForce = (_fighter != null && _fighter.stats != null) ? _fighter.stats.jumpForce : 1f;
        sensor.AddObservation(rb != null ? Mathf.Clamp(SafeDiv(rb.linearVelocity.x, moveSpeed), -1f, 1f) : 0f);
        sensor.AddObservation(rb != null ? Mathf.Clamp(SafeDiv(rb.linearVelocity.y, jumpForce * 2f), -1f, 1f) : 0f);

        // Own state flags
        sensor.AddObservation(_fighter != null ? (_fighter.Blocking ? 1f : 0f) : 0f);
        sensor.AddObservation(_fighter != null ? (_fighter.stats != null && _fighter.stats.canJump ? 1f : 0f) : 0f);

        // NEW: grounded flag (helps detect platform contact)
        sensor.AddObservation(_fighter != null ? (_fighter.IsGrounded ? 1f : 0f) : 0f);

        // NEW: normalized vertical position (helps agent know proximity to fall)
        sensor.AddObservation(Mathf.Clamp(SafeDiv(transform.position.y, maxObsDistance), -1f, 1f));

        // HP normalized
        sensor.AddObservation(_health != null ? Mathf.Clamp(SafeDiv(_health.currentHp, _health.maxHp), 0f, 1f) : 0f);
        sensor.AddObservation(_targetHealth != null ? Mathf.Clamp(SafeDiv(_targetHealth.currentHp, _targetHealth.maxHp), 0f, 1f) : 0f);
    }

    // Action spec: 1 continuous (move), 3 discrete branches (attack/jump/block) each size 2.
    public override void OnActionReceived(ActionBuffers actions)
    {
        // store continuous move target, smooth in FixedUpdate
        _targetMoveAxis = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);

        var disc = actions.DiscreteActions;
        _attackPressed = disc[0] == 1;
        _jumpPressed = disc[1] == 1;
        _blockHeld = disc[2] == 1;

        // decision step counter (this is what you already log as episode_length)
        _stepCount++;

        // Distance shaping: reward closing horizontal distance
        if (target != null)
        {
            float currentDist = Mathf.Abs((target.position - transform.position).x);
            float delta = _lastHorizontalDist - currentDist;
            if (!float.IsNaN(delta) && Mathf.Abs(delta) > Mathf.Epsilon)
            {
                AddReward(Mathf.Clamp(distanceRewardScale * delta, -0.05f, 0.05f));
            }
            _lastHorizontalDist = currentDist;
        }

        // Center shaping: reward getting closer to arena center (discourages wall-hugging).
        {
            Vector3 center = (arenaCenter != null) ? arenaCenter.position : Vector3.zero;
            float currentCenterDist = Mathf.Abs(transform.position.x - center.x);
            float centerDelta = _lastCenterDist - currentCenterDist;
            if (!float.IsNaN(centerDelta) && Mathf.Abs(centerDelta) > Mathf.Epsilon)
            {
                // small shaping reward, clamped
                AddReward(Mathf.Clamp(centerRewardScale * centerDelta, -0.02f, 0.02f));
            }
            _lastCenterDist = currentCenterDist;
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var cont = actionsOut.ContinuousActions;
        var disc = actionsOut.DiscreteActions;
        float move = 0f;
        if (Input.GetKey(KeyCode.A)) move -= 1f;
        if (Input.GetKey(KeyCode.D)) move += 1f;
        cont[0] = move;

        disc[0] = Input.GetKeyDown(KeyCode.Space) ? 1 : 0; // attack
        disc[1] = Input.GetKeyDown(KeyCode.W) ? 1 : 0; // jump
        disc[2] = Input.GetKey(KeyCode.LeftShift) ? 1 : 0; // block
    }

    // Reward hooks: called when opponent or self damaged
    private void OnTargetDamaged(float remainingHp)
    {
        float dmg = _targetLastHp - remainingHp;
        if (dmg > 0f) AddReward(0.01f * dmg);
        _targetLastHp = remainingHp;
    }

    private void OnSelfDamaged(float remainingHp)
    {
        float dmg = _lastHp - remainingHp;
        if (dmg > 0f) AddReward(-0.01f * dmg);
        _lastHp = remainingHp;
    }

    // Terminal checks done in Update so they trigger EndEpisode quickly
    void Update()
    {
        if (_targetHealth != null && _targetHealth.currentHp <= 0f)
        {
            AddReward(1.0f);
            if (logStats && _statsRecorder != null)
            {
                // existing metric: decision steps (what you called episode_length)
                _statsRecorder.Add("episode_length", _stepCount);
                _statsRecorder.Add("episode_reward", GetCumulativeReward());
                _statsRecorder.Add("wins", 1);

                // NEW metrics: environment steps and elapsed seconds
                _statsRecorder.Add("episode_env_steps", _envStepCount);
                _statsRecorder.Add("episode_seconds", Time.time - _episodeStartTime);
            }
            EndEpisode();
        }
        if (_health != null && _health.currentHp <= 0f)
        {
            AddReward(-1.0f);
            if (logStats && _statsRecorder != null)
            {
                _statsRecorder.Add("episode_length", _stepCount);
                _statsRecorder.Add("episode_reward", GetCumulativeReward());
                _statsRecorder.Add("losses", 1);

                // NEW metrics: environment steps and elapsed seconds
                _statsRecorder.Add("episode_env_steps", _envStepCount);
                _statsRecorder.Add("episode_seconds", Time.time - _episodeStartTime);
            }
            EndEpisode();
        }
    }

    private void OnDestroy()
    {
        if (_targetHealth != null) _targetHealth.OnDamaged -= OnTargetDamaged;
        if (_health != null) _health.OnDamaged -= OnSelfDamaged;
    }

    // Helper to avoid NaN/Infinity observations when denominators are zero or values invalid.
    private float SafeDiv(float numerator, float denominator)
    {
        if (float.IsNaN(numerator) || float.IsNaN(denominator)) return 0f;
        if (Mathf.Approximately(denominator, 0f)) return 0f;
        float v = numerator / denominator;
        if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
        return v;
    }
}