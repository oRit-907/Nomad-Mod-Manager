using Mono.Cecil;
using Mono.Cecil.Cil;
// Makes SharpCompress 0.24.0 (net45) safe inside Nomad Drive: renamed, so it can't clash with another mod's
// SharpCompress, unsigned, and without Debugger.Break (which the game's trimmed mscorlib doesn't have).
var asm = AssemblyDefinition.ReadAssembly(args[0]);
asm.Name.Name = "NomadModManager.SharpCompress";
asm.Name.PublicKey = Array.Empty<byte>(); asm.Name.PublicKeyToken = Array.Empty<byte>();
asm.Name.HasPublicKey = false;
asm.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
asm.MainModule.Name = "NomadModManager.SharpCompress.dll";
int n = 0;
foreach (var t in asm.MainModule.GetTypes())
foreach (var m in t.Methods)
{
    if (!m.HasBody) continue;
    foreach (var ins in m.Body.Instructions)
        if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.DeclaringType.FullName == "System.Diagnostics.Debugger" && mr.Name == "Break")
        { ins.OpCode = OpCodes.Nop; ins.Operand = null; n++; }
}
// ArchiveEncoding picks code page 437 for old zip names, which neither the game nor .NET Core has: UTF-8 instead
// (7z and RAR store names in Unicode anyway).
int e = 0;
foreach (var t in asm.MainModule.GetTypes())
foreach (var m in t.Methods)
{
    if (!m.HasBody) continue;
    var ins = m.Body.Instructions;
    for (int i = 1; i < ins.Count; i++)
        if (ins[i].OpCode == OpCodes.Call && ins[i].Operand is MethodReference g && g.DeclaringType.FullName == "System.Text.Encoding" && g.Name == "GetEncoding"
            && g.Parameters.Count == 1 && g.Parameters[0].ParameterType.FullName == "System.Int32")
        {
            // Encoding.get_UTF8 on the same mscorlib the call already points at (never this tool's own runtime)
            var utf8 = new MethodReference("get_UTF8", g.ReturnType, g.DeclaringType) { HasThis = false };
            ins[i - 1].OpCode = OpCodes.Nop; ins[i - 1].Operand = null; ins[i].Operand = utf8; e++;
        }
}
Console.WriteLine("Code page lookups replaced: " + e);
foreach (var r in asm.MainModule.AssemblyReferences) Console.WriteLine("references " + r.FullName);
asm.Write(args[1]);
Console.WriteLine("Debugger.Break removed: " + n);
