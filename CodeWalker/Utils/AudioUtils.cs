using CodeWalker.GameFiles;
using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAudio2;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeWalker.Utils
{

    public class AudioPlayer
    {
        private XAudio2 xAudio2;
        private MasteringVoice masteringVoice;

        public class AudioVoice
        {
            public AwcStream audio;
            public WaveFormat waveFormat;
            public AudioBuffer audioBuffer;
            public SourceVoice sourceVoice;
            public float[] outputMatrix = new[] { 1.0f, 1.0f }; //left/right channel output levels
            public float trackLength;
        }
        private AudioVoice[] voices = new AudioVoice[0];

        public enum PlayerState { Stopped, Playing, Paused };
        public PlayerState State { get; private set; } = PlayerState.Stopped;

        private Stopwatch playtimer = new Stopwatch();
        private int playBeginMs;
        private float trackLength;
        public bool trackFinished;

        private float volume = 1.0f;

        public int PlayTimeMS
        {
            get
            {
                return (int)playtimer.Elapsed.TotalMilliseconds + playBeginMs;
            }
        }
        public int TotalTimeMS
        {
            get
            {
                return (int)(trackLength * 1000);
            }
        }

        static AudioPlayer()
        {
            Dbg("static ctor — SharpDX.XAudio2 assembly location:");
            try
            {
                var asm = typeof(SharpDX.XAudio2.XAudio2).Assembly;
                Dbg("  SharpDX.XAudio2 from: " + asm.Location);
                var an = asm.GetName();
                Dbg($"  version={an.Version}, TFM via name={an.Name}");
            }
            catch (Exception e) { Dbg("  (failed to read SharpDX assembly) " + e.Message); }

            try
            {
                using (var tmp = new XAudio2())
                {
                    Dbg("  XAudio2 instance created OK");
                }
            }
            catch (Exception e) { Dbg("  (failed to create XAudio2 in static ctor) " + e); }
        }

        [Conditional("DEBUG")]
        private static void Dbg(string msg) { Debug.WriteLine("[CW AUDIO] " + msg); }

        [Conditional("DEBUG")]
        private static void DbgVoice(int idx, AudioVoice v, string label)
        {
            if (v == null) { Dbg($"voice[{idx}] {label}: (null)"); return; }
            var fmt = v.waveFormat;
            string fmtStr = fmt == null ? "(null)"
                : $"tag={fmt.Encoding}, hz={fmt.SampleRate}, ch={fmt.Channels}, bits={fmt.BitsPerSample}, blockAlign={fmt.BlockAlign}, avgBps={fmt.AverageBytesPerSecond}";
            int audioBytes = v.audioBuffer?.AudioBytes ?? -1;
            int playBegin = v.audioBuffer?.PlayBegin ?? -1;
            int? pcmLen = v.audio?.GetPcmData()?.Length;
            Dbg($"voice[{idx}] {label}: fmt=[{fmtStr}] audioBytes={audioBytes} playBegin={playBegin} pcmLen={pcmLen?.ToString() ?? "(null)"} srcVoice={(v.sourceVoice == null ? "null" : "ok")}");
        }

        public void LoadAudio(params AwcStream[] audios)
        {
            Dbg($"=== LoadAudio start — {audios?.Length ?? 0} streams ===");

            if (xAudio2 == null)
            {
                try
                {
                    xAudio2 = new XAudio2();
                    masteringVoice = new MasteringVoice(xAudio2);
                    Dbg("  XAudio2 + MasteringVoice created");
                }
                catch (Exception e)
                {
                    Dbg("  XAudio2 / MasteringVoice creation FAILED: " + e);
                    throw;
                }
            }

            if ((voices == null) || (voices.Length != audios.Length))
            {
                voices = new AudioVoice[audios.Length];
                for (int i = 0; i < audios.Length; i++)
                {
                    voices[i] = new AudioVoice();
                }
                Dbg($"  allocated {voices.Length} voice slots");
            }

            trackLength = 0;
            for (int i = 0; i < audios.Length; i++)
            {
                var voice = voices[i];
                var audio = audios[i];
                Dbg($"  LoadAudio loop i={i} audio={(audio == null ? "null" : audio.ToString())}");
                if (audio == null)
                {
                    voice.audio = null;
                    voice.audioBuffer = null;
                    voice.waveFormat = null;
                    continue;
                }
                if (audio == voice.audio)
                {
                    DbgVoice(i, voice, "unchanged");
                    continue;
                }

                voice.audioBuffer?.Stream?.Dispose();
                voice.audioBuffer = null;

                voice.audio = audio;
                voice.trackLength = audio.Length;
                trackLength = Math.Max(trackLength, voice.trackLength);
                Dbg($"  voice[{i}] audio.Length (seconds) = {audio.Length}, SamplesPerSecond={audio.SamplesPerSecond}");

                byte[] pcmData = null;
                try { pcmData = audio.GetPcmData(); }
                catch (Exception e)
                {
                    Dbg($"  voice[{i}] GetPcmData threw: {e}");
                    throw;
                }
                Dbg($"  voice[{i}] GetPcmData returned {pcmData?.Length ?? 0} bytes");

                try
                {
                    var codecField = audio.GetType().GetProperty("FormatChunk")?.GetValue(audio);
                    var streamFmtField = audio.GetType().GetProperty("StreamFormat")?.GetValue(audio);
                    Dbg($"  voice[{i}] FormatChunk={codecField}, StreamFormat={streamFmtField}");
                }
                catch { }

                WaveFormat fmt = null;
                try
                {
                    var wavStream = audio.GetWavStream();
                    var soundStream = new SoundStream(wavStream);
                    fmt = soundStream.Format;
                    int ssLen = (int)soundStream.Length;
                    uint[] dpi = soundStream.DecodedPacketsInfo;
                    Dbg($"  voice[{i}] SoundStream: format=tag={fmt.Encoding} hz={fmt.SampleRate} ch={fmt.Channels} bits={fmt.BitsPerSample} blockAlign={fmt.BlockAlign} avgBps={fmt.AverageBytesPerSecond} dataLen={ssLen} decodedPacketsInfo={(dpi == null ? "null" : dpi.Length + " entries")}");
                    soundStream.Close();
                    wavStream.Close();
                }
                catch (Exception e)
                {
                    Dbg($"  voice[{i}] SoundStream parse threw: {e}");
                    throw;
                }
                voice.waveFormat = fmt;

                voice.audioBuffer = new AudioBuffer
                {
                    Stream = DataStream.Create(pcmData, true, true, 0, true),
                    AudioBytes = pcmData.Length,
                    Flags = BufferFlags.EndOfStream
                };
                DbgVoice(i, voice, "after LoadAudio");
            }

            Dbg($"=== LoadAudio done — trackLength={trackLength} ===");
        }

        private void CreateSourceVoices(float playBegin = 0)
        {
            Dbg($"=== CreateSourceVoices start — voices.Length={voices.Length}, playBegin={playBegin} ===");

            if (playBegin > 0)
            {
                foreach (var voice in voices)
                {
                    if (voice?.audioBuffer == null || voice.waveFormat == null) continue;

                    int sampleRate = voice.waveFormat.SampleRate;
                    int blockAlign = Math.Max(1, voice.waveFormat.BlockAlign);
                    int totalFrames = voice.audioBuffer.AudioBytes / blockAlign;

                    int playBeginSamples = (int)(sampleRate * playBegin);
                    if (totalFrames > 0 && playBeginSamples >= totalFrames)
                    {
                        Dbg($"  voice: playBegin {playBeginSamples} >= totalFrames {totalFrames} — MARKING FOR SKIP");
                        voice.sourceVoice = null; // sentinel: skip this voice
                        voice.audioBuffer.PlayBegin = 0;
                        continue;
                    }
                    voice.audioBuffer.PlayBegin = playBeginSamples;
                }
                if (playtimer.IsRunning) playtimer.Restart(); else playtimer.Reset();
                playBeginMs = (int)(playBegin * 1000);
            }
            else
            {
                foreach (var voice in voices)
                {
                    if (voice?.audioBuffer != null) voice.audioBuffer.PlayBegin = 0;
                }
                playBeginMs = 0;
            }

            trackFinished = false;
            int voiceIdx = 0;
            int skippedCount = 0;
            int submittedCount = 0;
            foreach (var voice in voices)
            {
                DbgVoice(voiceIdx, voice, "entering submit loop");
                if (voice == null) { Dbg($"  voice[{voiceIdx}] is null, skipping"); voiceIdx++; skippedCount++; continue; }

                if (playBegin > 0 && voice.sourceVoice == null && voice.audioBuffer != null)
                {
                    Dbg($"  voice[{voiceIdx}] SKIPPED (playBegin out of range)");
                    voiceIdx++; skippedCount++; continue;
                }

                if (voice.audioBuffer == null) { Dbg($"  voice[{voiceIdx}] audioBuffer is null, skipping"); voiceIdx++; skippedCount++; continue; }
                if (voice.waveFormat == null) { Dbg($"  voice[{voiceIdx}] waveFormat is null, skipping"); voiceIdx++; skippedCount++; continue; }
                if (voice.audioBuffer.AudioBytes <= 0) { Dbg($"  voice[{voiceIdx}] AudioBytes={voice.audioBuffer.AudioBytes} <=0, skipping"); voiceIdx++; skippedCount++; continue; }
                if (voice.audioBuffer.Stream == null) { Dbg($"  voice[{voiceIdx}] Stream is null, skipping"); voiceIdx++; skippedCount++; continue; }

                int blockAlign = Math.Max(1, voice.waveFormat.BlockAlign);
                if (blockAlign > 1 && (voice.audioBuffer.AudioBytes % blockAlign) != 0)
                {
                    int before = voice.audioBuffer.AudioBytes;
                    voice.audioBuffer.AudioBytes -= (voice.audioBuffer.AudioBytes % blockAlign);
                    Dbg($"  voice[{voiceIdx}] rounded AudioBytes {before} -> {voice.audioBuffer.AudioBytes} (blockAlign={blockAlign})");
                }

                var codec = voice.audio?.StreamFormat?.Codec ?? voice.audio?.FormatChunk?.Codec ?? AwcCodecType.PCM;
                if (codec != AwcCodecType.PCM && codec != AwcCodecType.ADPCM)
                {
                    Dbg($"  voice[{voiceIdx}] WARNING: codec={codec} ({(int)codec}) is NOT PCM or ADPCM — data is compressed and will play as static/silence");
                }

                Dbg($"  voice[{voiceIdx}] calling SourceVoice ctor: fmt=tag={voice.waveFormat.Encoding} hz={voice.waveFormat.SampleRate} ch={voice.waveFormat.Channels} bits={voice.waveFormat.BitsPerSample}");
                SourceVoice sourceVoice = new SourceVoice(xAudio2, voice.waveFormat, true);
                Dbg($"  voice[{voiceIdx}] SourceVoice ctor OK");

                Dbg($"  voice[{voiceIdx}] calling SubmitSourceBuffer: audioBytes={voice.audioBuffer.AudioBytes} playBegin={voice.audioBuffer.PlayBegin} flags={voice.audioBuffer.Flags}");
                sourceVoice.SubmitSourceBuffer(voice.audioBuffer, null);
                Dbg($"  voice[{voiceIdx}] SubmitSourceBuffer OK");

                sourceVoice.BufferEnd += (context) => trackFinished = true;
                sourceVoice.SetVolume(volume);
                sourceVoice.SetOutputMatrix(1, 2, voice.outputMatrix);
                voice.sourceVoice = sourceVoice;
                submittedCount++;
                Dbg($"  voice[{voiceIdx}] started OK");
                voiceIdx++;
            }
            Dbg($"=== CreateSourceVoices done — {submittedCount} submitted, {skippedCount} skipped ===");
        }

        private void SetPlayerState(PlayerState newState)
        {
            if (State != newState)
            {
                switch (newState)
                {
                    case PlayerState.Playing:
                        if (State == PlayerState.Stopped)
                        {
                            playtimer.Reset();
                        }
                        playtimer.Start();
                        break;
                    case PlayerState.Paused:
                        playtimer.Stop();
                        break;
                    case PlayerState.Stopped:
                        playtimer.Stop();
                        break;
                }

                State = newState;
            }
        }

        public void SetVolume(float v)
        {
            volume = v;
            if (State == PlayerState.Playing)
            {
                foreach (var voice in voices)
                {
                    if (voice?.sourceVoice == null) continue;
                    voice.sourceVoice.SetVolume(v);
                }
            }
        }

        public void SetOutputMatrix(int v, float l, float r)
        {
            var voice = voices[v];
            voice.outputMatrix[0] = l;
            voice.outputMatrix[1] = r;
            if (State == PlayerState.Playing)
            {
                voice.sourceVoice.SetOutputMatrix(1, 2, voice.outputMatrix);
            }
        }

        public void DisposeAudio()
        {
            if (xAudio2 != null)
            {
                masteringVoice.Dispose();
                xAudio2.Dispose();
            }
            foreach (var voice in voices)
            {
                voice?.audioBuffer?.Stream?.Dispose();
            }
        }


        public void Stop()
        {
            if (State != PlayerState.Stopped)
            {
                foreach (var voice in voices)
                {
                    if (voice?.sourceVoice == null) continue;
                    try { voice.sourceVoice.DestroyVoice(); } catch { }
                    try { voice.sourceVoice.Dispose(); } catch { }
                    voice.sourceVoice = null;
                }
                SetPlayerState(PlayerState.Stopped);
            }
        }

        public void Play(float playBegin = 0)
        {
            Dbg($">>> Play(playBegin={playBegin}) called — State before Stop={State}");
            Stop();
            CreateSourceVoices(playBegin);
            int started = 0;
            foreach (var voice in voices)
            {
                if (voice?.sourceVoice == null) continue;
                voice.sourceVoice.Start();
                started++;
            }
            Dbg($"  Play: started {started}/{voices.Length} source voices");
            SetPlayerState(PlayerState.Playing);
        }

        public void Seek(float playBegin = 0)
        {
            if (State == PlayerState.Playing)
            {
                Play(playBegin);
            }
            else if (State == PlayerState.Paused)
            {
                var state = State;
                Stop();
                CreateSourceVoices(playBegin);
                State = state;
            }
        }

        public void Pause()
        {
            if (State == PlayerState.Playing)
            {
                foreach (var voice in voices)
                {
                    if (voice?.sourceVoice == null) continue;
                    voice.sourceVoice.Stop();
                }
                SetPlayerState(PlayerState.Paused);
            }
        }

        public void Resume()
        {
            if (State == PlayerState.Paused)
            {
                foreach (var voice in voices)
                {
                    if (voice?.sourceVoice == null) continue;
                    voice.sourceVoice.Start();
                }
                SetPlayerState(PlayerState.Playing);
            }
        }

    }



    public class AudioDatabase
    {

        public bool IsInited { get; set; }

        public Dictionary<uint, Dat54Sound> SoundsDB { get; set; }
        public Dictionary<uint, Dat151RelData> GameDB { get; set; }
        public Dictionary<uint, RpfFileEntry> ContainerDB { get; set; }

        public void Init(GameFileCache gameFileCache, bool sounds = true, bool game = true)
        {


            var rpfman = gameFileCache.RpfMan;

            var datrelentries = new Dictionary<uint, RpfFileEntry>();
            var awcentries = new Dictionary<uint, RpfFileEntry>();
            void addRpfDatRels(RpfFile rpffile)
            {
                if (rpffile.AllEntries == null) return;
                foreach (var entry in rpffile.AllEntries)
                {
                    if (entry is RpfFileEntry)
                    {
                        var fentry = entry as RpfFileEntry;
                        //if (entry.NameLower.EndsWith(".rel"))
                        //{
                        //    datrels[entry.NameHash] = fentry;
                        //}
                        if (sounds && entry.NameLower.EndsWith(".dat54.rel"))
                        {
                            datrelentries[entry.NameHash] = fentry;
                        }
                        if (game && entry.NameLower.EndsWith(".dat151.rel"))
                        {
                            datrelentries[entry.NameHash] = fentry;
                        }
                    }
                }
            }
            void addRpfAwcs(RpfFile rpffile)
            {
                if (rpffile.AllEntries == null) return;
                foreach (var entry in rpffile.AllEntries)
                {
                    if (entry is RpfFileEntry)
                    {
                        var fentry = entry as RpfFileEntry;
                        if (entry.NameLower.EndsWith(".awc"))
                        {
                            var shortname = entry.GetShortNameLower();
                            var parentname = entry.Parent?.GetShortNameLower() ?? "";
                            if (string.IsNullOrEmpty(parentname) && (entry.Parent?.File != null))
                            {
                                parentname = entry.Parent.File.NameLower;
                                int ind = parentname.LastIndexOf('.');
                                if (ind > 0)
                                {
                                    parentname = parentname.Substring(0, ind);
                                }
                            }
                            var contname = parentname + "/" + shortname;
                            var hash = JenkHash.GenHash(contname);
                            awcentries[hash] = fentry;
                        }
                    }
                }
            }

            var audrpf = rpfman.FindRpfFile("x64\\audio\\audio_rel.rpf");
            if (audrpf != null)
            {
                addRpfDatRels(audrpf);
            }
            foreach (var baserpf in gameFileCache.BaseRpfs)
            {
                addRpfAwcs(baserpf);
            }
            if (gameFileCache.EnableDlc)
            {
                var updrpf = rpfman.FindRpfFile("update\\update.rpf");
                if (updrpf != null)
                {
                    addRpfDatRels(updrpf);
                }
                foreach (var dlcrpf in gameFileCache.DlcActiveRpfs) //load from current dlc rpfs
                {
                    addRpfDatRels(dlcrpf);
                    addRpfAwcs(dlcrpf);
                }
            }



            var soundsdb = new Dictionary<uint, Dat54Sound>();
            var gamedb = new Dictionary<uint, Dat151RelData>();
            foreach (var datentry in datrelentries.Values)
            {
                var relfile = rpfman.GetFile<RelFile>(datentry);
                if (relfile?.RelDatas != null)
                {
                    foreach (var rd in relfile.RelDatas)
                    {
                        if (rd is Dat54Sound sd)
                        {
                            soundsdb[sd.NameHash] = sd;
                        }
                        else if (rd is Dat151RelData gd)
                        {
                            gamedb[gd.NameHash] = gd;
                        }
                    }
                }
            }

            ContainerDB = awcentries;
            if (sounds) SoundsDB = soundsdb;
            if (game) GameDB = gamedb;

            IsInited = true;
        }


    }


}
