using System;
using System.Threading;
namespace BAMP.SyncFix
{
    public sealed class MonitorLease
    {
        private readonly object gate;private bool held;
        public MonitorLease(object target){gate=target;Monitor.Enter(gate);held=true;}
        public void Release(){if(!held)return;held=false;Monitor.Exit(gate);}
    }
}
