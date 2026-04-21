using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Human keyboard input mapped to IFighterInput for local control and debugging.
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

    void Update()
    {
        _move = 0f;
        if (Input.GetKey(left)) _move -= 1f;
        if (Input.GetKey(right)) _move += 1f;
        JumpPressed = Input.GetKeyDown(jump);
        AttackPressed = Input.GetKeyDown(attack);
        BlockHeld = Input.GetKey(block);
    }
}
