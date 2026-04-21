using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(BoxCollider2D))]
public class Hitbox : MonoBehaviour
{
    public LayerMask hurtboxLayers;
    public float damage = 10f;
    public Vector2 knockback = new Vector2(8, 3);
    public Transform owner;
    private BoxCollider2D _col;


    void Awake()
    {
        _col = GetComponent<BoxCollider2D>();
        _col.isTrigger = true;
        gameObject.SetActive(false); // enable only during active frames
    }


    void OnTriggerEnter2D(Collider2D other)
    {
        if (owner && other.transform.IsChildOf(owner)) return; // ignore self

        // If the mask is non-zero, enforce it. If it's zero, treat as "match all".
        if (hurtboxLayers.value != 0 && (((1 << other.gameObject.layer) & hurtboxLayers.value) == 0)) return;

        var hurt = other.GetComponentInParent<Health>();
        if (!hurt) return;

        // If target already dead or invulnerable, ignore
        if (hurt.currentHp <= 0f) return;

        // Check for blocking first so we apply reduced damage once.
        var fc = other.GetComponentInParent<FighterController>();
        float appliedDamage = damage;
        if (fc && fc.Blocking) appliedDamage *= (1f - fc.stats.blockDamageReduction);

        hurt.TakeHit(appliedDamage);

        var rb = other.GetComponentInParent<Rigidbody2D>();
        if (rb)
        {
            // Determine facing/sign to compute knockback direction.
            // Use the attacker's visual facing if available (visual is what we flip),
            // otherwise fall back to owner's localScale sign, then to this transform as last resort.
            float dir = 1f;

            if (owner != null)
            {
                // Prefer FighterController.visual if present
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

            // Compute effective knockback using attacker's and victim's stats
            // Start with weapon-defined knockback (this.knockback)
            Vector2 effectiveKb = knockback;

            // Attacker multiplier (if owner has FighterController)
            FighterController ownerFcRef = null;
            if (owner != null)
            {
                ownerFcRef = owner.GetComponent<FighterController>();
                if (ownerFcRef != null && ownerFcRef.stats != null)
                {
                    effectiveKb *= ownerFcRef.stats.knockbackDealtMultiplier;
                }
            }

            // Victim taken multiplier (if victim has FighterController)
            if (fc != null && fc.stats != null)
            {
                effectiveKb *= fc.stats.knockbackTakenMultiplier;
            }

            // If blocking, apply additional knockback reduction from target's knockbackResistance
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
