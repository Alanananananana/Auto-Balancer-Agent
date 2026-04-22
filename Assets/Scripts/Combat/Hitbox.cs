using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Hitbox component attached to weapon/attack collider.
/// Applies damage and knockback to hurtbox targets on trigger enter.
/// Respects blocking for damage and knockback reduction.
/// Uses owner's and victim's stat multipliers for damage and knockback calculation.
/// </summary>
[RequireComponent(typeof(BoxCollider2D))]
public class Hitbox : MonoBehaviour
{
    public LayerMask hurtboxLayers;
    public float damage = 10f;
    public Vector2 knockback = new Vector2(8, 3);
    public Transform owner;
    private BoxCollider2D _col;

    /// <summary>
    /// Initialize hitbox as trigger, disable by default (enabled during attack active frames).
    /// </summary>
    void Awake()
    {
        _col = GetComponent<BoxCollider2D>();
        _col.isTrigger = true;
        gameObject.SetActive(false);
    }

    /// <summary>
    /// Apply damage and knockback when hitting a valid hurtbox target.
    /// Respects blocking for damage/knockback reduction and applies stat multipliers from both fighters.
    /// </summary>
    void OnTriggerEnter2D(Collider2D other)
    {
        // Ignore self
        if (owner && other.transform.IsChildOf(owner)) return;

        // Enforce layer mask (if set)
        if (hurtboxLayers.value != 0 && (((1 << other.gameObject.layer) & hurtboxLayers.value) == 0)) return;

        var hurt = other.GetComponentInParent<Health>();
        if (!hurt) return;

        // Ignore dead or invulnerable targets
        if (hurt.currentHp <= 0f) return;

        // Check for blocking and apply damage reduction
        var fc = other.GetComponentInParent<FighterController>();
        float appliedDamage = damage;
        if (fc && fc.Blocking) appliedDamage *= (1f - fc.stats.blockDamageReduction);

        hurt.TakeHit(appliedDamage);

        var rb = other.GetComponentInParent<Rigidbody2D>();
        if (rb)
        {
            // Determine knockback direction from attacker's visual facing
            float dir = 1f;

            if (owner != null)
            {
                var ownerFc = owner.GetComponent<FighterController>();
                if (ownerFc != null && ownerFc.visual != null)
                {
                    dir = Mathf.Sign(ownerFc.visual.localScale.x);
                }
                else
                {
                    dir = Mathf.Sign(owner.localScale.x);
                }
            }
            else
            {
                dir = Mathf.Sign(transform.localScale.x);
            }

            // Compute effective knockback using stat multipliers
            Vector2 effectiveKb = knockback;

            // Attacker multiplier
            FighterController ownerFcRef = null;
            if (owner != null)
            {
                ownerFcRef = owner.GetComponent<FighterController>();
                if (ownerFcRef != null && ownerFcRef.stats != null)
                {
                    effectiveKb *= ownerFcRef.stats.knockbackDealtMultiplier;
                }
            }

            // Victim taken multiplier
            if (fc != null && fc.stats != null)
            {
                effectiveKb *= fc.stats.knockbackTakenMultiplier;
            }

            // Blocking knockback reduction
            float kbMultiplier = 1f;
            if (fc != null && fc.Blocking)
            {
                kbMultiplier = 1f - Mathf.Clamp01(fc.stats.knockbackResistance);
            }

#if UNITY_EDITOR
            // Debug output to help diagnose knockback math at runtime (Editor only)
            string attackerName = owner != null ? owner.name : "(none)";
            string victimName = other.transform.root != null ? other.transform.root.name : other.name;
            float ownerMult = (ownerFcRef != null && ownerFcRef.stats != null) ? ownerFcRef.stats.knockbackDealtMultiplier : 1f;
            float victimMult = (fc != null && fc.stats != null) ? fc.stats.knockbackTakenMultiplier : 1f;
            //Debug.Log($"[Hitbox] Attacker='{attackerName}' Victim='{victimName}' " +
            //          $"baseKb={knockback} ownerMult={ownerMult:F2} victimMult={victimMult:F2} resistMult={kbMultiplier:F2} " +
            //          $"finalKb=({effectiveKb.x * kbMultiplier:F2},{effectiveKb.y * kbMultiplier:F2}) dir={dir}", ownerFcRef != null ? ownerFcRef.gameObject : gameObject);
#endif

            // Apply knockback: reset horizontal velocity then add impulse
            rb.linearVelocity = new Vector2(0, rb.linearVelocity.y);
            rb.AddForce(new Vector2(effectiveKb.x * dir, effectiveKb.y) * kbMultiplier, ForceMode2D.Impulse);
        }
    }
}
