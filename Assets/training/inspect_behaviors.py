#!/usr/bin/env python3
"""
Inspect trainer_config.yaml behaviors and attempt to structure each behavior
with the ML-Agents TrainerSettings to surface exactly which key/value is invalid.

Run with the project venv python:
  .\.venv_export\Scripts\python.exe Assets\training\inspect_behaviors.py
"""
import yaml, traceback, sys

try:
    from mlagents.trainers.settings import TrainerSettings
    import cattr
except Exception as ex:
    print("ERROR: Could not import ML-Agents TrainerSettings or cattr:", ex)
    sys.exit(2)

cfg_path = "Assets/training/trainer_config.yaml"
print("Loading:", cfg_path)
with open(cfg_path, "r", encoding="utf-8") as f:
    cfg = yaml.safe_load(f)

if not isinstance(cfg, dict) or "behaviors" not in cfg:
    print("No 'behaviors' section found in YAML.")
    sys.exit(3)

for name, beh in cfg["behaviors"].items():
    print("\n--- Behavior:", name)
    if not isinstance(beh, dict):
        print("  Behavior value is not a mapping:", type(beh))
        continue

    print("  Keys:", list(beh.keys()))
    # Show nested keys for common sections
    for k in ("hyperparameters", "network_settings", "reward_signals"):
        if k in beh:
            print(f"   - {k} keys:", list(beh[k].keys()) if isinstance(beh[k], dict) else type(beh[k]))

    # Try to structure into TrainerSettings to get the exact error
    try:
        obj = cattr.structure(beh, TrainerSettings)
        print("  Structured TrainerSettings OK.")
    except Exception:
        print("  structuring TrainerSettings raised:")
        traceback.print_exc()
        # print the raw behavior dict for inspection
        print("  Raw behavior dict:")
        import pprint
        pprint.pprint(beh, width=120)