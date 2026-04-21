#!/usr/bin/env python3
"""
Validate ML-Agents trainer_config.yaml using the installed mlagents package.
Run with the venv python to get a precise traceback for invalid keys.
"""
import sys, traceback, yaml

try:
    import mlagents
    from mlagents.trainers.settings import RunOptions
except Exception as ex:
    print("ERROR: Could not import mlagents or RunOptions from mlagents.trainers.settings")
    print(ex)
    sys.exit(2)

print("mlagents package:", getattr(mlagents, "__file__", "<no file>"))
cfg_path = "Assets/training/trainer_config.yaml"
print("Loading:", cfg_path)
try:
    with open(cfg_path, "r", encoding="utf-8") as f:
        cfg = yaml.safe_load(f)
    print("YAML parsed. Top-level keys:", list(cfg.keys()) if isinstance(cfg, dict) else type(cfg))
except Exception:
    print("YAML parse error:")
    traceback.print_exc()
    sys.exit(3)

print("Attempting RunOptions.from_dict(...) validation...")
try:
    ro = RunOptions.from_dict(cfg)
    print("RunOptions parsed OK.")
    if hasattr(ro, "trainer_config"):
        print("Behaviors:", list(ro.trainer_config.keys()))
except Exception:
    print("RunOptions.from_dict raised:")
    traceback.print_exc()
    sys.exit(4)