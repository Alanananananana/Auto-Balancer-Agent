using System.Collections;
using System.Collections.Generic;
using UnityEngine;

//Simple scripted AI used for debugging and for training opponents when you freeze learning.
//Moves toward target and attacks at close distance.
public class AIInput : MonoBehaviour, IFighterInput
{
    public Transform target;
    public float approachDistance = 2.5f;
    public float attackDistance = 1.4f;
    public float thinkInterval = 0.2f;

    private float _move;
    private bool _attack;
    private float _timer;

    public float MoveAxis => _move;
    public bool JumpPressed => false;
    public bool AttackPressed => _attack;
    public bool BlockHeld => false;

    void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer <= 0f)
        {
            _timer = thinkInterval;
            _attack = false;
            if (!target) return;
            float dx = target.position.x - transform.position.x;
            float adx = Mathf.Abs(dx);
            _move = Mathf.Sign(dx);
            if (adx < 0.3f) _move = 0f;
            if (adx <= attackDistance) _attack = true;
        }
        else
        {
            _attack = false;
        }
    }
}
