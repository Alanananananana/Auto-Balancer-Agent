using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Controls a single fighter's movement, attacks, and basic state (stun, block, jump).
/// Designed to be fed input by any IFighterInput provider (HumanInput, FighterAgent, AIInput).
/// This file is documented and mildly refactored for readability — behavior is unchanged except
/// for an optional velocity-based jump mode to make jump height consistent across timeScale changes.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class FighterController : MonoBehaviour
{
    [Header("Config")]
    public FighterStats stats;              // ScriptableObject-like container for movement/attack numbers
    public WeaponData weapon;               // Weapon/attack timing and damage
    public Transform visual;                // Sprite root used to flip facing
    public bool Blocking => _blocking;      // Public read-only block state

    [Header("Hitbox")]
    public Hitbox hitbox;                   // Child object used for attack collisions
    public Transform weaponVisual;          // Optional sword sprite to show during attack

    [Header("Input Provider")]
    public MonoBehaviour inputSource;       // Must implement IFighterInput at runtime

    [Header("Physics")]
    public float groundNormalY = 0.65f;     // Minimum contact normal.y to treat as ground
    public LayerMask groundMask = ~0;       // Layers considered ground for jumping

    [Header("Demo")]
    [Tooltip("Multiplier applied to horizontal moveSpeed for demo (set < 1 to slow down).")]
    public float demoSpeedMultiplier = 1f;  // Use <1 for demonstration slow-down; keep 1.0 for training

    [Header("Movement Mode")]
    [Tooltip("When true, controller sets velocity directly (smoothed). When false, uses AddForce (legacy).")]
    public bool useVelocityControl = true;  // Toggle to revert to force-based movement if desired

    [Header("Attack Movement")]
    [Tooltip("Fraction of top move speed allowed while attacking (0..1)")]
    public float attackMoveSpeedMultiplier = 0.6f;
    [Tooltip("Fraction of acceleration applied while attacking when moving (0..1)")]
    public float attackAccelerationMultiplier = 0.5f;

    [Header("Jump (stability)")]
    [Tooltip("When true, set vertical velocity directly for jumps (consistent across timescale/fixedDeltaTime).")]
    public bool useVelocityJump = true;
    [Tooltip("Target vertical velocity applied when jumping. If 0 or negative, falls back to stats.jumpForce as impulse amount.")]
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

    // Jump permission is tracked per-instance (do not mutate shared ScriptableObject)
    private bool _canJump;

    // Track ground contacts (handles multiple colliders touching)
    private readonly HashSet<Collider2D> _groundColliders = new HashSet<Collider2D>();

    // Track wall contacts (used by AI penalty logic in FighterAgent)
    private readonly HashSet<Collider2D> _wallColliders = new HashSet<Collider2D>();
    public bool IsTouchingWall => _wallColliders.Count > 0;

    // New: expose grounded state so Agents can observe it
    public bool IsGrounded => _groundColliders.Count > 0;

    // Facing cache so we only update hitbox offsets on facing changes
    private float _lastFacingSign = 1f;

    // Cache visual base local scale so flipping preserves sprite scale
    private Vector3 _visualBaseLocalScale = Vector3.one;

    // Cache weapon visual base transforms so we can mirror/rotate correctly
    private Vector3 _weaponVisualBaseLocalPos;
    private Vector3 _weaponVisualBaseLocalScale;
    private Vector3 _weaponVisualBaseLocalEuler;

    // ---- Unity messages ----

    /// <summary>
    /// One-time initialization. Cache components, configure hitbox from WeaponData,
    /// enable interpolation to reduce jitter when velocity is set directly.
    /// </summary>
    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        // Improve visual smoothness when we set velocity directly in FixedUpdate.
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        _health = GetComponent<Health>();
        _input = inputSource as IFighterInput;

        // initialize instance jump flag from stats default
        _canJump = stats != null && stats.canJump;

        // Configure hitbox properties from the assigned WeaponData (if present)
        if (hitbox != null && weapon != null)
        {
            var box = hitbox.GetComponent<BoxCollider2D>();
            if (box != null)
            {
                box.size = weapon.boxSize;
                box.offset = weapon.localOffset;
            }
            // Set base values; ApplyStats will override damage/cooldown if stats provided
            hitbox.damage = weapon.damage;
            hitbox.knockback = weapon.knockback;
            hitbox.owner = transform;
        }

        // Initialize visual base scale (preserve prefab scaling)
        if (visual != null)
        {
            _visualBaseLocalScale = visual.localScale;
            _lastFacingSign = Mathf.Sign(_visualBaseLocalScale.x != 0f ? _visualBaseLocalScale.x : 1f);
        }

        // Cache weapon visual base transforms (for mirroring/rotation)
        if (weaponVisual != null)
        {
            _weaponVisualBaseLocalPos = weaponVisual.localPosition;
            _weaponVisualBaseLocalScale = weaponVisual.localScale;
            _weaponVisualBaseLocalEuler = weaponVisual.localEulerAngles;
            weaponVisual.gameObject.SetActive(false);
        }

        // Ensure initial hitbox/weapon visuals are oriented correctly
        UpdateHitboxFacing();

        // Register to health events for stun/death handling using named handlers
        if (_health != null)
        {
            _health.OnDamaged += OnDamaged;
            _health.OnDeath += OnHealthDeath;
        }

        // Initialize derived values if stats/weapon are already assigned
        ApplyStats();
    }

    // Named handler previously wired as anonymous lambda
    private void OnDamaged(float remainingHp)
    {
        StartCoroutine(ApplyStun(stats != null ? stats.stunDuration : 0.25f));
    }

    // Named handler previously wired as anonymous lambda
    private void OnHealthDeath()
    {
        // Disables the controller when Health triggers death
        enabled = false;
    }

    // Ensure we unsubscribe so destroyed instances don't leave dangling delegates
    private void OnDestroy()
    {
        if (_health != null)
        {
            _health.OnDamaged -= OnDamaged;
            _health.OnDeath -= OnHealthDeath;
        }
    }

    void Update()
    {
        // Ensure input reference (inputSource may be assigned at runtime)
        if (_input == null) _input = inputSource as IFighterInput;
        if (_input == null) return;

        // Give priority to direct HumanInput sampling to avoid frame-order race with HumanInput.Update.
        var humanSource = inputSource as HumanInput;

        // Compute blocking flag (blocking reduces damage, handled elsewhere).
        // Prefer reading the actual key for human input to avoid race with execution order.
        if (humanSource != null)
            _blocking = Input.GetKey(humanSource.block) && !_attacking && !_stunned && IsGrounded;
        else
            _blocking = _input.BlockHeld && !_attacking && !_stunned && IsGrounded;

        // Flip the visual to face movement direction (if any)
        float move = _input.MoveAxis;
        if (Mathf.Abs(move) > 0.01f && visual != null)
        {
            float sign = Mathf.Sign(move);
            // Preserve the original visual scale in Y/Z and preserve magnitude in X while flipping sign
            visual.localScale = new Vector3(Mathf.Abs(_visualBaseLocalScale.x) * sign, _visualBaseLocalScale.y, _visualBaseLocalScale.z);

            // Update hitbox/weapon facing only when sign changes
            if (!Mathf.Approximately(Mathf.Sign(_lastFacingSign), Mathf.Sign(sign)))
                UpdateHitboxFacing();
        }

        // Handle jump input: prefer direct human key sampling to avoid missing GetKeyDown when spamming.
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

        // Handle attack input: prefer direct human key sampling to avoid frame-order races.
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

    void FixedUpdate()
    {
        if (_stunned) return;

        float inputAxis = _input?.MoveAxis ?? 0f;

        // Determine allowed speed while attacking (reduced) or normal otherwise
        float speedMultiplier = _attacking ? Mathf.Clamp01(attackMoveSpeedMultiplier) : 1f;

        // Apply demo multiplier here to slow horizontal movement for demos.
        float maxSpeed = stats.moveSpeed * speedMultiplier * demoSpeedMultiplier;

        // Desired velocity this frame
        float desired = inputAxis * maxSpeed;
        float currentX = _rb.linearVelocity.x;

        bool tryingToMove = Mathf.Abs(desired) > 0.01f;

        // Use acceleration when actively moving, deceleration when stopping
        float accel = tryingToMove ? stats.acceleration : stats.deceleration;

        // When attacking and trying to move, reduce acceleration so player can still steer but not gain full momentum
        if (_attacking && tryingToMove)
        {
            accel *= Mathf.Clamp01(attackAccelerationMultiplier);
        }

        if (useVelocityControl)
        {
            // Treat accel as the max change in velocity per second (units/s^2 approximated).
            float maxDelta = accel * Time.fixedDeltaTime;
            float newX = Mathf.MoveTowards(currentX, desired, maxDelta);

            // Snap to desired when very close to avoid tiny oscillations
            if (Mathf.Abs(newX - desired) < 0.01f)
            {
                newX = desired;
            }

            _rb.linearVelocity = new Vector2(newX, _rb.linearVelocity.y);

            // Enforce hard cap when attacking (prevent external forces exceeding allowed attack max)
            if (_attacking)
            {
                float clampedX = Mathf.Clamp(_rb.linearVelocity.x, -maxSpeed, maxSpeed);
                if (!Mathf.Approximately(clampedX, _rb.linearVelocity.x))
                    _rb.linearVelocity = new Vector2(clampedX, _rb.linearVelocity.y);
            }
        }
        else
        {
            // Legacy force-based behaviour (kept for compatibility)
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

    // ---- Attack handling ----

    /// <summary>
    /// Attempts to begin an attack if cooldown/recovery/stun allow it.
    /// </summary>
    void TryAttack()
    {
        if (_stunned || _recovering) return;

        // Use effective cooldown derived from stats.attackSpeed (fallback to weapon.cooldown if ApplyStats not called)
        if (Time.time - _lastAttackTime < _effectiveCooldown) return;
        if (_attacking) return;
        StartCoroutine(AttackRoutine());
    }

    /// <summary>
    /// Attack lifecycle coroutine: startup -> active (enable hitbox) -> recovery.
    /// </summary>
    IEnumerator AttackRoutine()
    {
        _attacking = true;
        _lastAttackTime = Time.time;

        // Startup (windup)
        yield return new WaitForSeconds(weapon.startup);

        // Active frames: enable hitbox and optional visual
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
    /// Call this after assigning stats and weapon on a spawned fighter.
    /// </summary>
    public void ApplyStats()
    {
        if (weapon == null) return;

        // Compute effective cooldown from configured weapon.cooldown and stats.attackSpeed
        if (stats != null && stats.attackSpeed > 0f)
            _effectiveCooldown = weapon.cooldown / stats.attackSpeed;
        else
            _effectiveCooldown = weapon.cooldown;

        // Update hitbox damage to respect attackDamageMultiplier (do not mutate shared WeaponData)
        if (hitbox != null)
        {
            float dmgMult = stats != null ? stats.attackDamageMultiplier : 1f;
            hitbox.damage = weapon.damage * dmgMult;

            // Keep knockback set from weapon; victim knockback resistance still applies in Hitbox logic
            hitbox.knockback = weapon.knockback;
        }

        // Update jump permission from stats (ensure runtime reflects current stats)
        _canJump = stats != null ? stats.canJump : true;

        // NEW: apply maxHp from stats to this instance's Health component and heal to full
        if (_health != null && stats != null)
        {
            // Ensure asset stats are within allowed ranges before applying
            stats.Normalize();

            _health.maxHp = stats.maxHp;
            _health.HealFull();
        }
    }

    /// <summary>
    /// Performs the jump impulse/velocity set used by the controller.
    /// Separated to avoid duplicating the logic above.
    /// </summary>
    private void PerformJump()
    {
        if (useVelocityJump)
        {
            float v = (jumpVelocity > 0f) ? jumpVelocity : (stats != null ? stats.jumpForce : 8f);
            if (logJumpDebug)
            {
                Debug.Log($"[Jump] SetVelocity jump v={v:F2} mass={_rb.mass:F2} grav={Physics2D.gravity.y:F2} gravityScale={_rb.gravityScale:F02} timeScale={Time.timeScale:F2} fixedDelta={Time.fixedDeltaTime:F4}", this);
            }
            _rb.linearVelocity = new Vector2(_rb.linearVelocity.x, v);
        }
        else
        {
            float jf = stats != null ? stats.jumpForce : 8f;
            if (logJumpDebug)
            {
                Debug.Log($"[Jump] AddForce Impulse force={jf:F2} mass={_rb.mass:F2} timeScale={Time.timeScale:F2}", this);
            }
            _rb.AddForce(Vector2.up * jf, ForceMode2D.Impulse);
        }
        _canJump = false;
    }

    IEnumerator ApplyStun(float seconds)
    {
        if (_blocking)
        {
            seconds *= 0.4f; // milder stun while blocking
        }

        _stunned = true;
        if (_health != null)
        {
            StartCoroutine(_health.TempInvuln(stats != null ? stats.invulnAfterHit : 0.2f));
        }
        yield return new WaitForSeconds(seconds);
        _stunned = false;
    }

    // ---- Hitbox / visual facing helper ----

    /// <summary>
    /// Updates hitbox offset and weapon visual to match current facing direction.
    /// Call when visual scale flips.
    /// </summary>
    private void UpdateHitboxFacing()
    {
        if (hitbox == null || weapon == null || visual == null) return;

        var box = hitbox.GetComponent<BoxCollider2D>();
        if (box != null)
        {
            var off = weapon.localOffset; // copy
            off.x = Mathf.Abs(off.x) * Mathf.Sign(visual.localScale.x);
            box.offset = off;

            if (weaponVisual != null)
            {
                // mirror position across X when facing flips
                weaponVisual.localPosition = new Vector3(off.x, off.y, weaponVisual.localPosition.z);

                // Mirror by flipping X scale instead of rotating the sprite.
                // Preserve original base scale's Y/Z and magnitude of X, flip sign to mirror.
                var vs = _weaponVisualBaseLocalScale;
                vs.x = Mathf.Abs(vs.x) * Mathf.Sign(visual.localScale.x);
                weaponVisual.localScale = vs;

                // NOTE: keep the weapon's base Euler rotation unchanged to avoid Z-rotation jumps.
                weaponVisual.localEulerAngles = _weaponVisualBaseLocalEuler;
            }
        }

        _lastFacingSign = Mathf.Sign(visual.localScale.x);
    }

    // ---- Ground / wall collision detection ----

    void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.collider.isTrigger) return;

        //if (collision.collider.CompareTag("wall"))
        //{
        //    _wallColliders.Add(collision.collider);
        //}

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

    void OnCollisionStay2D(Collision2D collision)
    {
        if (collision.collider.isTrigger) return;

        //if (collision.collider.CompareTag("wall"))
        //{
        //    if (!_wallColliders.Contains(collision.collider))
        //        _wallColliders.Add(collision.collider);
        //}

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

    void OnCollisionExit2D(Collision2D collision)
    {
        if (collision.collider.isTrigger) return;

        //if (collision.collider.CompareTag("wall"))
        //{
        //    _wallColliders.Remove(collision.collider);
        //}

        if ((groundMask.value & (1 << collision.collider.gameObject.layer)) == 0) return;

        if (_groundColliders.Remove(collision.collider) && _groundColliders.Count == 0)
        {
            _canJump = false;
        }
    }
}
