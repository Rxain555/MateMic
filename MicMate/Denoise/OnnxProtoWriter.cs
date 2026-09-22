using System.Text;

namespace MicMate.Denoise;

/// <summary>
/// 极简 ONNX protobuf 写入器（只覆盖本程序需要的字段）。
///
/// 生成的模型是一个“常量增益曲线”图：
///     bounded = Clip(log_mag, -120, 0)
///     gain    = Clip(gain_base, 0.08, 1)
/// 输入 log_mag [1,481] float32，输出 gain [1,481] float32。
/// 训练得到的每频点增益以 initializer（常数权重）形式写入图内，
/// 由 MicMate 用 OnnxRuntime.InferenceSession 校验并取出后驱动实时降噪。
///
/// 之所以手写而不是引入 onnxruntime 的训练/构造 API：保持依赖最小，
/// 同时让产出的 .onnx 与 tools/train_denoise.py 的产出完全等价、可互相替换。
/// </summary>
internal sealed class OnnxProtoWriter
{
    // ONNX TensorProto：1 = dims，2 = data_type，8 = name，9 = raw_data
    private const int FieldTensorDims = 1;         // TensorProto.dims
    private const int FieldTensorType = 2;         // TensorProto.data_type
    private const int FieldTensorRawData = 9;      // TensorProto.raw_data
    private const int FieldTensorName = 8;         // TensorProto.name

    private const int FieldNodeInput = 1;          // NodeProto.input
    private const int FieldNodeOutput = 2;         // NodeProto.output
    private const int FieldNodeName = 3;           // NodeProto.name
    private const int FieldNodeOpType = 4;         // NodeProto.op_type

    private const int FieldGraphNode = 1;          // GraphProto.node
    private const int FieldGraphName = 2;          // GraphProto.name
    private const int FieldGraphInitializer = 5;   // GraphProto.initializer
    private const int FieldGraphInput = 11;        // GraphProto.input
    private const int FieldGraphOutput = 12;       // GraphProto.output

    private const int FieldModelIrVersion = 1;       // ModelProto.ir_version
    private const int FieldModelProducerName = 2;    // ModelProto.producer_name
    private const int FieldModelGraph = 7;           // ModelProto.graph
    private const int FieldModelOpsetImport = 8;     // ModelProto.opset_import

    private const int FieldValueInfoName = 1;      // ValueInfoProto.name
    private const int FieldValueInfoType = 2;      // ValueInfoProto.type
    private const int FieldTypeTensorType = 1;     // TypeProto.tensor_type
    private const int FieldTensorTypeElemType = 1; // TypeProto.Tensor.elem_type
    private const int FieldTensorTypeShape = 2;    // TypeProto.Tensor.shape
    private const int FieldTensorShapeDim = 1;     // TensorShapeProto.dim
    private const int FieldDimValue = 1;           // TensorShapeProto.Dimension.dim_value

    private const int FieldOpsetVersion = 2;       // OperatorSetIdProto.version

    // ------------------------------------------------------------ TensorProto

    /// <summary>生成一个 float32 常量张量。dimensions 为空时表示标量（ONNX Clip 的 min/max 要求标量）。</summary>
    public byte[] MakeTensor(string name, float[] values, int[]? dimensions = null)
    {
        var dims = dimensions ?? new[] { 1, values.Length };
        var payload = new Proto();

        // 字段按编号递增写出：1=dims, 2=data_type, 8=name, 9=raw_data
        foreach (var dim in dims) payload.Int32(FieldTensorDims, dim);
        payload.Int32(FieldTensorType, 1);   // FLOAT
        payload.Bytes(FieldTensorName, Encoding.UTF8.GetBytes(name));

        var raw = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        payload.Bytes(FieldTensorRawData, raw);
        return payload.ToArray();
    }

    /// <summary>生成一个标量 float32 常量张量（shape 为空）。</summary>
    public byte[] MakeScalar(string name, float value) => MakeTensor(name, new[] { value }, Array.Empty<int>());

    // ------------------------------------------------------------ NodeProto

    public byte[] MakeNode(string opType, string[] inputs, string[] outputs, string name)
    {
        var node = new Proto();
        foreach (var input in inputs) node.Bytes(FieldNodeInput, Encoding.UTF8.GetBytes(input));
        foreach (var output in outputs) node.Bytes(FieldNodeOutput, Encoding.UTF8.GetBytes(output));
        node.Bytes(FieldNodeName, Encoding.UTF8.GetBytes(name));
        node.Bytes(FieldNodeOpType, Encoding.UTF8.GetBytes(opType));
        return node.ToArray();
    }

    // ------------------------------------------------------------ GraphProto

    public byte[] MakeGraph(string name, IReadOnlyList<byte[]> nodes, IReadOnlyList<byte[]> initializers)
    {
        var graph = new Proto();
        foreach (var node in nodes) graph.Bytes(FieldGraphNode, node);
        graph.Bytes(FieldGraphName, Encoding.UTF8.GetBytes(name));
        foreach (var initializer in initializers) graph.Bytes(FieldGraphInitializer, initializer);
        graph.Bytes(FieldGraphInput, MakeValueInfo("log_mag", 481));
        graph.Bytes(FieldGraphOutput, MakeValueInfo("gain", 481));
        return graph.ToArray();
    }

    private static byte[] MakeValueInfo(string name, int width)
    {
        var dim1 = new Proto();
        dim1.Int64(FieldDimValue, 1);

        var dim2 = new Proto();
        dim2.Int64(FieldDimValue, width);

        var shape = new Proto();
        shape.Bytes(FieldTensorShapeDim, dim1.ToArray());
        shape.Bytes(FieldTensorShapeDim, dim2.ToArray());

        var tensorType = new Proto();
        tensorType.Int32(FieldTensorTypeElemType, 1);   // FLOAT
        tensorType.Bytes(FieldTensorTypeShape, shape.ToArray());

        var type = new Proto();
        type.Bytes(FieldTypeTensorType, tensorType.ToArray());

        var valueInfo = new Proto();
        valueInfo.Bytes(FieldValueInfoName, Encoding.UTF8.GetBytes(name));
        valueInfo.Bytes(FieldValueInfoType, type.ToArray());
        return valueInfo.ToArray();
    }

    // ------------------------------------------------------------ ModelProto

    public byte[] MakeModel(byte[] graph)
    {
        var opset = new Proto();
        opset.Int64(FieldOpsetVersion, 13);

        var model = new Proto();
        model.Int64(FieldModelIrVersion, 8);
        model.Bytes(FieldModelProducerName, Encoding.UTF8.GetBytes("MicMate"));
        model.Bytes(FieldModelGraph, graph);
        model.Bytes(FieldModelOpsetImport, opset.ToArray());
        return model.ToArray();
    }

    // ------------------------------------------------------------ protobuf 基础写入

    private sealed class Proto
    {
        private readonly MemoryStream _stream = new();

        public void Int32(int field, int value) => Varint((field << 3) | 0, (ulong)(long)value);

        public void Int64(int field, long value) => Varint((field << 3) | 0, (ulong)value);

        public void Bytes(int field, byte[] value)
        {
            Varint((field << 3) | 2, (ulong)value.Length);
            _stream.Write(value, 0, value.Length);
        }

        private void Varint(int tag, ulong value)
        {
            WriteVarint((ulong)tag);
            WriteVarint(value);
        }

        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            _stream.WriteByte((byte)value);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
