using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Minimal trainer launcher. Runs the configured command (via PowerShell by default)
/// and optionally starts TensorBoard. No automatic model copying or Editor APIs.
/// Attach to a menu object and wire StartTrainer/StopTrainer to UI buttons.
/// </summary>
public class TrainerLauncher : MonoBehaviour
{
    [Header("Command")]
    [Tooltip("PowerShell script body to run. Use relative paths (like .\\.venv_export) -- the file will be written into Working Directory.")]
    [TextArea(2, 6)]
    // PowerShell body: ensure export venv exists, upgrade pip, then run trainer with venv python
    public string command = @"$root = $PSScriptRoot
$venv = Join-Path $root '.venv_export'
if (-not (Test-Path $venv)) { py -3 -m venv $venv }
$python = Join-Path $venv 'Scripts\python.exe'
if (-not (Test-Path $python)) {
    Write-Error ""venv python not found: $python""
    exit 1
}
# Upgrade pip (safe, idempotent)
& $python -m pip install --upgrade pip
# Optionally ensure mlagents installed (uncomment first-run line)
# & $python -m pip install -U mlagents
# Run trainer (use same run-id / config so exporter can produce ONNX)
& $python -m mlagents_learn '.\Assets\training\trainer_config.yaml' --run-id 'FighterRun10' --force";

    [Tooltip("Working directory where the command should run (trainer config folder). Use full path, e.g. C:\\Users\\...\\Sword-fighting AI")]
    public string workingDirectory = "";

    [Tooltip("Run the command string through PowerShell (recommended on Windows) or run directly (best for direct executables).")]
    public bool usePowerShell = true;

    [Header("TensorBoard (optional)")]
    [Tooltip("If true, will also start TensorBoard after trainer starts.")]
    public bool startTensorboard = false;
    [Tooltip("TensorBoard command string (example: \"tensorboard --logdir results --port 6006\"). If empty, no TB will be started.")]
    public string tensorboardCommand = "tensorboard --logdir results";

    [Tooltip("If true, the trainer process STDOUT/STDERR is logged to Unity Console.")]
    public bool logProcessOutput = true;

    [Header("Temp PS1")]
    [Tooltip("If true, the temporary .ps1 created for the trainer run will be preserved (not deleted) so you can run it manually.")]
    public bool keepTempPs1 = false;

    private Process _trainerProcess;
    private Process _tensorboardProcess;

    /// <summary>
    /// Start the configured trainer. Safe to call from a UI Button __OnClick__.
    /// </summary>
    public void StartTrainer()
    {
        if (_trainerProcess != null && !_trainerProcess.HasExited)
        {
            UnityEngine.Debug.LogWarning("[TrainerLauncher] Trainer already running.");
            return;
        }

        StartTrainerAsync();
    }

