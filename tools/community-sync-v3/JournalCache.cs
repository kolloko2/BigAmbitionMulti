using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace BAMP.SyncFix
{
    // Each update serializes one small bucket. Read paths reuse parsed state;
    // replay tombstones are retained rather than expired into possible duplicates.
    public static class JournalCache
    {
        private const string Legacy="GoingPublic.SyncFix.1", Prefix="GoingPublic.SyncFix.2.";
        private const int Buckets=256;
        private sealed class State
        {
            public Dictionary<string,string> ModData;
            public readonly Dictionary<string,string>[] Values=new Dictionary<string,string>[Buckets];
            public readonly string[] Text=new string[Buckets];
            public readonly Dictionary<string,Dictionary<string,string>> Index=new Dictionary<string,Dictionary<string,string>>();
        }
        private static readonly ConditionalWeakTable<GameInstance,State> Cache=new ConditionalWeakTable<GameInstance,State>();
        private static readonly object Gate=new object();
        private static int Bucket(string key){uint h=2166136261;foreach(char c in key)h=unchecked((h^c)*16777619);return (int)(h%Buckets);}
        private static State Get()
        {
            var save=SaveGameManager.Current;if(save==null)throw new InvalidOperationException("Journal requires a loaded character.");
            if(save.modData==null)save.modData=new Dictionary<string,string>();
            var state=Cache.GetOrCreateValue(save);
            if(!ReferenceEquals(state.ModData,save.modData))
            {state.ModData=save.modData;Array.Clear(state.Values,0,Buckets);Array.Clear(state.Text,0,Buckets);state.Index.Clear();}
            if(state.ModData.TryGetValue(Legacy,out var old))
            {
                var merged=new Dictionary<string,string>[Buckets];
                for(int i=0;i<Buckets;i++){Load(state,i);merged[i]=new Dictionary<string,string>(state.Values[i]);}
                foreach(var pair in JsonConvert.DeserializeObject<Dictionary<string,string>>(old)??new Dictionary<string,string>())
                    if(!merged[Bucket(pair.Key)].ContainsKey(pair.Key))merged[Bucket(pair.Key)][pair.Key]=pair.Value;
                // Prepare every serialized bucket before mutating modData.
                var texts=merged.Select(v=>JsonConvert.SerializeObject(v)).ToArray();
                state.Index.Clear();
                for(int i=0;i<Buckets;i++){state.Values[i]=merged[i];state.Text[i]=texts[i];state.ModData[Prefix+i.ToString("X2")]=texts[i];foreach(var p in merged[i])Index(state,p.Key,p.Value);}
                state.ModData.Remove(Legacy);SaveGameManager.MarkChange();
            }
            return state;
        }
        private static void Load(State state,int i)
        {
            state.ModData.TryGetValue(Prefix+i.ToString("X2"),out var raw);
            if(state.Values[i]==null||state.Text[i]!=raw)
            {
                if(state.Values[i]!=null)foreach(var p in state.Values[i])Index(state,p.Key,null);
                state.Values[i]=raw==null?new Dictionary<string,string>():JsonConvert.DeserializeObject<Dictionary<string,string>>(raw)??new Dictionary<string,string>();state.Text[i]=raw;
                foreach(var p in state.Values[i])Index(state,p.Key,p.Value);
            }
        }
        private static void Index(State state,string key,string value)
        {
            int sep=key.IndexOf('|');string category=sep<0?key:key.Substring(0,sep+1);
            if(!state.Index.TryGetValue(category,out var entries))state.Index[category]=entries=new Dictionary<string,string>();
            if(value==null)entries.Remove(key);else entries[key]=value;
        }
        public static KeyValuePair<string,string>[] Scan(string prefix)
        {
            lock(Gate){var s=Get();for(int i=0;i<Buckets;i++)Load(s,i);return s.Index.TryGetValue(prefix,out var entries)?entries.ToArray():s.Index.Where(p=>p.Key.StartsWith(prefix,StringComparison.Ordinal)).SelectMany(p=>p.Value).ToArray();}
        }
        public static void Remove(string key)
        {
            lock(Gate){var s=Get();int b=Bucket(key);Load(s,b);if(!s.Values[b].ContainsKey(key))return;
                var next=new Dictionary<string,string>(s.Values[b]);next.Remove(key);string json=JsonConvert.SerializeObject(next);
                s.ModData[Prefix+b.ToString("X2")]=json;s.Values[b]=next;s.Text[b]=json;Index(s,key,null);SaveGameManager.MarkChange();}
        }
        public static string Result(string key)
        {lock(Gate){var s=Get();int b=Bucket(key);Load(s,b);return s.Values[b].TryGetValue(key,out var v)?v:null;}}
        public static Dictionary<string,string> Snapshot()
        {lock(Gate){var s=Get();var all=new Dictionary<string,string>();for(int i=0;i<Buckets;i++){Load(s,i);foreach(var p in s.Values[i])all[p.Key]=p.Value;}return all;}}
        public static void Remember(string key,string value)
        {
            lock(Gate)
            {
                var s=Get();int b=Bucket(key);Load(s,b);
                if(s.Values[b].TryGetValue(key,out var old)&&old==value)return;
                var next=new Dictionary<string,string>(s.Values[b]){[key]=value};
                string json=JsonConvert.SerializeObject(next);
                s.ModData[Prefix+b.ToString("X2")]=json;s.Values[b]=next;s.Text[b]=json;Index(s,key,value);SaveGameManager.MarkChange();
            }
        }
    }
}
