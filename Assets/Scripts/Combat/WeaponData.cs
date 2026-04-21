using UnityEngine;

/// <summary>
/// Weapon configuration data used to configure Hitbox and attack timings.
/// Preferably stored as ScriptableObject assets.
/// </summary>
[CreateAssetMenu(menuName = "Combat/WeaponData")]
public class WeaponData : ScriptableObject
{
    public Vector2 boxSize = new Vector2(1f, 1f);
    public Vector2 localOffset = Vector2.zero;
    public float damage = 10f;
    public Vector2 knockback = new Vector2(8f, 3f);

    [Header("Timing (seconds)")]
    public float startup = 0.08f;
    public float active = 0.12f;
    public float recovery = 0.3f;
    public float cooldown = 0.5f;
}
