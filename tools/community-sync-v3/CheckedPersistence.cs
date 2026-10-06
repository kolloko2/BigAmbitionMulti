using System;
using System.IO;

namespace BAMP.SyncFix
{
    public static class CheckedPersistence
    {
        private sealed class Frame{public Frame Previous;public bool Stored;public Exception Failure;}
        [ThreadStatic] private static Frame current;
        public static void ManifestStored(){if(current!=null)current.Stored=true;}
        public static void Failed(Exception failure){if(current!=null&&!current.Stored)current.Failure=failure;}
        public static void Run(Action persist)
        {
            var frame=new Frame{Previous=current};current=frame;
            try
            {
                persist();
                if(!frame.Stored)throw new IOException("Authoritative manifest was not committed; messages remain buffered.",frame.Failure);
            }
            finally{current=frame.Previous;}
        }
    }
}
