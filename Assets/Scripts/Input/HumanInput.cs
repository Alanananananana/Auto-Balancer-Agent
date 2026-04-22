using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Human keyboard input provider implementing IFighterInput.
/// Maps KeyCodes to movement, jump, attack, and block actions for local control and debugging.
/// </summary>
public class HumanInput : MonoBehaviour, IFighterInput
{
    [Header("Keybinds")]
    public KeyCode left = KeyCode.A;
    public KeyCode right = KeyCode.D;
    public KeyCode jump = KeyCode.Space;
    public KeyCode attack = KeyCode.K;
    public KeyCode block = KeyCode.S;

    private float _move;
    public float MoveAxis => _move;
    public bool JumpPressed { get; private set; }
    public bool AttackPressed { get; private set; }
    public bool BlockHeld { get; private set; }

    /// <summary>
    /// Sample input every frame and update IFighterInput state.
    /// </summary>
    void Update()
    {
        _move = 0f;
        if (Input.GetKey(left)) _move -= 1f;
        if (Input.GetKey(right)) _move += 1f;
        JumpPressed = Input.GetKeyDown(jump);
        AttackPressed = Input.GetKeyDown(attack);
        BlockHeld = Input.GetKey(block);
        
        // TEMPORARY DEBUG (remove in production)
        if (_move != 0f || JumpPressed || AttackPressed)
        {
            //Debug.Log($"[HumanInput] _move={_move} jump={JumpPressed} attack={AttackPressed} left={left} right={right}");
        }
    }
}
