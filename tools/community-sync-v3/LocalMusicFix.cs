using System;
using System.Reflection;
using BigAmbitionsMP;
using HarmonyLib;
using Player.Sound.Radio;

namespace BAMP.SyncFix
{
    [HarmonyPatch(typeof(LoudSpeakersManager), "PlayStation", new[] { typeof(RadioStation) })]
    public static class KeepLocalMusicStation
    {
        private static readonly FieldInfo Initialized = AccessTools.Field(typeof(LoudSpeakersManager), "_speakersInitialized");
        private static readonly FieldInfo CurrentStation = AccessTools.Field(typeof(LoudSpeakersManager), "_currentStation");
        private static readonly MethodInfo Stop = AccessTools.Method(typeof(LoudSpeakersManager), "Stop", Type.EmptyTypes);
        private static bool _logged;

        private static bool Prefix(LoudSpeakersManager __instance, RadioStation radioStation)
        {
            if ((!MPServer.IsRunning && !MPClient.InMpGame) || radioStation != RadioStation.LocalFiles)
                return true;
            if (!(bool)Initialized.GetValue(__instance)) return true;
            var player = InstanceBehavior<GameManager>.Instance?.radioPlayer;
            var station = player?.GetRadioStationData(radioStation);
            if (station == null || station.HasPlayableClips) return true;

            // Native PlayStation -> PlayNextStation writes the fallback into the
            // building registration. Local files belong to this computer: an empty
            // playlist must not change the shared station on every other computer.
            // Keep _currentStation so OnSongsLoaded resumes playback after a refresh.
            Stop.Invoke(__instance, null);
            CurrentStation.SetValue(__instance, RadioStation.LocalFiles);
            if (!_logged)
            {
                _logged = true;
                Hooks.Warn("local music unavailable on this computer; keeping the shared LocalFiles station and stopping only local speakers");
            }
            return false;
        }

    }
}
