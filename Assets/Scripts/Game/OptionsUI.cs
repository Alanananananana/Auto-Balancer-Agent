using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Simple UI bridge to edit OptionsData at runtime and persist them.
/// Now uses Sliders for numeric attributes and Text labels for live numeric readouts.
/// Wire the Slider and Text components in the Inspector.
/// Provides a Back button handler that saves options and returns to the main menu.
/// Also contains a demo randomizer button to randomize two PlayerConfig stats for balancing demos.
/// </summary>
public class OptionsUI : MonoBehaviour
{
    public Slider moveSpeed;
    public Text moveSpeedLabel;

    public Slider acceleration;
    public Text accelerationLabel;

    public Slider deceleration;
    public Text decelerationLabel;

    public Slider jumpForce;
    public Text jumpForceLabel;

    public Toggle canJump;

    public Slider stunDuration;
    public Text stunDurationLabel;

    public Slider invulnAfterHit;
    public Text invulnAfterHitLabel;

    public Slider blockDamageReduction;
    public Text blockDamageReductionLabel;

    public Slider knockbackResistance;
    public Text knockbackResistanceLabel;

    [Tooltip("Scene name to return to when Back is pressed. Add scene to Build Settings.")]
    public string returnSceneName = "MainMenu";

    // --- Demo randomization settings (inspector) ---
    [Header("Demo Randomizer")]
    [Tooltip("PlayerConfig for P1 (assign in inspector).")]
    public PlayerConfig player1Config;
    [Tooltip("PlayerConfig for P2 (assign in inspector).")]
    public PlayerConfig player2Config;
    [Tooltip("If true, make P1 the obviously stronger player after randomization; otherwise P2 will be stronger.")]
    public bool randomizeMakeP1Stronger = true;
    [Tooltip("How much to boost the designated stronger player when ensuring they are obviously better (0.0..1.0).")]
    [Range(0f, 1f)]
    public float strongerBoost = 0.25f;
    [Tooltip("If true, persist randomized changes to the FighterStats assets (Editor only).")]
    public bool persistRandomizeToAssets = false;

    void OnEnable()
    {
        RefreshUI();
        SubscribeSliderEvents(true);
    }

    void OnDisable()
    {
        SubscribeSliderEvents(false);
    }

    void SubscribeSliderEvents(bool subscribe)
    {
        if (moveSpeed != null)
        {
            if (subscribe) moveSpeed.onValueChanged.AddListener(OnMoveSpeedChanged);
            else moveSpeed.onValueChanged.RemoveListener(OnMoveSpeedChanged);
        }
        if (acceleration != null)
        {
            if (subscribe) acceleration.onValueChanged.AddListener(OnAccelerationChanged);
            else acceleration.onValueChanged.RemoveListener(OnAccelerationChanged);
        }
        if (deceleration != null)
        {
            if (subscribe) deceleration.onValueChanged.AddListener(OnDecelerationChanged);
            else deceleration.onValueChanged.RemoveListener(OnDecelerationChanged);
        }
        if (jumpForce != null)
        {
            if (subscribe) jumpForce.onValueChanged.AddListener(OnJumpForceChanged);
            else jumpForce.onValueChanged.RemoveListener(OnJumpForceChanged);
        }
        if (stunDuration != null)
        {
            if (subscribe) stunDuration.onValueChanged.AddListener(OnStunDurationChanged);
            else stunDuration.onValueChanged.RemoveListener(OnStunDurationChanged);
        }
        if (invulnAfterHit != null)
        {
            if (subscribe) invulnAfterHit.onValueChanged.AddListener(OnInvulnAfterHitChanged);
            else invulnAfterHit.onValueChanged.RemoveListener(OnInvulnAfterHitChanged);
        }
        if (blockDamageReduction != null)
        {
            if (subscribe) blockDamageReduction.onValueChanged.AddListener(OnBlockDamageReductionChanged);
            else blockDamageReduction.onValueChanged.RemoveListener(OnBlockDamageReductionChanged);
        }
        if (knockbackResistance != null)
        {
            if (subscribe) knockbackResistance.onValueChanged.AddListener(OnKnockbackResistanceChanged);
            else knockbackResistance.onValueChanged.RemoveListener(OnKnockbackResistanceChanged);
        }
    }

