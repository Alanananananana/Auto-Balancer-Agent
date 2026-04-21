using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface IFighterInput
{
    float MoveAxis { get; }
    bool JumpPressed { get; }
    bool AttackPressed { get; }
    bool BlockHeld { get; }
}
