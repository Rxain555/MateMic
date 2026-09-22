using System;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

// 读取 DPDFNet 模型的张量名称与自定义元数据（状态初值就藏在元数据里）。
var path = args[0];
using var session = new InferenceSession(path);

Console.WriteLine("=== 输入 ===");
foreach (var input in session.InputMetadata)
    Console.WriteLine($"  {input.Key}  [{string.Join(",", input.Value.Dimensions)}] {input.Value.ElementType}");

Console.WriteLine("=== 输出 ===");
foreach (var output in session.OutputMetadata)
    Console.WriteLine($"  {output.Key}  [{string.Join(",", output.Value.Dimensions)}] {output.Value.ElementType}");

Console.WriteLine("=== 自定义元数据 ===");
var metadata = session.ModelMetadata;
Console.WriteLine($"  ProducerName: {metadata.ProducerName}");
Console.WriteLine($"  GraphName:    {metadata.GraphName}");
Console.WriteLine($"  Description:  {metadata.Description}");
Console.WriteLine($"  Version:      {metadata.Version}");
Console.WriteLine($"  CustomMetadataMap 数量: {metadata.CustomMetadataMap.Count}");
foreach (var kv in metadata.CustomMetadataMap)
{
    var value = kv.Value ?? string.Empty;
    var preview = value.Length > 120 ? value[..120] + "…" : value;
    Console.WriteLine($"  {kv.Key} = {preview}");
}
