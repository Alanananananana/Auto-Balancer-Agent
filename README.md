Unity Version: Unity 6.4 (6000.4.1f1)
Python version: 3.10.3

The cloned repository can run immediately and will use the given pre-trained AI models for fighter A and fighter B, as well as the model for the auto balancer agent.

Every 30 matches (Adjustable) the Auto balancer agent will adjust the stats to make the two fighters as equal as possible while still respecting their fighting archetypes to avoid an identical stalemate.

To train models for yourself, a list of required powershell programs is included in the root folder under "requirements.txt" as well as their respected versions.
STEP 1: Clone the repository into a folder of your choice
Step 2: Install Python version 3.10.3
STEP 3: Right click the root folder and open in terminal.
STEP 4: Paste the following commands into powershell to create a virtual environment and install the necessary packages:

py -m venv .venv

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

.\.venv\Scripts\Activate.ps1


pip install -r requirements.txt


pip install --no-deps onnx==1.21.0

pip install --no-deps onnx-ir==0.2.0

pip install --no-deps onnxscript==0.6.2


STEP 5: To train fighters, remove the models from the prefab fighters and change the Behaviour Type to default. Then uncheck the AutoBalancer object in SampleScene, then run this command

mlagents-learn .\Assets\training\trainer_config.yaml --run-id=run_name

To train the auto balancer, Attach the pretrained models to the fighter prefabs and make sure Behaviour Type is inference (Set up as standard)

mlagents-learn .\Assets\training\auto_balance.yaml --run-id=AutoBalancerRun_name
