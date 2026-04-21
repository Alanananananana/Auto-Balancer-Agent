using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Fighter/PlayerConfig")]
public class PlayerConfig : ScriptableObject
{
    public bool isAI = true;
    public FighterStats stats;
    public WeaponData weapon;

    [Header("Runtime Overrides")]
    [Tooltip("If true, OptionsManager overrides (user-tuned options) are applied to this player's stats at spawn.")]
    public bool applyOptionsOverrides = true;

    [Header("Prefab")]
    [Tooltip("Optional fighter prefab to use for this player. If null, GameManager.fighterPrefab is used.")]
    public GameObject fighterPrefab;
}