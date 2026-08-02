# -*- coding: utf-8 -*-
"""探针:从 python 直接拉起 serve worker,检查 list-tag-tables 响应的原始字节编码。"""
import json
import subprocess

EXE = r"D:\Workspace\TiaOpennessWorker\src\TiaOpennessWorker\bin\Debug\net48\TiaOpennessWorker.exe"
AP21 = r"D:/Workspace/TiaOpennessWorker/output/projects/TiaOpennessProj_20260801_180607/TiaOpennessProj_20260801_180607.ap21"

p = subprocess.Popen([EXE, "serve"], stdin=subprocess.PIPE,
                     stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
print("READY:", p.stdout.readline())

for req in [
    {"id": "1", "cmd": "open-project", "args": {"projectFile": AP21}},
    {"id": "2", "cmd": "list-tag-tables"},
    {"id": "3", "cmd": "shutdown"},
]:
    p.stdin.write(json.dumps(req).encode("utf-8") + b"\n")
    p.stdin.flush()
    raw = p.stdout.readline()
    print("RAW BYTES:", raw)
    print("  as utf-8:", raw.decode("utf-8", "replace").strip())
    print("  as gbk  :", raw.decode("gbk", "replace").strip())