    /// <summary>
    /// Stop trainer and tensorboard processes (if running).
    /// </summary>
    public void StopTrainer()
    {
        try
        {
            if (_trainerProcess != null && !_trainerProcess.HasExited)
            {
                _trainerProcess.Kill();
                UnityEngine.Debug.Log("[TrainerLauncher] Killed trainer process.");
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[TrainerLauncher] Error stopping trainer: " + e);
        }

        try
        {
            if (_tensorboardProcess != null && !_tensorboardProcess.HasExited)
            {
                _tensorboardProcess.Kill();
                UnityEngine.Debug.Log("[TrainerLauncher] Killed TensorBoard process.");
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[TrainerLauncher] Error stopping TensorBoard: " + e);
        }
    }

    // Implementation -------------------------------------------

    private async void StartTrainerAsync()
    {
        try
        {
            // Use explicit project root as default working dir (parent of Assets) so temp ps1 and venv paths
            // match manual console runs. If workingDirectory is set in the Inspector, use that instead.
            var defaultCwd = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? defaultCwd : workingDirectory;
            UnityEngine.Debug.Log($"[TrainerLauncher] Starting trainer: usePowerShell={usePowerShell} cwd='{cwd}'");

            if (usePowerShell)
            {
                // Write a temporary .ps1 file into the working directory and execute via -File
                string ps1Path = CreateTempPs1(cwd, command);
                if (string.IsNullOrEmpty(ps1Path))
                {
                    UnityEngine.Debug.LogError("[TrainerLauncher] Failed to create temporary ps1 script.");
                    return;
                }

                // Log ps1 path and a short preview so you can run it manually and verify
                UnityEngine.Debug.Log($"[TrainerLauncher] Wrote temp ps1: {ps1Path}");
                try
                {
                    var preview = File.ReadAllText(ps1Path);
                    UnityEngine.Debug.Log($"[TrainerLauncher] Temp ps1 preview:\n{preview.Substring(0, Math.Min(200, preview.Length))}");
                }
                catch { /* ignore preview failures */ }

                var psArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"";
                _trainerProcess = StartProcess("powershell", psArgs, cwd);

                // Ensure temp file removed after process exits (unless user requested preservation)
                if (_trainerProcess != null)
                {
                    _trainerProcess.EnableRaisingEvents = true;
                    _trainerProcess.Exited += (s, e) =>
                    {
                        try
                        {
                            if (File.Exists(ps1Path))
                            {
                                if (keepTempPs1)
                                {
                                    UnityEngine.Debug.Log($"[TrainerLauncher] Preserving temp ps1 (keepTempPs1=true): {ps1Path}");
                                }
                                else
                                {
                                    File.Delete(ps1Path);
                                    UnityEngine.Debug.Log($"[TrainerLauncher] Deleted temp ps1: {ps1Path}");
                                }
                            }
                        }
                        catch (Exception ex) { UnityEngine.Debug.LogWarning("[TrainerLauncher] Could not delete temp ps1: " + ex); }
                    };
                }
            }
            else
            {
                // Non-PowerShell path: split command for direct exec
                var parts = SplitCommand(command);
                if (parts.Length == 0)
                {
                    UnityEngine.Debug.LogError("[TrainerLauncher] Command string empty.");
                    return;
                }
                string exe = parts[0];
                string args = parts.Length > 1 ? command.Substring(parts[0].Length).Trim() : "";
                _trainerProcess = StartProcess(exe, args, cwd);
            }

            if (_trainerProcess == null)
            {
                UnityEngine.Debug.LogError("[TrainerLauncher] Failed to start trainer process.");
                return;
            }

            if (startTensorboard && !string.IsNullOrWhiteSpace(tensorboardCommand))
            {
                await Task.Delay(1500);
                StartTensorBoard();
            }

            // Await process exit asynchronously (do not block main thread)
            await Task.Run(() => _trainerProcess.WaitForExit());

            UnityEngine.Debug.Log("[TrainerLauncher] Trainer process exited with code: " + _trainerProcess.ExitCode);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("[TrainerLauncher] Exception launching trainer: " + ex);
        }
    }

    private string CreateTempPs1(string workingDir, string body)
    {
        try
        {
            Directory.CreateDirectory(workingDir); // no-op if exists
            string name = $"UnityTrainer_{Guid.NewGuid():N}.ps1";
            string path = Path.Combine(workingDir, name);
            File.WriteAllText(path, body, Encoding.UTF8);
            UnityEngine.Debug.Log($"[TrainerLauncher] Wrote temp ps1: {path}");
            return path;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[TrainerLauncher] Failed to write temp ps1: " + e);
            return null;
        }
    }

    private Process StartProcess(string fileName, string arguments, string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = logProcessOutput,
                RedirectStandardError = logProcessOutput,
                CreateNoWindow = true,
                WorkingDirectory = workingDir
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            if (logProcessOutput)
            {
                proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.Log("[Trainer STDOUT] " + e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.LogError("[Trainer STDERR] " + e.Data); };
            }

            proc.Start();

            if (logProcessOutput)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }

            return proc;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[TrainerLauncher] Failed to start process: " + e);
            return null;
        }
    }

    private void StartTensorBoard()
    {
        try
        {
            var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? Application.dataPath : workingDirectory;
            var args = $"-NoProfile -ExecutionPolicy Bypass -Command \"{tensorboardCommand}\"";
            _tensorboardProcess = StartProcess("powershell", args, cwd);
            UnityEngine.Debug.Log("[TrainerLauncher] Started TensorBoard (if available).");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[TrainerLauncher] Could not start TensorBoard: " + e);
        }
    }

    // Very simple space-aware split (keeps quoted parts intact).
    private string[] SplitCommand(string cmd)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        bool inQuote = false;
        foreach (var c in cmd)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts.ToArray();
    }

    void OnApplicationQuit()
    {
        StopTrainer();
    }
}
