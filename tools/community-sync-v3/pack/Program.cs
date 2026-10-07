using ILRepacking;
using Mono.Cecil;

if(args.Length==3&&args[0]=="contracts") {
    using var target=AssemblyDefinition.ReadAssembly(args[1]);
    IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(Types(t.NestedTypes)));
    var all=Types(target.MainModule.Types).ToDictionary(t=>t.FullName);
    int checkedRefs=0;
    foreach(var file in Directory.GetFiles(args[2],"*.cs",SearchOption.TopDirectoryOnly))
    foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file),"Hooks\\.(Get|Set|Call|Method)\\(\\s*\"([^\"]+)\"\\s*,\\s*\"([^\"]+)\"")) {
        var kind=match.Groups[1].Value;var typeName="BigAmbitionsMP."+match.Groups[2].Value;var member=match.Groups[3].Value;
        if(!all.TryGetValue(typeName,out var type))throw new Exception("Missing reflection type: "+typeName);
        bool found=kind=="Get"||kind=="Set"?type.Fields.Any(f=>f.Name==member):type.Methods.Any(m=>m.Name==member);
        if(!found)throw new Exception("Missing reflection contract: "+Path.GetFileName(file)+" "+typeName+"."+member);
        checkedRefs++;
    }
    Console.WriteLine("PASS: "+checkedRefs+" literal reflection member references match the actual DLL.");return;
}

if (args.Length >= 3 && args[0] == "inspect") {
    using var inspected = AssemblyDefinition.ReadAssembly(args[1]);
    IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[]{t}.Concat(All(t.NestedTypes)));
    foreach(var method in All(inspected.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody && m.FullName.Contains(args[2]))) {
        Console.WriteLine(method.FullName);
        foreach(var i in method.Body.Instructions) Console.WriteLine(i);
        foreach(var e in method.Body.ExceptionHandlers) Console.WriteLine($"EH {e.HandlerType} {e.TryStart} -> {e.TryEnd}; {e.HandlerStart} -> {e.HandlerEnd}");
    }
    return;
}
if (args.Length != 4) throw new ArgumentException("original helper output gameManaged");
var original = Path.GetFullPath(args[0]);
var helper = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
new ILRepack(new RepackOptions {
    InputAssemblies = new[] { original, helper },
    OutputFile = output,
    SearchDirectories = new[] { Path.GetFullPath(args[3]), Path.Combine(Path.GetDirectoryName(original)!, "Dependencies"), Path.GetDirectoryName(helper)! },
    TargetKind = ILRepack.Kind.Dll,
    DebugInfo = false
}).Repack();

// Verify every original method body symbolically: tokens can be renumbered by a
// merge, but behavior, field/method references and control flow must be unchanged.
using var before = AssemblyDefinition.ReadAssembly(original);
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetFullPath(args[3]));
resolver.AddSearchDirectory(Path.Combine(Path.GetDirectoryName(original)!, "Dependencies"));
resolver.AddSearchDirectory(Path.GetDirectoryName(helper)!);
using var after = AssemblyDefinition.ReadAssembly(output,new ReaderParameters {AssemblyResolver=resolver,InMemory=true});
var permittedChanges = SurgicalFixes.Apply(after);
HarmonyAudit.Check(after);
var auditMethods=Flatten(after.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody&&(permittedChanges.Contains(m.FullName)||m.DeclaringType.FullName.StartsWith("BAMP."))).ToArray();
foreach(var method in auditMethods)StackAudit.Check(method);
Console.WriteLine("PASS: IL stack/branches checked for "+auditMethods.Length+" changed and helper method bodies.");
IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[] {t}.Concat(Flatten(t.NestedTypes)));
string Body(MethodDefinition m) {
    if (!m.HasBody) return "no-body";
    var ins = m.Body.Instructions;
    string Operand(object? o) => o switch {
        null => "",
        Mono.Cecil.Cil.Instruction target => "@" + ins.IndexOf(target),
        Mono.Cecil.Cil.Instruction[] targets => string.Join(",", targets.Select(t => "@" + ins.IndexOf(t))),
        MemberReference member => member.FullName,
        Mono.Cecil.Cil.VariableDefinition variable => "local" + variable.Index,
        ParameterDefinition parameter => "arg" + parameter.Index,
        _ => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };
    var il = string.Join("\n", ins.Select(i => i.OpCode.Name + " " + Operand(i.Operand)));
    var eh = string.Join("\n", m.Body.ExceptionHandlers.Select(e => e.HandlerType + ":" + e.CatchType?.FullName + ":" + Operand(e.TryStart) + ":" + Operand(e.TryEnd) + ":" + Operand(e.HandlerStart) + ":" + Operand(e.HandlerEnd) + ":" + Operand(e.FilterStart)));
    return m.Body.InitLocals + ":" + string.Join(",", m.Body.Variables.Select(v => v.VariableType.FullName)) + "\n" + il + "\n" + eh;
}
var afterTypes = Flatten(after.MainModule.Types).ToDictionary(t => t.FullName);
int methods = 0;
foreach(var type in Flatten(before.MainModule.Types)) {
    if (!afterTypes.TryGetValue(type.FullName, out var merged)) throw new Exception("Lost type: " + type.FullName);
    foreach(var field in type.Fields)
        if (!merged.Fields.Any(f => f.FullName == field.FullName && f.Attributes == field.Attributes)) throw new Exception("Changed field: " + field.FullName);
    foreach(var method in type.Methods) {
        var found = merged.Methods.SingleOrDefault(m => m.FullName == method.FullName) ?? throw new Exception("Lost method: " + method.FullName);
        if (!permittedChanges.Contains(method.FullName) && Body(found) != Body(method)) throw new Exception("Unexpected original IL change: " + method.FullName);
        methods++;
    }
}
var patches = Flatten(after.MainModule.Types).Where(t => t.Namespace == "BAMP.HostBizManFix" && t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")).ToArray();
if (patches.Length != 7) throw new Exception("Expected 7 host patches, got " + patches.Length);
var cartPatches = Flatten(after.MainModule.Types).Where(t => t.Namespace == "BAMP.CartTrunkFix" && t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")).ToArray();
if (cartPatches.Length != 3) throw new Exception("Expected 3 cart patches, got " + cartPatches.Length);
if (after.MainModule.AssemblyReferences.Any(r => r.Name == "BAMP.HostBizManFix" || r.Name == "BigAmbitionsMP")) throw new Exception("Unexpected self/helper dependency");
after.Write(output + ".verified.tmp");
File.Move(output + ".verified.tmp", output, true);
File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!,"verification.json"),System.Text.Json.JsonSerializer.Serialize(new {
    sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output))),
    gameSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(Path.GetFullPath(args[3]),"BigAmbitions.dll")))),
    protocol=131,reviewedMethodEdits=permittedChanges.Count,originalMethodBodies=methods-permittedChanges.Count,ilBodiesChecked=auditMethods.Length
}));
File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "reviewed-method-edits.txt"),string.Join("\n",permittedChanges.Order()));
Console.WriteLine("PASS: " + (methods-permittedChanges.Count) + " original method bodies unchanged; " + permittedChanges.Count + " reviewed edits; 7 host + 3 cart and sync patches merged; no helper DLL dependency.");