    public void RefreshUI()
    {
        var om = OptionsManager.Instance;
        if (om == null) return;
        var o = om.optionsData;

        if (moveSpeed != null) moveSpeed.value = o.moveSpeed;
        if (acceleration != null) acceleration.value = o.acceleration;
        if (deceleration != null) deceleration.value = o.deceleration;
        if (jumpForce != null) jumpForce.value = o.jumpForce;
        if (canJump != null) canJump.isOn = o.canJump;

        if (stunDuration != null) stunDuration.value = o.stunDuration;
        if (invulnAfterHit != null) invulnAfterHit.value = o.invulnAfterHit;
        if (blockDamageReduction != null) blockDamageReduction.value = o.blockDamageReduction;
        if (knockbackResistance != null) knockbackResistance.value = o.knockbackResistance;

        // Update labels to match slider values
        UpdateAllLabels();
    }

    public void SaveFromUI()
    {
        var om = OptionsManager.Instance;
        if (om == null) return;
        var o = om.optionsData;

        o.moveSpeed = moveSpeed != null ? moveSpeed.value : o.moveSpeed;
        o.acceleration = acceleration != null ? acceleration.value : o.acceleration;
        o.deceleration = deceleration != null ? deceleration.value : o.deceleration;
        o.jumpForce = jumpForce != null ? jumpForce.value : o.jumpForce;
        o.canJump = canJump != null ? canJump.isOn : o.canJump;

        o.stunDuration = stunDuration != null ? stunDuration.value : o.stunDuration;
        o.invulnAfterHit = invulnAfterHit != null ? invulnAfterHit.value : o.invulnAfterHit;
        o.blockDamageReduction = blockDamageReduction != null ? blockDamageReduction.value : o.blockDamageReduction;
        o.knockbackResistance = knockbackResistance != null ? knockbackResistance.value : o.knockbackResistance;

        om.Save();
        Debug.Log("[OptionsUI] Saved options.");
    }

    public void ResetToDefaults()
    {
        var om = OptionsManager.Instance;
        if (om == null) return;
        if (om.defaultStats != null)
        {
            var d = om.defaultStats;
            om.optionsData.moveSpeed = d.moveSpeed;
            om.optionsData.acceleration = d.acceleration;
            om.optionsData.deceleration = d.deceleration;
            om.optionsData.jumpForce = d.jumpForce;
            om.optionsData.canJump = d.canJump;

            om.optionsData.stunDuration = d.stunDuration;
            om.optionsData.invulnAfterHit = d.invulnAfterHit;
            om.optionsData.blockDamageReduction = d.blockDamageReduction;
            om.optionsData.knockbackResistance = d.knockbackResistance;

            om.Save();
            RefreshUI();
            Debug.Log("[OptionsUI] Reset options to defaultStats and saved.");
        }
    }

    // --- Demo Randomizer public handler (wire this to a UI Button OnClick) ---
    /// <summary>
    /// Randomize both player configs for a demo: assigns one "heavy" biased build and one "default" biased build,
    /// then ensures the chosen stronger player is obviously better by applying strongerBoost.
    /// </summary>
    public void OnRandomizePlayersPressed()
    {
        if (player1Config == null || player2Config == null)
        {
            Debug.LogWarning("[OptionsUI] Cannot randomize players - assign player1Config and player2Config in inspector.");
            return;
        }

        // Decide which player will be heavy vs default randomly for variety
        bool p1IsHeavy = (Random.value < 0.5f);
        bool p2IsHeavy = !p1IsHeavy;

        // Apply biased randomization
        ApplyBiasRandomization(player1Config, p1IsHeavy);
        ApplyBiasRandomization(player2Config, p2IsHeavy);

        // Ensure one player is obviously stronger
        if (randomizeMakeP1Stronger)
            EnsurePlayerStronger(player1Config, player2Config, strongerBoost);
        else
            EnsurePlayerStronger(player2Config, player1Config, strongerBoost);

        // Persist changes to assets only in Editor if requested (keeps runtime safe)
#if UNITY_EDITOR
        if (persistRandomizeToAssets)
        {
            if (player1Config?.stats != null) { UnityEditor.EditorUtility.SetDirty(player1Config.stats); }
            if (player2Config?.stats != null) { UnityEditor.EditorUtility.SetDirty(player2Config.stats); }
            UnityEditor.AssetDatabase.SaveAssets();
            Debug.Log("[OptionsUI] Randomized player stats persisted to assets (Editor).");
        }
#endif
        Debug.Log($"[OptionsUI] Randomized players. P1 heavy={p1IsHeavy}, P2 heavy={p2IsHeavy}, P1 stronger={randomizeMakeP1Stronger}.");
    }

    // --- Randomization helpers ---

