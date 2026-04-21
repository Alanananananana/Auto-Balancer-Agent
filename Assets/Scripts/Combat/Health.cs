using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Health : MonoBehaviour
{
    public float maxHp = 100f;
    public float currentHp;
    public bool IsInvulnerable { get; private set; }

    public System.Action<float> OnDamaged; // passes remaining HP
    public System.Action OnDeath;

    private void Awake() => currentHp = maxHp;

    public void HealFull()
    {
        currentHp = maxHp;
        IsInvulnerable = false;
    }

    // Apply damage unless invulnerable or already dead.
    public void TakeHit(float dmg)
    {
        if (IsInvulnerable || currentHp <= 0f) return;

        currentHp = Mathf.Max(0f, currentHp - dmg);
        OnDamaged?.Invoke(currentHp);

        if (currentHp <= 0f)
        {
            IsInvulnerable = true; // prevent re-invoking death repeatedly
            OnDeath?.Invoke();
        }
    }

    // Small window of invulnerability used by stun interactions.
    public IEnumerator TempInvuln(float seconds)
    {
        IsInvulnerable = true;
        yield return new WaitForSeconds(seconds);
        IsInvulnerable = false;
    }
}
