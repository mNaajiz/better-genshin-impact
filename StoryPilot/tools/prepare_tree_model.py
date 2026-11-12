"""Copy BetterGI's reward-tree model into StoryPilot, in a form OpenCV's DNN module can load.

BetterGI's bgi_tree.onnx (Ultralytics 8.4 export, opset 22, FP16) lists its nodes out of order:
the input Cast comes after the first Conv. ONNX Runtime doesn't mind, but OpenCV's importer
fails with "inputs.size() != 0". This script only reorders the nodes (topological sort); the
weights and outputs are unchanged.

Usage (needs `pip install onnx`):
    python StoryPilot/tools/prepare_tree_model.py "C:/Program Files/BetterGI/Assets/Model/Domain/bgi_tree.onnx"
The result goes to StoryPilot/Assets/Model/bgi_tree.onnx (git-ignored) unless a second path is given.
"""

import os
import sys

import onnx


def toposort(graph):
    available = {i.name for i in graph.input} | {t.name for t in graph.initializer} | {""}
    pending, ordered = list(graph.node), []
    while pending:
        rest = []
        for node in pending:
            if all(name in available for name in node.input):
                ordered.append(node)
                available.update(node.output)
            else:
                rest.append(node)
        if len(rest) == len(pending):
            raise RuntimeError("the graph has a cycle or a missing input")
        pending = rest
    del graph.node[:]
    graph.node.extend(ordered)


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "Model", "bgi_tree.onnx")
    model = onnx.load(src)
    toposort(model.graph)
    onnx.checker.check_model(model)
    os.makedirs(os.path.dirname(os.path.abspath(dst)), exist_ok=True)
    onnx.save(model, dst)
    print(f"wrote {os.path.abspath(dst)}")


if __name__ == "__main__":
    main()
