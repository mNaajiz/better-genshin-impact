"""Converts the community AutoCommissionNova route data (babalae/bettergi-scripts-list,
repo/js/AutoCommissionNova/process/<region>, GPL-3.0) into one compact JSON for StoryPilot.

Usage: python import_commissions.py <path to process/蒙德> <out.json>
Each entry: commission (Chinese id), kind (basic|npc), location, variant dir, steps, and every route file
(only the fields StoryPilot uses). The route's "anchor" is the last point of _path.json: the commission
marker sits near it, which is how StoryPilot picks the route for a marker it sees on the map.
"""
import json
import os
import sys

KEEP = ("type", "move_mode", "action", "action_params", "x", "y")


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def main(src, out):
    entries = []
    for kind_dir, kind in (("Basic", "basic"), ("NPC", "npc")):
        root = os.path.join(src, kind_dir)
        if not os.path.isdir(root):
            continue
        for name in sorted(os.listdir(root)):
            for variant in sorted(os.listdir(os.path.join(root, name))):
                d = os.path.join(root, name, variant)
                proc = os.path.join(d, "process.json")
                if not os.path.isfile(proc):
                    continue
                routes = {}
                for f in sorted(os.listdir(d)):
                    if f.endswith(".json") and f != "process.json" and ".backup." not in f:
                        try:
                            j = load(os.path.join(d, f))
                        except ValueError:
                            continue
                        if isinstance(j, dict) and "positions" in j:
                            routes[f] = [{k: p[k] for k in KEEP if k in p} for p in j["positions"]]
                main_route = routes.get("_path.json") or []
                anchor = main_route[-1] if main_route else None
                loc = variant.rsplit("-", 1)[0] if variant.rsplit("-", 1)[-1].isdigit() else variant
                entries.append({
                    "commission": name,
                    "kind": kind,
                    "location": loc,
                    "variant": variant,
                    "anchor": {"x": anchor["x"], "y": anchor["y"]} if anchor else None,
                    "steps": load(proc),
                    "routes": routes,
                    "files": sorted(os.listdir(d)),
                })
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"source": "babalae/bettergi-scripts-list AutoCommissionNova (GPL-3.0)", "entries": entries},
                  f, ensure_ascii=False, indent=1)
    print(f"{len(entries)} entries -> {out}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