    void ApplyBiasRandomization(PlayerConfig cfg, bool heavyBias)
    {
        if (cfg == null) return;
        var s = cfg.stats;
        if (s == null) return;

        // Random seed per call for variety
        // Heavy bias: slower movement, stronger damage, slower attackSpeed
        if (heavyBias)
        {
            s.moveSpeed = Random.Range(2.0f, 5.0f); // slower than default 6
            s.acceleration = Random.Range(30f, 60f);
            s.deceleration = Random.Range(40f, 70f);
            s.jumpForce = Random.Range(4f, 8f);
            s.canJump = true;

            s.stunDuration = Random.Range(0.15f, 0.35f);
            s.invulnAfterHit = Random.Range(0.12f, 0.30f);
            s.blockDamageReduction = Random.Range(0.3f, 0.75f);

            // Heavy: deals more knockback and damage but is slower to attack
            s.knockbackResistance = Random.Range(0.0f, 0.6f);
            s.knockbackDealtMultiplier = Random.Range(1.0f, 1.8f);
            s.knockbackTakenMultiplier = Random.Range(0.8f, 1.2f);

            s.attackSpeed = Random.Range(0.6f, 0.95f); // slower attacks per second
            s.attackDamageMultiplier = Random.Range(1.1f, 2.0f); // more damage
        }
        else
        {
            // Default bias: quicker movement, faster attacks, moderate damage
            s.moveSpeed = Random.Range(5.5f, 8.5f);
            s.acceleration = Random.Range(50f, 80f);
            s.deceleration = Random.Range(60f, 100f);
            s.jumpForce = Random.Range(6f, 10f);
            s.canJump = true;

            s.stunDuration = Random.Range(0.10f, 0.30f);
            s.invulnAfterHit = Random.Range(0.08f, 0.25f);
            s.blockDamageReduction = Random.Range(0.2f, 0.65f);

            s.knockbackResistance = Random.Range(0.2f, 0.7f);
            s.knockbackDealtMultiplier = Random.Range(0.8f, 1.3f);
            s.knockbackTakenMultiplier = Random.Range(0.6f, 1.0f);

            s.attackSpeed = Random.Range(1.0f, 1.6f); // faster attacks per second
            s.attackDamageMultiplier = Random.Range(0.7f, 1.4f);
        }

        ClampStats(s);
    }

    // Ensures 'strong' player has a noticeably higher composite score than 'other'
    void EnsurePlayerStronger(PlayerConfig strong, PlayerConfig other, float boost)
    {
        if (strong == null || other == null) return;
        var sa = strong.stats;
        var sb = other.stats;
        if (sa == null || sb == null) return;

        float scoreA = CompositeScore(sa);
        float scoreB = CompositeScore(sb);

        // If already stronger by a margin, do nothing. Otherwise apply boost to key offensive/mobility stats.
        float desiredDelta = 0.15f; // require at least ~15% higher composite score
        if ((scoreA - scoreB) / Mathf.Max(0.0001f, scoreB) >= desiredDelta) return;

        // Apply multiplicative boost to selected fields
        sa.moveSpeed = Mathf.Clamp(sa.moveSpeed * (1f + boost), 0.5f, 20f);
        sa.attackSpeed = Mathf.Clamp(sa.attackSpeed * (1f + boost), 0.1f, 5f);
        sa.attackDamageMultiplier = Mathf.Clamp(sa.attackDamageMultiplier * (1f + boost), 0.2f, 5f);
        sa.knockbackDealtMultiplier = Mathf.Clamp(sa.knockbackDealtMultiplier * (1f + boost), 0.3f, 5f);
        sa.knockbackTakenMultiplier = Mathf.Clamp(sa.knockbackTakenMultiplier * (1f - boost * 0.5f), 0.2f, 3.0f); // reduce taken multiplier to resist knockback a bit
        sa.blockDamageReduction = Mathf.Clamp(sa.blockDamageReduction * (1f + boost * 0.5f), 0f, 0.95f);

        // Slightly nerf the other to make the gap clearer
        sb.moveSpeed = Mathf.Clamp(sb.moveSpeed * (1f - boost * 0.5f), 0.2f, 20f);
        sb.attackSpeed = Mathf.Clamp(sb.attackSpeed * (1f - boost * 0.5f), 0.05f, 5f);
        sb.attackDamageMultiplier = Mathf.Clamp(sb.attackDamageMultiplier * (1f - boost * 0.5f), 0.1f, 5f);

        ClampStats(sa);
        ClampStats(sb);
    }

