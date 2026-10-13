"""Downloads only what is needed from the public AIOps datasets into data/external/aiops (git-ignored, not redistributed).

  Loghub HDFS_v1 (Zenodo): only the two preprocessed files we use, via HTTP range requests (the full zip is ~1.5 GB).
  RCAEval RE2-OB (Hugging Face, MIT): the 90 Online Boutique multi-source cases (~0.9 GB).
"""
from pathlib import Path

from huggingface_hub import snapshot_download
from remotezip import RemoteZip

ROOT = Path(__file__).resolve().parents[2] / "data" / "external" / "aiops"
ROOT.mkdir(parents=True, exist_ok=True)

with RemoteZip("https://zenodo.org/records/8196385/files/HDFS_v1.zip?download=1") as z:
    for name in ["preprocessed/HDFS.log_templates.csv", "preprocessed/Event_traces.csv", "README.md"]:
        z.extract(name, ROOT / "hdfs")

snapshot_download(repo_id="phamquiluan/RCAEval", repo_type="dataset",
                  allow_patterns=["re2ob*", "cases.parquet", "README.md"], local_dir=ROOT / "rcaeval")
print("done")
