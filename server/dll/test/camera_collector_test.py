"""Runs camera_collector.lua (stubbed DCS) and checks the collector's camera JSON.
usage: python camera_collector_test.py   (needs DCS's bin/luae.exe at E:/DCS World)"""
import json, os, subprocess, sys

LUAE = r"E:\DCS World\bin\luae.exe"
here = os.path.dirname(os.path.abspath(__file__))
collector = os.path.join(here, "..", "..", "lua", "collector.lua")
out = subprocess.run([LUAE, os.path.join(here, "camera_collector.lua"), os.path.abspath(collector)],
                     capture_output=True, text=True, timeout=30).stdout.splitlines()
results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + (" | " + str(detail) if detail and not ok else ""))


check("rescan ok", out[0] == "", out[0])
pic, f1, f2 = (json.loads(l) for l in out[1:4])
units = {u["id"]: u for u in pic["units"]}
check("camera_picture: all 4 units of both sides", set(units) == {16777473, 16777999, 501, 777}, list(units))
check("categories mapped", [units[i]["category"] for i in (16777473, 777, 501)] == ["air", "ship", "ground"])
check("player and coalition", units[16777473]["player"] == "Bullat" and units[16777999]["coalition"] == "red")
check("world x/y/z", (units[16777473]["x"], units[16777473]["y"], units[16777473]["z"]) == (-281000.0, 6100.0, 640000.0))
check("heading from velocity", units[16777473]["hdg"] == 270.0 and units[16777999]["hdg"] == 180.0)
w = pic["weapons"]
check("missile with offset id, shooter, target", len(w) == 1 and w[0]["id"] == 1000000001 and w[0]["shooter"] == 16777473
      and w[0]["target"] == 16777999, w)
kinds = {e["kind"]: e for e in pic["events"]}
check("shot and kill events", "shot" in kinds and kinds["kill"]["unit"] == 501 and kinds["kill"]["other"] == 16777473, pic["events"])
check("mission time", pic["time"] == 1240.5 and f1["time"] == 1240.5)
objs = {o["id"]: o for o in f1["objects"]}
check("focus: asked units + missile, unknown id left out", set(objs) == {16777473, 16777999, 1000000001}, list(objs))
v = objs[16777473]
check("focus attitude: west, 45 right bank", (v["hdg"], v["pitch"], v["roll"]) == (270.0, 0.0, 45.0), v)
check("focus velocity", (v["vx"], v["vy"], v["vz"]) == (0.0, 0.0, -231.0), v)
check("focus kinds", objs[1000000001]["kind"] == "weapon" and v["kind"] == "unit")
check("ended missile leaves the focus", [o["id"] for o in f2["objects"]] == [16777473], f2)
print("ALL PASS" if all(results) else f"{results.count(False)} FAILED")
sys.exit(0 if all(results) else 1)
