using System;

/// <summary>
/// Serializable POCO used to persist adjustable fighter stats between runs.
/// Kept separate from FighterStats to avoid mutating assets.
/// </summary>
[Serializable]
public class OptionsData
{
    public float moveSpeed = 6f;
    public float acceleration = 60f;
    public float deceleration = 80f;
    public float jumpForce = 8f;
    public bool canJump = true;

    public float stunDuration = 0.25f;
    public float invulnAfterHit = 0.2f;
    public float blockDamageReduction = 0.6f;
    public float knockbackResistance = 0.5f;

    // Per-player AI toggles (persisted). True = AI controls the fighter.
    public bool player1IsAI = true;
    public bool player2IsAI = true;
}
