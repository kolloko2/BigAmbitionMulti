using Mono.Cecil;
using Mono.Cecil.Cil;

static class HarmonyAudit
{
    static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
    public static void Check(AssemblyDefinition assembly)
    {
        int count=0;
        var all=All(assembly.MainModule.Types).ToArray();
        foreach(var patch in all.Where(t=>t.Namespace.StartsWith("BAMP.")&&t.CustomAttributes.Any(a=>a.AttributeType.FullName=="HarmonyLib.HarmonyPatch")))
        {
            var targets=new List<MethodDefinition>();
            foreach(var attribute in patch.CustomAttributes.Where(a=>a.AttributeType.FullName=="HarmonyLib.HarmonyPatch"))
            {
                var type=attribute.ConstructorArguments.Select(a=>a.Value).OfType<TypeReference>().FirstOrDefault();
                var name=attribute.ConstructorArguments.Select(a=>a.Value).OfType<string>().FirstOrDefault();
                if(type!=null&&name!=null)
                {
                    var argumentTypes=attribute.ConstructorArguments
                        .Where(a=>a.Type.FullName=="System.Type[]")
                        .Select(a=>a.Value).OfType<CustomAttributeArgument[]>().FirstOrDefault();
                    targets.AddRange(type.Resolve().Methods.Where(m=>m.Name==name
                        &&(argumentTypes==null || (m.Parameters.Count==argumentTypes.Length
                            && m.Parameters.Select(p=>p.ParameterType.FullName)
                                .SequenceEqual(argumentTypes.Select(a=>((TypeReference)a.Value).FullName))))));
                }
            }
            foreach(var method in All(new[]{patch}).SelectMany(t=>t.Methods).Where(m=>m.HasBody))
            {
                var ins=method.Body.Instructions;
                for(int k=2;k<ins.Count;k++)
                    if(ins[k].Operand is MethodReference mr&&mr.DeclaringType.FullName=="BAMP.SyncFix.Hooks"&&mr.Name=="Method"&&ins[k-1].OpCode==OpCodes.Ldstr&&ins[k-2].OpCode==OpCodes.Ldstr)
                    {
                        var targetType=all.Single(t=>t.FullName=="BigAmbitionsMP."+(string)ins[k-2].Operand);
                        var name=(string)ins[k-1].Operand;
                        var found=targetType.Methods.Where(m=>m.Name==name).ToArray();
                        if(found.Length!=1)throw new Exception($"Target ambiguity/missing: {patch.Name}: {targetType.Name}.{name}");
                        targets.Add(found[0]);
                    }
            }
            if(patch.Namespace!="BAMP.SyncFix")continue; // existing host/cart targets covered by their executable tests
            if(targets.Count==0)throw new Exception("No Harmony target found for "+patch.Name);
            foreach(var handler in patch.Methods.Where(m=>m.Name=="Prefix"||m.Name=="Postfix"||m.Name=="Finalizer"))
                foreach(var p in handler.Parameters.Where(p=>!p.Name.StartsWith("__")))
                    foreach(var target in targets)
                        if(!target.Parameters.Any(tp=>tp.Name==p.Name))throw new Exception($"Harmony argument mismatch: {patch.Name}.{handler.Name} '{p.Name}' -> {target.FullName}");
            count++;
        }
        Console.WriteLine($"PASS: {count} sync Harmony patch targets and injected argument names match the actual game/mod metadata.");
    }
}
