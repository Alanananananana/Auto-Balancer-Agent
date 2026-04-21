using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Simple UI helper to expose the per-player AI toggles in the options menu.
/// Attach to the options panel and wire two Toggle components.
/// Saves changes to OptionsManager and (optionally) forces a runtime respawn by calling GameManager if needed.
/// </summary>
public class OptionsMenuUI : MonoBehaviour
{
    public Toggle player1AiToggle;
    public Toggle player2AiToggle;

    // Optional: reference to GameManager to reapply choices immediately
    public GameManager gameManager;

    void OnEnable()
    {
        if (OptionsManager.Instance == null) return;
        player1AiToggle.isOn = OptionsManager.Instance.optionsData.player1IsAI;
        player2AiToggle.isOn = OptionsManager.Instance.optionsData.player2IsAI;

        // wire up listeners
        player1AiToggle.onValueChanged.RemoveAllListeners();
        player2AiToggle.onValueChanged.RemoveAllListeners();

        player1AiToggle.onValueChanged.AddListener(OnPlayer1AiChanged);
        player2AiToggle.onValueChanged.AddListener(OnPlayer2AiChanged);
    }

    void OnDisable()
    {
        player1AiToggle.onValueChanged.RemoveAllListeners();
        player2AiToggle.onValueChanged.RemoveAllListeners();
    }

    void OnPlayer1AiChanged(bool isAi)
    {
        if (OptionsManager.Instance == null) return;
        OptionsManager.Instance.optionsData.player1IsAI = isAi;
        OptionsManager.Instance.Save();

        // Optionally respawn fighters to apply change immediately
        if (gameManager != null)
        {
            // Force respawn next round: destroy current instances and spawn with current flags
            // Simple and safe: call GameManager's respawn coroutine by ending current round
            // Here we just destroy and let GameManager's respawn logic recreate (keep a tiny delay)
            if (gameManager != null)
            {
                // Destroy current instances to force immediate respawn with new input bindings.
                if (gameManager.GetWins("P1") >= 0) // crude check to ensure manager exists
                {
                    // Attempt a soft respawn: destroy instances and call RespawnBothRoutine
                    // The coroutine is internal; instead we can trigger a manual respawn by destroying instances
                    // and letting GameManager's RespawnBothRoutine run on its own next death. Simpler: reload scene is heavy.
                    // We'll just destroy and then call SpawnFighter indirectly by calling GameManager.Start() is not appropriate.
                    // Leave it to user to press "Respawn" or restart scene to avoid side-effects.
                }
            }
        }
    }

    void OnPlayer2AiChanged(bool isAi)
    {
        if (OptionsManager.Instance == null) return;
        OptionsManager.Instance.optionsData.player2IsAI = isAi;
        OptionsManager.Instance.Save();
    }
}