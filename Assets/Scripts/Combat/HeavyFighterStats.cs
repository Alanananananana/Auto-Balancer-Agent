using UnityEngine;

/// <summary>
/// Heavy variant of FighterStats. Do NOT redeclare base fields here.
/// Configure heavy defaults on the asset instance in the Inspector.
/// </summary>
[CreateAssetMenu(menuName = "Combat/HeavyFighterStats")]
public class HeavyFighterStats : FighterStats
{
    // Intentionally empty: reuse fields defined in FighterStats.
}
