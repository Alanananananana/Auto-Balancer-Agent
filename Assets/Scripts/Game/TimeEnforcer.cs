using UnityEngine;

/// <summary>
/// Force a sane Time.timeScale every frame. Attach to GameManager (or any always-active object).
/// Use only for demos; ML-Agents trainer will try to set timeScale again while training.
/// </summary>
public class TimeEnforcer : MonoBehaviour
{
    [Tooltip("Target game time scale for demo (1 = realtime).")]
    public float targetTimeScale = 1f;

    [Tooltip("Base fixed delta time (default Unity = 0.02).")]
    public float baseFixedDeltaTime = 0.02f;

    [Tooltip("If true, enforce values every frame (guards against external overrides).")]
    public bool enforceEveryFrame = true;

    void Start()
    {
        Apply();
        Debug.Log($"[TimeEnforcer] Applied timeScale={Time.timeScale:F2}, fixedDeltaTime={Time.fixedDeltaTime:F4}");
    }

    void Update()
    {
        if (enforceEveryFrame) Apply();
    }

    private void Apply()
    {
        Time.timeScale = targetTimeScale;
        Time.fixedDeltaTime = baseFixedDeltaTime * targetTimeScale;
    }
}