    // Composite score heuristic weighing mobility and offense (used only to decide stronger)
    float CompositeScore(FighterStats s)
    {
        if (s == null) return 0f;
        // Weigh moveSpeed and attackSpeed higher; include damage and knockback dealt modestly.
        return s.moveSpeed * 0.4f + s.attackSpeed * 0.3f + s.attackDamageMultiplier * 0.2f + s.knockbackDealtMultiplier * 0.1f;
    }

    void ClampStats(FighterStats s)
    {
        if (s == null) return;
        s.moveSpeed = Mathf.Clamp(s.moveSpeed, 0.2f, 30f);
        s.acceleration = Mathf.Clamp(s.acceleration, 1f, 500f);
        s.deceleration = Mathf.Clamp(s.deceleration, 1f, 500f);
        s.jumpForce = Mathf.Clamp(s.jumpForce, 0f, 30f);

        s.stunDuration = Mathf.Clamp(s.stunDuration, 0f, 5f);
        s.invulnAfterHit = Mathf.Clamp(s.invulnAfterHit, 0f, 5f);
        s.blockDamageReduction = Mathf.Clamp(s.blockDamageReduction, 0f, 0.95f);
        s.knockbackResistance = Mathf.Clamp(s.knockbackResistance, 0f, 1f);

        s.attackSpeed = Mathf.Clamp(s.attackSpeed, 0.01f, 5f);
        s.attackDamageMultiplier = Mathf.Clamp(s.attackDamageMultiplier, 0.05f, 10f);
        s.knockbackDealtMultiplier = Mathf.Clamp(s.knockbackDealtMultiplier, 0.05f, 10f);
        s.knockbackTakenMultiplier = Mathf.Clamp(s.knockbackTakenMultiplier, 0.05f, 10f);
    }

    // --- Slider change handlers update the numeric readout labels ---

    void UpdateAllLabels()
    {
        if (moveSpeed != null && moveSpeedLabel != null) moveSpeedLabel.text = Format(moveSpeed.value);
        if (acceleration != null && accelerationLabel != null) accelerationLabel.text = Format(acceleration.value);
        if (deceleration != null && decelerationLabel != null) decelerationLabel.text = Format(deceleration.value);
        if (jumpForce != null && jumpForceLabel != null) jumpForceLabel.text = Format(jumpForce.value);

        if (stunDuration != null && stunDurationLabel != null) stunDurationLabel.text = Format(stunDuration.value);
        if (invulnAfterHit != null && invulnAfterHitLabel != null) invulnAfterHitLabel.text = Format(invulnAfterHit.value);
        if (blockDamageReduction != null && blockDamageReductionLabel != null) blockDamageReductionLabel.text = Format(blockDamageReduction.value);
        if (knockbackResistance != null && knockbackResistanceLabel != null) knockbackResistanceLabel.text = Format(knockbackResistance.value);
    }

    string Format(float v)
    {
        return v.ToString("0.##");
    }

    void OnMoveSpeedChanged(float v) { if (moveSpeedLabel != null) moveSpeedLabel.text = Format(v); }
    void OnAccelerationChanged(float v) { if (accelerationLabel != null) accelerationLabel.text = Format(v); }
    void OnDecelerationChanged(float v) { if (decelerationLabel != null) decelerationLabel.text = Format(v); }
    void OnJumpForceChanged(float v) { if (jumpForceLabel != null) jumpForceLabel.text = Format(v); }

    void OnStunDurationChanged(float v) { if (stunDurationLabel != null) stunDurationLabel.text = Format(v); }
    void OnInvulnAfterHitChanged(float v) { if (invulnAfterHitLabel != null) invulnAfterHitLabel.text = Format(v); }
    void OnBlockDamageReductionChanged(float v) { if (blockDamageReductionLabel != null) blockDamageReductionLabel.text = Format(v); }
    void OnKnockbackResistanceChanged(float v) { if (knockbackResistanceLabel != null) knockbackResistanceLabel.text = Format(v); }

    /// <summary>
    /// Handler for the Back button. Saves current options then loads the configured return scene.
    /// Wire the Back button OnClick to this method.
    /// </summary>
    public void OnBackPressed()
    {
        // Persist current UI values
        SaveFromUI();

        // Load return scene (ensure it's added to Build Settings)
        if (string.IsNullOrWhiteSpace(returnSceneName))
        {
            Debug.LogWarning("[OptionsUI] returnSceneName is empty; cannot load scene.");
            return;
        }

        SceneManager.LoadScene(returnSceneName);
    }
}
