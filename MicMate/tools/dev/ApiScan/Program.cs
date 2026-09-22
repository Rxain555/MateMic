using System;
using System.Linq;
using System.Reflection;

// 查 NAudio 是否自带 SmbPitchShiftingSampleProvider / PitchShiftingSampleProvider
var assemblies = new[]
{
    typeof(NAudio.Wave.WaveFormat).Assembly,
    typeof(NAudio.Dsp.Complex).Assembly,
    typeof(NAudio.Wave.SampleProviders.WaveToSampleProvider).Assembly,
};
foreach (var asm in assemblies.Distinct())
{
    Console.WriteLine($"=== {asm.GetName().Name} ===");
    foreach (var t in asm.GetTypes().Where(t => t.Name.Contains("Pitch", StringComparison.OrdinalIgnoreCase)
                                             || t.Name.Contains("Smb", StringComparison.OrdinalIgnoreCase)
                                             || t.Name.Contains("Autotune", StringComparison.OrdinalIgnoreCase)
                                             || t.Name.Contains("AutoTune", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"  [类型] {t.FullName}");
        foreach (var c in t.GetConstructors())
            Console.WriteLine("    .ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
            Console.WriteLine($"    {m.ReturnType.Name} {m.Name}(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
        foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"    prop {pr.PropertyType.Name} {pr.Name}");
    }
}
