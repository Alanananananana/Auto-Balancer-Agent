using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Small menu helper to start training (optional) and begin the match when Play is pressed.
/// Wire the UI Button OnClick to MenuController.OnPlayPressed().
/// </summary>
public class MenuController : MonoBehaviour
{
    [Tooltip("Optional: TrainerLauncher to start training before the match (can be left null).")]
    public TrainerLauncher trainerLauncher;

    [Tooltip("If true, call trainerLauncher.StartTrainer() when Play is pressed.")]
    public bool startTrainerBeforeMatch = false;

    [Tooltip("Delay (seconds) after starting trainer before launching the match. Increase if venv activation is slow.")]
    public float delayAfterTrainerStart = 1.5f;

    [Tooltip("If true, load a scene name. Otherwise, send 'StartMatch' message to gameManager (if assigned).")]
    public bool loadScene = true;

    [Tooltip("Scene name to load when Play is pressed (used when loadScene = true).")]
    public string sceneName = "MatchScene";

    [Tooltip("Scene name to load when Options is pressed.")]
    public string optionsSceneName = "OptionsScene";

    [Tooltip("Optional GameManager reference. When loadScene = false, MenuController will SendMessage('StartMatch') to this object.")]
    public GameObject gameManager;

    /// <summary>
    /// Hook this to your UI Button OnClick.
    /// </summary>
    public void OnPlayPressed()
    {
        if (startTrainerBeforeMatch && trainerLauncher != null)
        {
            trainerLauncher.StartTrainer();
            StartCoroutine(StartMatchDelayed(delayAfterTrainerStart));
        }
        else
        {
            StartMatchImmediate();
        }
    }

    /// <summary>
    /// Hook this to your Options button OnClick. Saves nothing here — Options scene handles save/back.
    /// </summary>
    public void OnOptionsPressed()
    {
        if (string.IsNullOrWhiteSpace(optionsSceneName))
        {
            Debug.LogError("[MenuController] optionsSceneName is empty; cannot load Options scene.");
            return;
        }
        SceneManager.LoadScene(optionsSceneName);
    }

    private IEnumerator StartMatchDelayed(float delay)
    {
        yield return new WaitForSeconds(delay);
        StartMatchImmediate();
    }

    private void StartMatchImmediate()
    {
        if (loadScene)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                Debug.LogError("[MenuController] sceneName is empty; cannot load scene.");
                return;
            }
            SceneManager.LoadScene(sceneName);
        }
        else
        {
            if (gameManager != null)
            {
                // Try to call StartMatch on the GameManager if implemented. Uses SendMessage to avoid compile-time dependency.
                gameManager.SendMessage("StartMatch", SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                Debug.LogWarning("[MenuController] loadScene is false and gameManager is null. Nothing to start.");
            }
        }
    }
}
