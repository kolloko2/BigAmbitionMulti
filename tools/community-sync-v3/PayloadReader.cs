using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace BAMP.SyncFix
{
    public static class PayloadReader
    {
        private static long nextWarning;
        public static T Decode<T>(string json)
        {
            if(string.IsNullOrWhiteSpace(json))return default(T);
            try
            {
                using(var reader=new JsonTextReader(new StringReader(json)){MaxDepth=64})
                    return JsonSerializer.CreateDefault(new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=64,CheckAdditionalContent=true}).Deserialize<T>(reader);
            }
            catch(JsonException ex)
            {
                long now=DateTime.UtcNow.Ticks,prior=Interlocked.Read(ref nextWarning);
                if(now>=prior&&Interlocked.CompareExchange(ref nextWarning,now+TimeSpan.TicksPerSecond*30,prior)==prior)
                    Hooks.Warn("invalid "+typeof(T).Name+" payload refused: "+ex.Message);
                return default(T);
            }
        }
    }
}
