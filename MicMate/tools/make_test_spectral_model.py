#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成一个最小但形态正确的**频谱域**降噪 ONNX 模型，用于验证 MicMate 的频谱域适配器。

形态（与 GTCRN / DPDFNet 同族）：
    输入  spec      float32 [1, bins, 1, 2]   复频谱（实/虚交错在最后一维）
    输入  state_in  float32 [1, state]        循环状态（本例不使用，占位以贴合真实形态）
    输出  spec_e    float32 [1, bins, 1, 2]   处理后的复频谱
    输出  state_out float32 [1, state]        状态回传

行为：对每个频点施加固定增益（模拟降噪掩膜），因此输出电平应当低于输入但结构相同。
这样我就能客观验证：
  · STFT/分析是否与模型约定一致（否则电平会完全错乱）
  · 输出能否正确 iSTFT 回波形

只依赖标准库 + onnx。
"""
import sys
import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

BINS = int(sys.argv[1]) if len(sys.argv) > 1 else 481
STATE = int(sys.argv[2]) if len(sys.argv) > 2 else 8
OUT = sys.argv[3] if len(sys.argv) > 3 else f"spec_denoise_b{BINS}.onnx"

# 降噪掩膜：高频多压一点，低频少压一点（模拟真实降噪的频响倾向）
gains = np.linspace(0.85, 0.35, BINS, dtype=np.float32)
mask = np.repeat(gains[None, :, None, None], 2, axis=3).astype(np.float32)   # [1, BINS, 1, 2]

nodes = [
    helper.make_node("Mul", ["spec", "mask"], ["spec_e"], name="apply_mask"),
    # 状态原样回传（真实模型里这里是 GRU/卷积 cache）
    helper.make_node("Identity", ["state_in"], ["state_out"], name="pass_state"),
]

inputs = [
    helper.make_tensor_value_info("spec", TensorProto.FLOAT, [1, BINS, 1, 2]),
    helper.make_tensor_value_info("state_in", TensorProto.FLOAT, [1, STATE]),
]
outputs = [
    helper.make_tensor_value_info("spec_e", TensorProto.FLOAT, [1, BINS, 1, 2]),
    helper.make_tensor_value_info("state_out", TensorProto.FLOAT, [1, STATE]),
]

graph = helper.make_graph(nodes, "MicMateSpectralTest", inputs, outputs,
                          [numpy_helper.from_array(mask, name="mask")])
model = helper.make_model(graph, producer_name="MicMate-Test",
                          opset_imports=[helper.make_opsetid("", 13)])
model.ir_version = 8

onnx.checker.check_model(model)
onnx.save(model, OUT)
print(f"已生成 {OUT}：bins={BINS} state={STATE}，掩膜 {gains[0]:0.2f} → {gains[-1]:0.2f}")
