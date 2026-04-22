using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Controls a single fighter's movement, attacks, and basic state (stun, block, jump).
/// Designed to be fed input by any IFighterInput provider (HumanInput, FighterAgent, AIInput).
/// Handles velocity-based movement, attack state machine, hitbox management, and ground detection.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class FighterController : MonoBehaviour
{
    [Header("Config")]
    public FighterStats stats;              // ScriptableObject container for movement/attack stats
    public WeaponData weapon;               // Weapon/attack timing and damage configuration
    public Transform visual;                // Sprite root used to flip facing direction
    public bool Blocking => _blocking;      // Public read-only block state

    [Header("Hitbox")]
    public Hitbox hitbox;                   // Child object used for attack collisions
    public Transform weaponVisual;          // Optional sword sprite shown during attack

    [Header("Input Provider")]
    public MonoBehaviour inputSource;       // Must implement IFighterInput at runtime

    [Header("Physics")]
    public float groundNormalY = 0.65f;     // Minimum contact normal.y to treat as ground
    public LayerMask groundMask = ~0;       // Layers considered ground for jumping

    [Header("Demo")]
    [Tooltip("Multiplier applied to horizontal moveSpeed for demo (set < 1 to slow down).")]
    public float demoSpeedMultiplier = 1f;  // Use <1 for demonstration slow-down

    [Header("Movement Mode")]
    [Tooltip("When true, controller sets velocity directly (smoothed). When false, uses AddForce (legacy).")]
    public bool useVelocityControl = true;

    [Header("Attack Movement")]
    [Tooltip("Fraction of top move speed allowed while attacking (0..1)")]
    public float attackMoveSpeedMultiplier = 0.6f;
    [Tooltip("Fraction of acceleration applied while attacking when moving (0..1)")]
    public float attackAccelerationMultiplier = 0.5f;

    [Header("Jump (stability)")]
    [Tooltip("When true, set vertical velocity directly for jumps (consistent across timescale/fixedDeltaTime).")]
    public bool useVelocityJump = true;
    [Tooltip("Target vertical velocity applied when jumping. If 0 or negative, falls back to stats.jumpForce as impulse.")]
    public float jumpVelocity = 0f;
    [Tooltip("Enable debug logging for jump events (prints velocities, gravity, timescale).")]
    public bool logJumpDebug = false;

    // ---- Private runtime state ----
    private IFighterInput _input;
    private Rigidbody2D _rb;
    private Health _health;

    private bool _attacking;
    private bool _recovering;
    private bool _stunned;
    private bool _blocking;
    private float _lastAttackTime;

    // Effective cooldown computed from weapon.cooldown and stats.attackSpeed
    private float _effectiveCooldown = 0.0f;

    // Jump permission tracked per-instance
    private bool _canJump;

    // Track ground contacts (handles multiple colliders)
    private readonly HashSet<Collider2D> _groundColliders = new HashSet<Collider2D>();

    // Track wall contacts (used by AI penalty logic in FighterAgent)
    private readonly HashSet<Collider2D> _wallColliders = new HashSet<Collider2D>();
    public bool IsTouchingWall => _wallColliders.Count > 0;

    // Expose grounded state so Agents can observe it
    public bool IsGrounded => _groundColliders.Count > 0;

    // Facing cache to update hitbox only on changes
    private float _lastFacingSign = 1f;

    // Cache visual base local scale for flipping
    private Vector3 _visualBaseLocalScale = Vector3.one;

    // Cache weapon visual base transforms for mirroring
    private Vector3 _weaponVisualBaseLocalPos;
    private Vector3 _weaponVisualBaseLocalScale;
    private Vector3 _weaponVisualBaseLocalEuler;

    /// <summary>
    /// One-time initialization. Cache components, configure hitbox from WeaponData,
    /// enable interpolation to reduce jitter when velocity is set directly.
    /// </summary>
    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        // Improve visual smoothness when velocity is set directly
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        _health = GetComponent<Health>();
        _input = inputSource as IFighterInput;

        // Initialize instance jump flag from stats
        _canJump = stats != null && stats.canJump;

        // Configure hitbox properties from WeaponData
        if (hitbox != null && weapon != null)
        {
            var box = hitbox.GetComponent<BoxCollider2D>();
            if (box != null)
            {
                box.size = weapon.boxSize;
                box.offset = weapon.localOffset;
            }
            hitbox.damage = weapon.damage;
            hitbox.knockback = weapon.knockback;
            hitbox.owner = transform;
        }

        // Initialize visual base scale
        if (visual != null)
        {
            _visualBaseLocalScale = visual.localScale;
            _lastFacingSign = Mathf.Sign(_visualBaseLocalScale.x != 0f ? _visualBaseLocalScale.x : 1f);
        }

        // Cache weapon visual base transforms
        if (weaponVisual != null)
        {
            _weaponVisualBaseLocalPos = weaponVisual.localPosition;
            _weaponVisualBaseLocalScale = weaponVisual.localScale;
            _weaponVisualBaseLocalEuler = weaponVisual.localEulerAngles;
            weaponVisual.gameObject.SetActive(false);
        }

        // Ensure initial hitbox/weapon visuals are oriented correctly
        UpdateHitboxFacing();

        // Register health event handlers
        if (_health != null)
        {
            _health.OnDamaged += OnDamaged;
            _health.OnDeath += OnHealthDeath;
        }

        // Initialize derived values
        ApplyStats();
    }

    /// <summary>
    /// Handle damage event: apply stun.
    /// </summary>
    private void OnDamaged(float remainingHp)
    {
        StartCoroutine(ApplyStun(stats != null ? stats.stunDuration : 0.25f));
    }

    /// <summary>
    /// Handle death event: disable controller.
    /// </summary>
    private void OnHealthDeath()
    {
        enabled = false;
    }

    /// <summary>
    /// Unsubscribe from health events on destroy to prevent dangling delegates.
    /// </summary>
    private void OnDestroy()
    {
        if (_health != null)
        {
            _health.OnDamaged -= OnDamaged;
            _health.OnDeath -= OnHealthDeath;
        }
    }

    /// <summary>
    /// Handle input sampling, visual flipping, jump, and attack requests.
    /// Reads directly from Input for HumanInput to avoid frame-order race conditions.
    /// </summary>
    void Update()
    {
        // Refresh input reference
        _input = inputSource as IFighterInput;
        if (_input == null) return;

        var humanSource = inputSource as HumanInput;

        // Compute blocking flag (must be grounded, not attacking/stunned)
        if (humanSource != null)
            _blocking = Input.GetKey(humanSource.block) && !_attacking && !_stunned && IsGrounded;
        else
            _blocking = _input.BlockHeld && !_attacking && !_stunned && IsGrounded;

        // Flip visual to face movement direction
        float move = _input.MoveAxis;
        if (Mathf.Abs(move) > 0.01f && visual != null)
        {
            float sign = Mathf.Sign(move);
            visual.localScale = new Vector3(Mathf.Abs(_visualBaseLocalScale.x) * sign, _visualBaseLocalScale.y, _visualBaseLocalScale.z);

            // Update hitbox/weapon facing only when sign changes
            if (!Mathf.Approximately(Mathf.Sign(_lastFacingSign), Mathf.Sign(sign)))
                UpdateHitboxFacing();
        }

        // Handle jump input (prefer direct key sampling for humans)
        bool jumpRequested = false;
        if (humanSource != null)
        {
            jumpRequested = Input.GetKeyDown(humanSource.jump);
        }
        else
        {
            jumpRequested = _input.JumpPressed;
        }

        if (_canJump && jumpRequested && !_stunned)
        {
            PerformJump();
        }

        // Handle attack input (prefer direct key sampling for humans)
        bool attackRequested = false;
        if (humanSource != null)
        {
            attackRequested = Input.GetKeyDown(humanSource.attack);
        }
        else
        {
            attackRequested = _input.AttackPressed;
        }

        if (attackRequested)
        {
            TryAttack();
        }
    }

    /// <summary>
    /// Apply movement physics: smooth velocity control or legacy force-based movement.
    /// Respects attack movement restrictions and demo speed multiplier.
    /// </summary>
    void FixedUpdate()
    {
        if (_stunned) return;

        float inputAxis = _input?.MoveAxis ?? 0f;

        // Determine allowed speed (reduced while attacking)
        float speedMultiplier = _attacking ? Mathf.Clamp01(attackMoveSpeedMultiplier) : 1f;
        float maxSpeed = stats.moveSpeed * speedMultiplier * demoSpeedMultiplier;

        float desired = inputAxis * maxSpeed;
        float currentX = _rb.linearVelocity.x;

        bool tryingToMove = Mathf.Abs(desired) > 0.01f;

        // Use acceleration when moving, deceleration when stopping
        float accel = tryingToMove ? stats.acceleration : stats.deceleration;

        // Reduce acceleration while attacking and moving
        if (_attacking && tryingToMove)
        {
            accel *= Mathf.Clamp01(attackAccelerationMultiplier);
        }

        if (useVelocityControl)
        {
            // Velocity-based movement (smooth, timestep-independent)
            float maxDelta = accel * Time.fixedDeltaTime;
            float newX = Mathf.MoveTowards(currentX, desired, maxDelta);

            // Snap to desired when very close
            if (Mathf.Abs(newX - desired) < 0.01f)
            {
                newX = desired;
            }

            _rb.linearVelocity = new Vector2(newX, _rb.linearVelocity.y);

            // Enforce hard cap while attacking
            if (_attacking)
            {
                float clampedX = Mathf.Clamp(_rb.linearVelocity.x, -maxSpeed, maxSpeed);
                if (!Mathf.Approximately(clampedX, _rb.linearVelocity.x))
                    _rb.linearVelocity = new Vector2(clampedX, _rb.linearVelocity.y);
            }
        }
        else
        {
            // Legacy force-based movement
            float diff = desired - currentX;
            float force = Mathf.Clamp(diff * accel, -accel, accel);
            _rb.AddForce(new Vector2(force, 0f), ForceMode2D.Force);

            if (_attacking)
            {
                float clampedX = Mathf.Clamp(_rb.linearVelocity.x, -maxSpeed, maxSpeed);
                if (!Mathf.Approximately(clampedX, _rb.linearVelocity.x))
                    _rb.linearVelocity = new Vector2(clampedX, _rb.linearVelocity.y);
            }
        }
    }

    /// <summary>
    /// Attempt to begin an attack if cooldown/recovery/stun allow it.
    /// </summary>
    void TryAttack()
    {
        if (_stunned || _recovering) return;

        // Use effective cooldown from stats.attackSpeed
        if (Time.time - _lastAttackTime < _effectiveCooldown) return;
        if (_attacking) return;
        StartCoroutine(AttackRoutine());
    }

    /// <summary>
    /// Attack lifecycle coroutine: startup -> active (enable hitbox) -> recovery.
    /// </summary>
    System.Collections.IEnumerator AttackRoutine()
    {
        _attacking = true;
        _lastAttackTime = Time.time;

        // Startup (windup)
        yield return new WaitForSeconds(weapon.startup);

        // Active frames: enable hitbox and visual
        if (hitbox) hitbox.gameObject.SetActive(true);
        if (weaponVisual) weaponVisual.gameObject.SetActive(true);

        yield return new WaitForSeconds(weapon.active);

        // End active frames
        if (hitbox) hitbox.gameObject.SetActive(false);
        if (weaponVisual) weaponVisual.gameObject.SetActive(false);

        // Recovery (endlag)
        _recovering = true;
        yield return new WaitForSeconds(weapon.recovery);
        _recovering = false;
        _attacking = false;
    }

    /// <summary>
    /// Apply per-instance derived values from stats and weapon.
    /// Call after assigning stats/weapon on a spawned fighter.
    /// Computes effective cooldown, updates hitbox damage, and sets max HP.
    /// </summary>
    public void ApplyStats()
    {
        if (weapon == null) return;

        // Compute effective cooldown from weapon.cooldown and stats.attackSpeed
        if (stats != null && stats.attackSpeed > 0f)
            _effectiveCooldown = weapon.cooldown / stats.attackSpeed;
        else
            _effectiveCooldown = weapon.cooldown;

        // Update hitbox damage with attackDamageMultiplier
        if (hitbox != null)
        {
            float dmgMult = stats != null ? stats.attackDamageMultiplier : 1f;
            hitbox.damage = weapon.damage * dmgMult;
            hitbox.knockback = weapon.knockback;
        }

        // Update jump permission from stats
        _canJump = stats != null ? stats.canJump : true;

        // Apply maxHp from stats to Health component
        if (_health != null && stats != null)
        {
            stats.Normalize();
            _health.maxHp = stats.maxHp;
            _health.HealFull();
        }
    }

    /// <summary>
    /// Perform jump impulse or velocity set based on configuration.
    /// Supports both velocity-based (consistent) and force-based (legacy) jump modes.
    /// </summary>
    private void PerformJump()
    {
        if (useVelocityJump)
        {
            // Velocity-based jump (consistent across timescales)
            float v = (jumpVelocity > 0f) ? jumpVelocity : (stats != null ? stats.jumpForce : 8f);
            if (logJumpDebug)
            {
                Debug.Log($"[Jump] SetVelocity jump v={v:F2} mass={_rb.mass:F2} grav={Physics2D.gravity.y:F2} gravityScale={_rb.gravityScale:F02} timeScale={Time.timeScale:F2} fixedDelta={Time.fixedDeltaTime:F4}", this);
            }
            _rb.linearVelocity = new Vector2(_rb.linearVelocity.x, v);
        }
        else
        {
            // Force-based jump (legacy)
            float jf = stats != null ? stats.jumpForce : 8f;
            if (logJumpDebug)
            {
                Debug.Log($"[Jump] AddForce Impulse force={jf:F2} mass={_rb.mass:F2} timeScale={Time.timeScale:F2}", this);
            }
            _rb.AddForce(Vector2.up * jf, ForceMode2D.Impulse);
        }
        _canJump = false;
    }

    /// <summary>
    /// Apply stun for specified duration. Blocking reduces stun duration.
    /// Triggers temporary invulnerability via Health component.
    /// </summary>
    System.Collections.IEnumerator ApplyStun(float seconds)
    {
        if (_blocking)
        {
            seconds *= 0.4f; // Milder stun while blocking
        }

        _stunned = true;
        if (_health != null)
        {
            StartCoroutine(_health.TempInvuln(stats != null ? stats.invulnAfterHit : 0.2f));
        }
        yield return new WaitForSeconds(seconds);
        _stunned = false;
    }

    /// <summary>
    /// Update hitbox offset and weapon visual to match current facing direction.
    /// Called when visual scale flips.
    /// </summary>
    private void UpdateHitboxFacing()
    {
        if (hitbox == null || weapon == null || visual == null) return;

        var box = hitbox.GetComponent<BoxCollider2D>();
        if (box != null)
        {
            var off = weapon.localOffset;
            off.x = Mathf.Abs(off.x) * Mathf.Sign(visual.localScale.x);
            box.offset = off;

            if (weaponVisual != null)
            {
                // Mirror position across X when facing flips
                weaponVisual.localPosition = new Vector3(off.x, off.y, weaponVisual.localPosition.z);

                // Mirror by flipping X scale
                var vs = _weaponVisualBaseLocalScale;
                vs.x = Mathf.Abs(vs.x) * Mathf.Sign(visual.localScale.x);
                weaponVisual.localScale = vs;

                // Keep base Euler rotation unchanged
                weaponVisual.localEulerAngles = _weaponVisualBaseLocalEuler;
            }
        }

        _lastFacingSign = Mathf.Sign(visual.localScale.x);
    }

    /// <summary>
    /// Detect ground contact and enable jumping when touching ground with valid normal.
    /// </summary>
    void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.collider.isTrigger) return;

        if ((groundMask.value & (1 << collision.collider.gameObject.layer)) == 0) return;

        foreach (var contact in collision.contacts)
        {
            if (contact.normal.y >= groundNormalY)
            {
                _groundColliders.Add(collision.collider);
                _canJump = true;
                break;
            }
        }
    }

    /// <summary>
    /// Track ongoing ground contact and maintain jump permission.
    /// </summary>
    void OnCollisionStay2D(Collision2D collision)
    {
        if (collision.collider.isTrigger) return;

        if ((groundMask.value & (1 << collision.collider.gameObject.layer)) == 0) return;

        foreach (var contact in collision.contacts)
        {
            if (contact.normal.y >= groundNormalY)
            {
                if (!_groundColliders.Contains(collision.collider))
                    _groundColliders.Add(collision.collider);
                _canJump = true;
                return;
            }
        }

        if (_groundColliders.Remove(collision.collider) && _groundColliders.Count == 0)
        {
            _canJump = false;
        }
    }

    /// <summary>
    /// Remove ground contact and disable jumping when no longer touching ground.
    /// </summary>
    void OnCollisionExit2D(Collision2D collision)
    {
        if (collision.collider.isTrigger) return;

        if ((groundMask.value & (1 << collision.collider.gameObject.layer)) == 0) return;

        if (_groundColliders.Remove(collision.collider) && _groundColliders.Count == 0)
        {
            _canJump = false;
        }
    }
}
