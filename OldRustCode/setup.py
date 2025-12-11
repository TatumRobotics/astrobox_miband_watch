#!/usr/bin/env python3

import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

xml_path = Path("repos.xml")
root = ET.parse(xml_path).getroot()

for repo in root.findall("repo"):
    name = repo.get("name")
    url = repo.get("url")
    path = repo.get("path")
    branch = repo.get("branch", "main")    
    target = Path(path)
    
    if target.exists():
        print(f"Skipping {name}: {path} already exists")
        continue
    
    print(f"Cloning {name} -> {path}")
    
    target.parent.mkdir(parents=True, exist_ok=True)
    
    result = subprocess.run(
        ["git", "clone", "--branch", branch, "--single-branch", url, str(target)],
        capture_output=True,
        text=True
    )
    
    if result.returncode != 0:
        print(f"Failed: {result.stderr.strip()}")
    else:
        print("Done")
