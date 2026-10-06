using Mono.Cecil;
using Mono.Cecil.Cil;

static class SurgicalFixes
{
    static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types) => types.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
    public static HashSet<string> Apply(AssemblyDefinition assembly)
    {
        var module=assembly.MainModule;
        var types=All(module.Types).ToArray();
        var methods=types.SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
        var changed=new HashSet<string>();
        MethodDefinition Hook(string name) => types.Single(t=>t.FullName=="BAMP.SyncFix.Hooks").Methods.Single(m=>m.Name==name);
        MethodDefinition Core(string name) => types.Single(t=>t.FullName=="BAMP.SyncFix.SafetyCore").Methods.Single(m=>m.Name==name);
        MethodDefinition Wire(string name) => types.Single(t=>t.FullName=="BAMP.SyncFix.Wire").Methods.Single(m=>m.Name==name);
        MethodDefinition Find(string type,string name) => methods.Single(m=>m.DeclaringType.FullName=="BigAmbitionsMP."+type&&m.Name==name);
        bool Is(Instruction i,string name) => i.Operand is MethodReference r && r.FullName.Contains(name);
        void Changed(MethodDefinition m) { changed.Add(m.FullName); }
        int copy=0, writes=0;
        var payloadRead=Find("MessageEnvelope","GetPayload");
        var decodePayload=types.Single(t=>t.FullName=="BAMP.SyncFix.PayloadReader").Methods.Single(m=>m.Name=="Decode");
        var decodeCall=payloadRead.Body.Instructions.Single(i=>Is(i,"Newtonsoft.Json.JsonConvert::DeserializeObject"));
        var guardedRead=new GenericInstanceMethod(decodePayload);guardedRead.GenericArguments.Add(payloadRead.GenericParameters[0]);decodeCall.Operand=guardedRead;Changed(payloadRead);
        var offerAnswer=Find("MPHub","HostHandleAnswer");
        var moneyTransport=types.Single(t=>t.FullName=="BAMP.SyncFix.MoneyTransport");
        var offerLocalCash=offerAnswer.Body.Instructions.Single(i=>Is(i,"MPHub::MyMoney"));
        var offerRemoteCash=offerAnswer.Body.Instructions.Single(i=>Is(i,"MPServer::GetKnownCash"));
        offerLocalCash.Operand=moneyTransport.Methods.Single(m=>m.Name=="NativeLocalAvailable");
        offerRemoteCash.Operand=moneyTransport.Methods.Single(m=>m.Name=="NativeAvailable");Changed(offerAnswer);
        foreach(var m in methods.Where(m=>m.DeclaringType.Namespace=="BigAmbitionsMP"))
            foreach(var i in m.Body.Instructions.ToArray()) {
                if(Is(i,"System.IO.Stream::CopyTo(System.IO.Stream)")) {i.OpCode=OpCodes.Call;i.Operand=Core("CopyBounded");Changed(m);copy++;}
                if(Is(i,"System.IO.File::WriteAllBytes(System.String,System.Byte[])")) {i.OpCode=OpCodes.Call;i.Operand=Core("AtomicBytes");Changed(m);writes++;}
                if(Is(i,"System.IO.File::WriteAllText(System.String,System.String)")) {i.OpCode=OpCodes.Call;i.Operand=Core("AtomicText");Changed(m);writes++;}
            }
        if(copy<2||writes<8)throw new Exception($"Missing persistence hooks: {copy}/{writes}");
        var persistence=types.Single(t=>t.FullName=="BAMP.SyncFix.CheckedPersistence");
        var writeManifest=Find("MPSaveManager","WriteManifest");
        var manifestCommits=writeManifest.Body.Instructions.Where(i=>Is(i,"System.IO.File::Replace")||Is(i,"System.IO.File::Move")).ToArray();
        if(manifestCommits.Length!=2)throw new Exception("Unexpected manifest commit paths");
        foreach(var call in manifestCommits)writeManifest.Body.GetILProcessor().InsertAfter(call,Instruction.Create(OpCodes.Call,persistence.Methods.Single(m=>m.Name=="ManifestStored")));
        Changed(writeManifest);
        foreach(var persistMethod in new[]{writeManifest,Find("MPSaveCoordinator","PersistGrantsNow")})
        {
            var handler=persistMethod.Body.ExceptionHandlers.Where(e=>e.HandlerType==ExceptionHandlerType.Catch&&e.CatchType?.FullName=="System.Exception").OrderByDescending(e=>e.HandlerStart.Offset).First();
            var old=handler.HandlerStart;var il=persistMethod.Body.GetILProcessor();var duplicate=Instruction.Create(OpCodes.Dup);
            il.InsertBefore(old,duplicate);il.InsertBefore(old,Instruction.Create(OpCodes.Call,persistence.Methods.Single(m=>m.Name=="Failed")));handler.HandlerStart=duplicate;
            foreach(var boundary in persistMethod.Body.ExceptionHandlers){if(boundary.TryEnd==old)boundary.TryEnd=duplicate;if(boundary.HandlerEnd==old)boundary.HandlerEnd=duplicate;}
            Changed(persistMethod);
        }
        var decode=Find("MessageEnvelope","Deserialize");
        var capacityCtor=decode.Body.Instructions.Single(i=>i.OpCode==OpCodes.Newobj&&Is(i,"MemoryStream::.ctor(System.Int32)"));
        decode.Body.GetILProcessor().InsertBefore(capacityCtor,Instruction.Create(OpCodes.Call,Core("InitialCapacity")));Changed(decode);
        foreach(var name in new[]{"HostHandleSaveData","ClientHandleStoreMirror"}) {
            var m=Find("MPSaveCoordinator",name);
            var unzip=m.Body.Instructions.Single(i=>Is(i,"MPSaveCoordinator::UnGzipBytes"));
            var store=unzip.Next;
            VariableDefinition local=store.Operand as VariableDefinition ?? m.Body.Variables[store.OpCode==OpCodes.Stloc_0?0:store.OpCode==OpCodes.Stloc_1?1:store.OpCode==OpCodes.Stloc_2?2:3];
            if(!store.OpCode.Name.StartsWith("stloc"))throw new Exception("Expected decompressed-save local");
            var il=m.Body.GetILProcessor();var cursor=store;
            foreach(var i in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldloc,local),Instruction.Create(OpCodes.Call,Hook("ValidateSave"))}) {il.InsertAfter(cursor,i);cursor=i;}
            var save=m.Body.Instructions.Single(i=>Is(i,"BAMP.SyncFix.SafetyCore::AtomicBytes"));
            var payloadType=m.Parameters[0].ParameterType.Resolve();
            il.InsertBefore(save,Instruction.Create(OpCodes.Ldarg_0));
            il.InsertBefore(save,Instruction.Create(OpCodes.Callvirt,payloadType.Methods.Single(p=>p.Name=="get_MetaJson")));
            save.Operand=Core("AtomicSavePair");
            Changed(m);
        }
        var cargo=Find("CargoTransfer","ApplyDeliver");
        var clear=cargo.Body.Instructions.Single(i=>Is(i,"List`1<BigAmbitionsMP.CargoTransferItem>::Clear"));
        clear.OpCode=OpCodes.Call;clear.Operand=Hook("PreserveCargoResults");
        var catchGet=cargo.Body.Instructions.SkipWhile(i=>i!=clear).Single(i=>Is(i,"CargoTransferPayload::get_Items") && Is(i.Next,"GetEnumerator"));
        var cargoIL=cargo.Body.GetILProcessor();
        var getItems=(MethodReference)catchGet.Operand;
        var c1=Instruction.Create(OpCodes.Ldloc_2);var c2=Instruction.Create(OpCodes.Callvirt,getItems);var c3=Instruction.Create(OpCodes.Call,Hook("Undelivered"));
        cargoIL.InsertAfter(catchGet,c1);cargoIL.InsertAfter(c1,c2);cargoIL.InsertAfter(c2,c3);Changed(cargo);
        var stock=Find("MPStockSync","Tick");
        var progress=types.Single(t=>t.FullName=="BAMP.SyncFix.StorageProgress");
        var result=Find("StorageSync","OnResult");
        foreach(var pair in new[]{("EchoBuildingReplica","Echo"),("OnTakeResult","Take"),("OnPutResult","Put")})
        {
            var call=result.Body.Instructions.Single(i=>Is(i,"StorageSync::"+pair.Item1+"("));
            call.OpCode=OpCodes.Call;call.Operand=progress.Methods.Single(m=>m.Name==pair.Item2);
        }
        Changed(result);
        foreach(var method in methods.Where(m=>m.DeclaringType.FullName=="BigAmbitionsMP.StorageSync"&&new[]{"OnResult","EchoBuildingReplica","OnTakeResult","Deliver","OnPutResult","ConsumeSource","RemoveFromAccessorHandVehicle","ReducePutSourceByAmount","UnequipWornAfterStore"}.Contains(m.Name)))
        {
            foreach(var eh in method.Body.ExceptionHandlers.Where(e=>e.HandlerType==ExceptionHandlerType.Catch).ToArray())
            {
                var originalStart=eh.HandlerStart;
                var fault=Instruction.Create(OpCodes.Call,progress.Methods.Single(m=>m.Name=="Fail"));
                method.Body.GetILProcessor().InsertBefore(eh.HandlerStart,fault);eh.HandlerStart=fault;
                foreach(var boundary in method.Body.ExceptionHandlers){if(boundary.TryEnd==originalStart)boundary.TryEnd=fault;if(boundary.HandlerEnd==originalStart)boundary.HandlerEnd=fault;}
            }
            foreach(var call in method.Body.Instructions.Where(i=>Is(i,"PlayerHelper::set_ItemInstanceInHands")||Is(i,"GameManager::ChangeMoneySafe")||Is(i,"TryToAddToCargo")).ToArray())
            {
                var target=(MethodReference)call.Operand;var il=method.Body.GetILProcessor();
                if(target.ReturnType.FullName=="System.Boolean")
                {
                    var duplicate=Instruction.Create(OpCodes.Dup);il.InsertAfter(call,duplicate);
                    il.InsertAfter(duplicate,Instruction.Create(OpCodes.Call,progress.Methods.Single(m=>m.Name=="CommitIf")));
                }
                else if(target.ReturnType.FullName=="System.Void")il.InsertAfter(call,Instruction.Create(OpCodes.Call,progress.Methods.Single(m=>m.Name=="Commit")));
                else throw new Exception("Unexpected storage commit return type");
            }
            Changed(method);
        }
        var count=stock.Body.Instructions.Single(i=>Is(i,"Dictionary`2<System.String,BigAmbitions.Items.ItemInstance>::get_Count"));
        count.OpCode=OpCodes.Pop;count.Operand=null;
        stock.Body.GetILProcessor().InsertAfter(count,Instruction.Create(OpCodes.Ldc_I4_1));Changed(stock);
        var drain=Find("GameStatePatcher","DrainQueue");
        var ceiling=drain.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldc_I4 && (int)i.Operand==500);
        ceiling.Operand=int.MaxValue;Changed(drain);
        var create=Find("MessageEnvelope","Create");
        var ret=create.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ret);var cil=create.Body.GetILProcessor();
        foreach(var i in new[]{Instruction.Create(OpCodes.Dup),Instruction.Create(OpCodes.Ldarg_2),Instruction.Create(OpCodes.Box,create.GenericParameters[0]),Instruction.Create(OpCodes.Call,Wire("StampEnvelope"))})cil.InsertBefore(ret,i);
        Changed(create);
        foreach(var type in types.Where(t=>t.Namespace=="BigAmbitionsMP" && (t.Name.EndsWith("Payload")||t.Name=="MergerGroupInfo"||t.Name=="MergerOfferInfo")))
            type.Fields.Add(new FieldDefinition("FixStamp",FieldAttributes.Public,types.Single(t=>t.FullName=="BAMP.SyncFix.WireStamp")));
        int protocols=0;
        foreach(var m in methods.Where(m=>m.DeclaringType.FullName=="BigAmbitionsMP.MPClient"||m.DeclaringType.FullName=="BigAmbitionsMP.MPServer")) {
            var ins=m.Body.Instructions;
            for(int k=0;k<ins.Count;k++)if(ins[k].OpCode==OpCodes.Ldc_I4_S && Convert.ToInt32(ins[k].Operand)==27 &&
                (Is(ins[k].Next,"HelloPayload::set_Protocol") || Is(ins[k].Previous,"HelloPayload::get_Protocol") || ins.Any(i=>i.OpCode==OpCodes.Ldstr && ((string)i.Operand).Contains("version mismatch")))) {
                ins[k].OpCode=OpCodes.Ldc_I4;ins[k].Operand=130;Changed(m);protocols++;
            }
        }
        if(protocols!=3)throw new Exception("Expected 3 protocol constants, got "+protocols);

        void ReplacePayloadCall(string callee,string payload,string hook) {
            var candidates=methods.SelectMany(m=>m.Body.Instructions.Where(i=>Is(i,callee)).Select(i=>(m,i)))
                .Where(pair=>pair.m.DeclaringType.Fields.Any(f=>f.FieldType.FullName=="BigAmbitionsMP."+payload)).ToArray();
            if(candidates.Length!=1)throw new Exception($"Expected one {callee} payload closure, got {candidates.Length}");
            var (m,call)=candidates[0];var all=m.Body.Instructions;int end=all.IndexOf(call);
            var starts=all.Take(end).Select((i,k)=>(i,k)).Where(p=>end-p.k<30 && p.i.OpCode==OpCodes.Ldarg_0 && p.i.Next.OpCode==OpCodes.Ldfld && ((FieldReference)p.i.Next.Operand).FieldType.FullName=="BigAmbitionsMP."+payload).ToArray();
            if(starts.Length<2)throw new Exception("Unexpected payload call argument shape");
            var start=starts.First();var field=(FieldReference)start.i.Next.Operand;
            for(int k=start.k;k<=end;k++){all[k].OpCode=OpCodes.Nop;all[k].Operand=null;}
            all[start.k].OpCode=OpCodes.Ldarg_0;all[start.k+1].OpCode=OpCodes.Ldfld;all[start.k+1].Operand=field;
            call.OpCode=OpCodes.Call;call.Operand=Wire(hook);Changed(m);
        }
        ReplacePayloadCall("MPHub::ApplyMoneyDelta", "MoneyAdjustPayload", "ApplyMoney");
        ReplacePayloadCall("MPServer::HostWalletDelta", "MergerWalletDeltaPayload", "ApplyWallet");
        var receive=Find("MPServer","OnReceive");
        var typeGetter=receive.Body.Instructions.First(i=>Is(i,"MessageEnvelope::get_Type"));
        var receiveAnchor=typeGetter.Previous;
        var envelopeLocal=receive.Body.Variables.Single(v=>v.VariableType.FullName=="BigAmbitionsMP.MessageEnvelope");
        var namesTryGet=receive.Body.Instructions.First(i=>Is(i,"Dictionary`2<System.Int32,System.String>::TryGetValue"));
        var senderField=(FieldReference)namesTryGet.Previous.Operand;
        if(namesTryGet.Previous.OpCode!=OpCodes.Ldflda || namesTryGet.Previous.Previous.OpCode!=OpCodes.Ldloc_0)throw new Exception("Unexpected authenticated sender shape");
        var originalReceiveInstructions=receive.Body.Instructions.ToArray();
        var consume=types.Single(t=>t.FullName=="BAMP.SyncFix.MoneyTransport").Methods.Single(m=>m.Name=="Consume");
        var receiveIL=receive.Body.GetILProcessor();
        var receiptFirst=Instruction.Create(OpCodes.Ldloc,envelopeLocal);
        foreach(var i in new[]{receiptFirst,Instruction.Create(OpCodes.Ldloc_0),Instruction.Create(OpCodes.Ldfld,senderField),Instruction.Create(OpCodes.Call,consume),Instruction.Create(OpCodes.Brfalse,receiveAnchor),Instruction.Create(OpCodes.Ret)})receiveIL.InsertBefore(receiveAnchor,i);
        foreach(var i in originalReceiveInstructions)
            if(i.Operand==receiveAnchor)i.Operand=receiptFirst;
        Changed(receive);
        var clientReceive=Find("MPClient","OnReceive");
        var deserialize=clientReceive.Body.Instructions.Single(i=>Is(i,"MessageEnvelope::Deserialize"));
        var savedEnvelope=deserialize.Next;
        if(savedEnvelope.OpCode!=OpCodes.Stloc_0)throw new Exception("Unexpected client envelope local");
        var clientIL=clientReceive.Body.GetILProcessor();var afterEnvelope=savedEnvelope.Next;
        var acceptPacket=types.Single(t=>t.FullName=="BAMP.SyncFix.WorldStreams").Methods.Single(m=>m.Name=="AllowEnvelope");
        foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldloc_0),Instruction.Create(OpCodes.Call,acceptPacket),Instruction.Create(OpCodes.Brtrue,afterEnvelope),Instruction.Create(OpCodes.Ret)})clientIL.InsertBefore(afterEnvelope,instruction);
        Changed(clientReceive);
        // OwnerApply must not turn an already successful mutation into a refusal
        // when the subsequent UI/interior publication throws.
        var ownerApply=Find("StorageSync","OwnerApply");
        var ownerSetOk=ownerApply.Body.Instructions.Single(i=>Is(i,"StorageResPayload::set_Ok"));
        var ownerErrorStart=ownerSetOk.Previous.Previous;
        var ownerLeave=ownerApply.Body.Instructions.First(i=>i.Offset>ownerSetOk.Offset&&i.OpCode==OpCodes.Leave_S);
        var ownerIL=ownerApply.Body.GetILProcessor();
        var okGetter=ownerApply.ReturnType.Resolve().Methods.Single(m=>m.Name=="get_Ok");
        foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldloc_0),Instruction.Create(OpCodes.Callvirt,okGetter),Instruction.Create(OpCodes.Brtrue,ownerLeave)})ownerIL.InsertBefore(ownerErrorStart,instruction);
        Changed(ownerApply);
        var messageType=types.Single(t=>t.FullName=="BigAmbitionsMP.MessageType");
        if(messageType.Fields.Any(f=>f.HasConstant&&Convert.ToInt32(f.Constant)==223))throw new Exception("Receipt type is already allocated");
        messageType.Fields.Add(new FieldDefinition("SyncFixMoneyReceipt",FieldAttributes.Public|FieldAttributes.Static|FieldAttributes.Literal|FieldAttributes.HasDefault,messageType){Constant=(byte)223});
        foreach(var pair in new[]{("SharedShopPrices","MPPriceSync::PublishNow"),("SharedShopSchedule","SharedShopSchedule::ApplyRoutedDays"),("SharedShopStaff","SaveGameManager::MarkChange")})
        {
            var m=Find(pair.Item1,"ApplyOnOwner");var all=m.Body.Instructions;
            var setter=all.Single(i=>Is(i,"Dictionary`2<System.String,System.ValueTuple`2<System.Int32,System.Int32>>::set_Item"));
            int end=all.IndexOf(setter),start=end-7;
            if(all[start].OpCode!=OpCodes.Ldsfld||!((FieldReference)all[start].Operand).Name.Contains("_appliedSeq"))throw new Exception("Watermark instruction shape changed");
            var templates=all.Skip(start).Take(8).Select(i=>(i.OpCode,i.Operand)).ToArray();
            for(int k=start;k<=end;k++){all[k].OpCode=OpCodes.Nop;all[k].Operand=null;}
            var anchors=all.Where(i=>Is(i,pair.Item2)).ToArray();
            if(anchors.Length==0)throw new Exception("No successful watermark anchor for "+pair.Item1);
            foreach(var anchor in anchors)
            {
                var il=m.Body.GetILProcessor();
                var destination=pair.Item1=="SharedShopSchedule"?anchor.Next:anchor;
                if(pair.Item1=="SharedShopStaff"||pair.Item1=="SharedShopSchedule")
                {
                    il.InsertBefore(destination,Instruction.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(destination,Instruction.Create(OpCodes.Callvirt,m.Parameters[0].ParameterType.Resolve().Methods.Single(v=>v.Name=="get_Seq")));
                    il.InsertBefore(destination,Instruction.Create(OpCodes.Brfalse,destination));
                }
                if(pair.Item1=="SharedShopSchedule")
                {
                    var applied=(VariableDefinition)anchor.Previous.Previous.Previous.Operand;
                    var countRef=(MethodReference)all.Skip(all.IndexOf(destination)).First(i=>Is(i,"List`1<System.Int32>::get_Count")).Operand;
                    il.InsertBefore(destination,Instruction.Create(OpCodes.Ldloc,applied));
                    il.InsertBefore(destination,Instruction.Create(OpCodes.Callvirt,countRef));
                    il.InsertBefore(destination,Instruction.Create(OpCodes.Brfalse,destination));
                }
                foreach(var template in templates){var clone=Instruction.Create(OpCodes.Nop);clone.OpCode=template.OpCode;clone.Operand=template.Operand;il.InsertBefore(destination,clone);}
            }
            Changed(m);
        }
        foreach(var method in methods.Where(m=>changed.Contains(m.FullName)))
            foreach(var i in method.Body.Instructions)
                if(i.OpCode.OperandType==OperandType.ShortInlineBrTarget)
                    i.OpCode=typeof(OpCodes).GetFields().Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).Single(op=>op.Name==i.OpCode.Name.Replace(".s",""));
        Console.WriteLine($"Surgical hooks: {writes} atomic writes, {copy} bounded decoders, protocol 130, cargo/stock/queue/wire guards; {changed.Count} reviewed method edits.");
        return changed;
    }
}
