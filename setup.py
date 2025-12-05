#!/usr/bin/env python3

import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

xml_path = Path("repos.xml")

if not xml_path.exists():
    print("Error: repos.xml not found")
    exit(1)

root = ET.parse(xml_path).getroot()

for repo in root.findall("repo"):
    name = repo.get("name", "(unnamed)")
    url = repo.get("url")
    path = repo.get("path")
    branch = repo.get("branch", "main")
    visibility = repo.get("visibility", "public").lower()
    
    if not url or not path:
        print(f"Skipping {name}: missing url or path")
        continue
    
    if visibility == "private":
        print(f"Skipping {name}: private repo")
        continue
    
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
        print(f"  Failed: {result.stderr.strip()}")
    else:
        print(f"  Done")