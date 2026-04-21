using UnityEngine;

/// <summary>
/// Stats for the fighter. Stored as ScriptableObject so multiple fighters can share presets.
/// </summary>
[CreateAssetMenu(menuName = "Combat/FighterStats")]
public class FighterStats : ScriptableObject
{
    [Header("Health")]
    public float maxHp = 100f;

    [Header("Movement")]
    public float moveSpeed = 6f;
    public float acceleration = 60f;
    public float deceleration = 80f;
    public float jumpForce = 8f;
    public bool canJump = true;

    [Header("Combat")]
    public float stunDuration = 0.25f;
    public float invulnAfterHit = 0.2f;
    [Range(0f, 1f)]
    public float blockDamageReduction = 0.6f; // percent damage reduced when blocking
    [Range(0f, 1f)]
    public float knockbackResistance = 0.5f; // additional reduction applied while blocking (0..1)

    [Header("Attack")]
    [Tooltip("Attacks per second. Higher = faster cadence.")]
    public float attackSpeed = 1.0f;
    [Tooltip("Multiplier applied to base attack damage.")]
    public float attackDamageMultiplier = 1.0f;

    [Header("Knockback")]
    [Tooltip("Multiplier applied to the knockback this fighter DEALS (applied to WeaponData.knockback).")]
    public float knockbackDealtMultiplier = 1.0f;
    [Tooltip("Multiplier applied to the knockback this fighter TAKES (applied to incoming knockback). 1 = full, <1 = reduced).")]
    public float knockbackTakenMultiplier = 1.0f;

    /// <summary>
    /// Enforce per-class bounds for stats. Call after changing any stat at runtime or in editor.
    /// Default fighters:
    ///   - maxHp >= 50, <= 500 (asset cap)
    ///   - attackDamageMultiplier <= 1.5
    ///   - knockbackDealtMultiplier <= 1.5
    ///   - attackSpeed <= 3.0
    /// Heavy fighters:
    ///   - maxHp >= 100, <= 800
    ///   - attackDamageMultiplier <= 2.0
    ///   - knockbackDealtMultiplier <= 2.0
    ///   - attackSpeed clamped to [0.5, 1.0]
    /// Note: cross-class HP ratio enforcement (Default <= 75% Heavy) is handled by the balancer code
    /// where both PlayerConfig assets are available.
    /// 
    /// Behavior change:
    /// - When called with enforceRuntimeClamps = false (Inspector / OnValidate), clamps are NOT applied so designers can edit freely.
    /// - At runtime (AI / FighterController / AutoBalancerAgent), calls to Normalize() without a parameter used to enforce clamps (previous behavior).
    ///   For now the clamp code has been intentionally applied only when effectiveEnforce is true.
    /// </summary>
    public virtual void Normalize(bool enforceRuntimeClamps = true)
    {
        // effectiveEnforce respects both the call-site intent and the global GameManager toggle.
        bool effectiveEnforce = enforceRuntimeClamps && GameManager.EnforceRuntimeClampsDefault;

        if (!effectiveEnforce)
        {
            // Do not apply runtime clamps when requested (e.g. editor OnValidate or global toggle OFF).
            // Still ensure certain value sanity (non-negative where clearly required).
            knockbackTakenMultiplier = Mathf.Max(0f, knockbackTakenMultiplier);
            blockDamageReduction = Mathf.Clamp01(blockDamageReduction);
            knockbackResistance = Mathf.Clamp01(knockbackResistance);
            return;
        }

        // Enforce per-class clamps at runtime.
        bool heavy = this is HeavyFighterStats;

        if (heavy)
        {
            maxHp = Mathf.Clamp(maxHp, 100f, 300f);
            attackDamageMultiplier = Mathf.Clamp(attackDamageMultiplier, 1.0f, 3.0f);
            knockbackDealtMultiplier = Mathf.Clamp(knockbackDealtMultiplier, 1.0f, 2.0f);
            attackSpeed = Mathf.Clamp(attackSpeed, 0.5f, 1.0f);
        }
        else
        {
            maxHp = Mathf.Clamp(maxHp, 50f, 200f);
            attackDamageMultiplier = Mathf.Clamp(attackDamageMultiplier, 0.5f, 1.5f);
            knockbackDealtMultiplier = Mathf.Clamp(knockbackDealtMultiplier, 1.0f, 1.5f);
            attackSpeed = Mathf.Clamp(attackSpeed, 1.5f, 3.0f);
        }

        // knockbackTakenMultiplier and other fields
        knockbackTakenMultiplier = Mathf.Max(0f, knockbackTakenMultiplier);
        blockDamageReduction = Mathf.Clamp01(blockDamageReduction);
        knockbackResistance = Mathf.Clamp01(knockbackResistance);
    }

    // Editor-time convenience: avoid applying runtime clamps when editing in the Inspector.
#if UNITY_EDITOR
    void OnValidate()
    {
        // Do NOT enforce runtime clamps when the user edits the asset in the Inspector.
        // This allows designers to set values freely. Runtime code should call Normalize() to enforce limits.
        Normalize(false);
    }
#endif
}
