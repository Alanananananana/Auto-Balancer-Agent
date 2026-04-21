using System.IO;
using UnityEngine;

/// <summary>
/// Singleton managing persistent options for fighter stats.
/// Saves/loads JSON to Application.persistentDataPath/options.json.
/// Provides helper to create a runtime FighterStats copy with applied overrides.
/// </summary>
public class OptionsManager : MonoBehaviour
{
    public static OptionsManager Instance { get; private set; }

    [Tooltip("Optional base FighterStats used to initialize defaults on first run.")]
    public FighterStats defaultStats;

    public OptionsData optionsData = new OptionsData();

    private string _path;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _path = Path.Combine(Application.persistentDataPath, "options.json");
        Load();
    }

    /// <summary>
    /// Load options from disk. If missing, initialize from defaultStats (if assigned) or keep defaults.
    /// </summary>
    public void Load()
    {
        if (File.Exists(_path))
        {
            try
            {
                var json = File.ReadAllText(_path);
                optionsData = JsonUtility.FromJson<OptionsData>(json) ?? new OptionsData();
                Debug.Log($"[OptionsManager] Loaded options from {_path}");
                return;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[OptionsManager] Failed to read options.json: " + ex);
            }
        }

        // No file or failed to load -> initialize from defaultStats if present
        if (defaultStats != null)
        {
            optionsData.moveSpeed = defaultStats.moveSpeed;
            optionsData.acceleration = defaultStats.acceleration;
            optionsData.deceleration = defaultStats.deceleration;
            optionsData.jumpForce = defaultStats.jumpForce;
            optionsData.canJump = defaultStats.canJump;

            optionsData.stunDuration = defaultStats.stunDuration;
            optionsData.invulnAfterHit = defaultStats.invulnAfterHit;
            optionsData.blockDamageReduction = defaultStats.blockDamageReduction;
            optionsData.knockbackResistance = defaultStats.knockbackResistance;

            // Keep AI defaults true (do not override)
        }

        Save(); // persist defaults
    }

    /// <summary>
    /// Save current options to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var json = JsonUtility.ToJson(optionsData, true);
            File.WriteAllText(_path, json);
            Debug.Log($"[OptionsManager] Saved options to {_path}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[OptionsManager] Failed to write options.json: " + ex);
        }
    }

    /// <summary>
    /// Create a runtime FighterStats instance using optionsData values (so assets aren't mutated).
    /// If baseStats is provided we copy its values first, then optionally override with optionsData values.
    /// Caller is responsible for lifetime (these are created at runtime).
    /// </summary>
    /// <param name="baseStats">Source stats to copy from (can be HeavyFighterStats instance)</param>
    /// <param name="applyOverrides">If true, apply optionsData overrides; set false to preserve baseStats exactly (useful for AI/heavy presets)</param>
    public FighterStats CreateStatsOverride(FighterStats baseStats = null, bool applyOverrides = true)
    {
        var copy = ScriptableObject.CreateInstance<FighterStats>();
        if (baseStats != null)
        {
            copy.moveSpeed = baseStats.moveSpeed;
            copy.acceleration = baseStats.acceleration;
            copy.deceleration = baseStats.deceleration;
            copy.jumpForce = baseStats.jumpForce;
            copy.canJump = baseStats.canJump;

            copy.stunDuration = baseStats.stunDuration;
            copy.invulnAfterHit = baseStats.invulnAfterHit;
            copy.blockDamageReduction = baseStats.blockDamageReduction;
            copy.knockbackResistance = baseStats.knockbackResistance;

            // Preserve attack fields added to FighterStats
            copy.attackSpeed = baseStats.attackSpeed;
            copy.attackDamageMultiplier = baseStats.attackDamageMultiplier;
            copy.knockbackDealtMultiplier = baseStats.knockbackDealtMultiplier;
            copy.maxHp = baseStats.maxHp;
        }

        if (!applyOverrides) return copy;

        // Apply overrides from optionsData
        copy.moveSpeed = optionsData.moveSpeed;
        copy.acceleration = optionsData.acceleration;
        copy.deceleration = optionsData.deceleration;
        copy.jumpForce = optionsData.jumpForce;
        copy.canJump = optionsData.canJump;

        copy.stunDuration = optionsData.stunDuration;
        copy.invulnAfterHit = optionsData.invulnAfterHit;
        copy.blockDamageReduction = optionsData.blockDamageReduction;
        copy.knockbackResistance = optionsData.knockbackResistance;

        return copy;
    }
}
