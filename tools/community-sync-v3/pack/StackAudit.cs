using Mono.Cecil;
using Mono.Cecil.Cil;
static class StackAudit
{
    public static void Check(MethodDefinition method)
    {
        var ins=method.Body.Instructions;var seen=new Dictionary<Instruction,int>();var work=new Queue<(Instruction,int)>();
        void Push(Instruction i,int height){if(i!=null)work.Enqueue((i,height));}
        Push(ins[0],0);
        foreach(var eh in method.Body.ExceptionHandlers){Push(eh.HandlerStart,eh.HandlerType==ExceptionHandlerType.Catch?1:0);if(eh.FilterStart!=null)Push(eh.FilterStart,1);}
        while(work.Count>0)
        {
            var (i,height)=work.Dequeue();if(seen.TryGetValue(i,out var previous)){if(previous!=height)throw new Exception($"Stack merge {method.FullName}: {i} {height}/{previous}");continue;}seen[i]=height;
            int pop=0,push=0;
            if(i.Operand is MethodReference call&&(i.OpCode==OpCodes.Call||i.OpCode==OpCodes.Callvirt||i.OpCode==OpCodes.Newobj))
            {pop=call.Parameters.Count+(i.OpCode!=OpCodes.Newobj&&call.HasThis?1:0);push=i.OpCode==OpCodes.Newobj||call.ReturnType.FullName!="System.Void"?1:0;}
            else if(i.OpCode==OpCodes.Ret)pop=method.ReturnType.FullName=="System.Void"?0:1;
            else {
                pop=i.OpCode.StackBehaviourPop switch {
                    StackBehaviour.Pop0=>0,StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref=>1,
                    StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi=>2,
                    StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref=>3,
                    StackBehaviour.PopAll=>height,StackBehaviour.Varpop=>0,_=>throw new Exception("Unknown pop "+i)};
                push=i.OpCode.StackBehaviourPush switch {StackBehaviour.Push0=>0,StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref=>1,StackBehaviour.Push1_push1=>2,StackBehaviour.Varpush=>0,_=>throw new Exception("Unknown push "+i)};
            }
            if(height<pop)throw new Exception($"Stack underflow {method.FullName}: {i} height {height}, pop {pop}");
            int next=height-pop+push;
            if(i.OpCode==OpCodes.Leave||i.OpCode==OpCodes.Leave_S)next=0;
            if(i.OpCode.FlowControl==FlowControl.Return||i.OpCode.FlowControl==FlowControl.Throw)continue;
            if(i.Operand is Instruction target)Push(target,next);
            if(i.Operand is Instruction[] targets)foreach(var target2 in targets)Push(target2,next);
            if(i.OpCode.FlowControl!=FlowControl.Branch)Push(i.Next,next);
        }
    }
}
