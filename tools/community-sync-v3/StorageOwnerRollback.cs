using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BigAmbitionsMP;

namespace BAMP.SyncFix
{
    // Preserve live object identities and runtime caches. Restoring original
    // fields/list entries needs no constructors or Unity asset serialization.
    public sealed class CargoRollback
    {
        private readonly List<Action> restore=new List<Action>();
        private readonly HashSet<object> seen=new HashSet<object>(ReferenceComparer.Instance);
        private sealed class ReferenceComparer:IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance=new ReferenceComparer();
            public new bool Equals(object a,object b)=>ReferenceEquals(a,b);
            public int GetHashCode(object a)=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a);
        }
        public void Dictionary(IDictionary dictionary)
        {
            if(dictionary==null)return;
            var entries=new List<DictionaryEntry>();foreach(DictionaryEntry e in dictionary)entries.Add(e);
            restore.Add(()=>{dictionary.Clear();foreach(var e in entries)dictionary.Add(e.Key,e.Value);});
        }
        public void Capture(object root)
        {
            if(root==null||!seen.Add(root))return;
            if(root is IList list)
            {
                var entries=new object[list.Count];list.CopyTo(entries,0);
                restore.Add(()=>{list.Clear();foreach(var e in entries)list.Add(e);});
                foreach(var entry in entries)Capture(entry);return;
            }
            var type=root.GetType();
            if(type.Namespace==null||!type.Namespace.StartsWith("BigAmbitions.Items",StringComparison.Ordinal))return;
            foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.Instance))
            {
                if(field.IsDefined(typeof(NonSerializedAttribute),true))continue;
                var value=field.GetValue(root);if(!field.IsInitOnly)restore.Add(()=>field.SetValue(root,value));
                if(value is IList||value?.GetType().Namespace?.StartsWith("BigAmbitions.Items",StringComparison.Ordinal)==true)Capture(value);
            }
        }
        public void Restore(){foreach(var action in restore)action();}
        public void CargoField(object holder)
        {
            var field=holder?.GetType().GetField("cargoInstances");if(field==null)return;
            var value=field.GetValue(holder);restore.Add(()=>field.SetValue(holder,value));Capture(value);
        }
        public static CargoRollback Owner(StorageOpPayload req)
        {
            var snapshot=new CargoRollback();
            if(req.Container=="vehicle")snapshot.CargoField(Hooks.Call("StorageSync","FindVehicleById",req.VehicleId,"rollback baseline"));
            else
            {
                var registration=Hooks.Call("GameStatePatcher","FindRegistration",req.AddressKey);
                var entries=(IDictionary)registration?.GetType().GetField("itemInstances")?.GetValue(registration);
                if(entries!=null)
                {
                    if(req.Ctx=="itemsell")snapshot.Dictionary(entries);
                    foreach(DictionaryEntry entry in entries)
                    {
                        var id=entry.Value?.GetType().GetField("id")?.GetValue(entry.Value)?.ToString();
                        if(entry.Key?.ToString()==req.ItemId||id==req.ItemId||req.Ctx=="stationreturn")snapshot.Capture(entry.Value);
                    }
                }
            }
            return snapshot;
        }
    }
}
