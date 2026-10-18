using System.IO;
using System.Runtime.InteropServices;
using ZogmaDeck.Core.Actions;

namespace ZogmaDeck.Services;

/// <summary>
/// Lecture audio par Media Foundation (décodage de la seule piste audio, ce qui ignore les pochettes d'album intégrées)
/// et waveOut (sortie). Le volume est appliqué aux échantillons par ZogmaDeck : exact dès le premier échantillon et
/// modifiable pendant la lecture. Remplace MediaPlayer de WPF, qui jouait à plein volume pendant plusieurs secondes les
/// MP3 contenant une pochette (traitée comme une vidéo). Appels COM par table virtuelle, sans dépendance.
/// </summary>
internal static class MediaFoundationAudio
{
    // ---------- Constantes Media Foundation (mfapi.h, mfreadwrite.h) ----------
    private const uint FirstAudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    private const uint EndOfStreamFlag = 0x2;
    private static readonly Guid MajorTypeKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid BitsPerSampleKey = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid ChannelsKey = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SampleRateKey = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BlockAlignKey = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    // Emplacements dans les tables virtuelles (IUnknown : 0-2 ; IMFAttributes : 3-32)
    private const int AttrGetUInt32 = 7, AttrSetUInt32 = 21, AttrSetGuid = 24;
    private const int ReaderSetStreamSelection = 4, ReaderGetCurrentMediaType = 6, ReaderSetCurrentMediaType = 7,
        ReaderSetCurrentPosition = 8, ReaderReadSample = 9, ReaderGetPresentationAttribute = 12;
    private const int SampleConvertToContiguousBuffer = 41;
    private const int BufferLock = 3, BufferUnlock = 4;

    private static readonly Lazy<int> Startup = new(() => MFStartup(0x00020070, 0), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Format PCM 16 bits décodé.</summary>
    internal readonly record struct PcmFormat(int SampleRate, int Channels, int BlockAlign);

    /// <summary>Lecteur de piste audio décodée en PCM 16 bits.</summary>
    internal sealed class Decoder : IDisposable
    {
        private IntPtr _reader;

        public Decoder(string path)
        {
            EnsureStarted();
            CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED ; sans effet si déjà initialisé
            Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out _reader), "ouverture");
            Check(Call<SetSelectionFn>(_reader, ReaderSetStreamSelection)(_reader, AllStreams, false), "sélection");
            Check(Call<SetSelectionFn>(_reader, ReaderSetStreamSelection)(_reader, FirstAudioStream, true), "piste audio");

            Check(MFCreateMediaType(out var type), "type");
            try
            {
                var major = MediaTypeAudio;
                var sub = AudioFormatPcm;
                var majorKey = MajorTypeKey;
                var subKey = SubtypeKey;
                var bitsKey = BitsPerSampleKey;
                Check(Call<SetGuidFn>(type, AttrSetGuid)(type, ref majorKey, ref major), "type");
                Check(Call<SetGuidFn>(type, AttrSetGuid)(type, ref subKey, ref sub), "type");
                Check(Call<SetUInt32Fn>(type, AttrSetUInt32)(type, ref bitsKey, 16), "type");
                Check(Call<SetMediaTypeFn>(_reader, ReaderSetCurrentMediaType)(_reader, FirstAudioStream, IntPtr.Zero, type), "décodage PCM");
            }
            finally
            {
                Marshal.Release(type);
            }

            Check(Call<GetMediaTypeFn>(_reader, ReaderGetCurrentMediaType)(_reader, FirstAudioStream, out var current), "format");
            try
            {
                Format = new PcmFormat((int)GetUInt32(current, SampleRateKey), (int)GetUInt32(current, ChannelsKey), (int)GetUInt32(current, BlockAlignKey));
            }
            finally
            {
                Marshal.Release(current);
            }
            Duration = ReadDuration();
        }

        public PcmFormat Format { get; }
        public TimeSpan? Duration { get; }

        private TimeSpan? ReadDuration()
        {
            var pv = new PropVariant();
            var key = DurationKey;
            if (Call<GetPresentationAttributeFn>(_reader, ReaderGetPresentationAttribute)(_reader, MediaSource, ref key, ref pv) < 0) return null;
            return pv.vt == 21 /* VT_UI8 */ ? TimeSpan.FromTicks(pv.value) : null; // unités de 100 ns
        }

        /// <summary>Positionne la lecture (unités de 100 ns, comme TimeSpan).</summary>
        public void Seek(TimeSpan position)
        {
            var pv = new PropVariant { vt = 20 /* VT_I8 */, value = position.Ticks };
            var timeFormat = Guid.Empty;
            Check(Call<SetPositionFn>(_reader, ReaderSetCurrentPosition)(_reader, ref timeFormat, ref pv), "positionnement");
        }

        /// <summary>Bloc suivant d'échantillons PCM et son horodatage ; null à la fin du fichier.</summary>
        public (byte[] Data, TimeSpan Time)? Read()
        {
            while (true)
            {
                Check(Call<ReadSampleFn>(_reader, ReaderReadSample)(_reader, FirstAudioStream, 0, out _, out var flags, out var timestamp, out var sample), "lecture");
                if (sample == IntPtr.Zero)
                {
                    if ((flags & EndOfStreamFlag) != 0) return null;
                    continue; // changement de format, intervalle vide…
                }
                try
                {
                    Check(Call<OutPtrFn>(sample, SampleConvertToContiguousBuffer)(sample, out var buffer), "tampon");
                    try
                    {
                        Check(Call<LockFn>(buffer, BufferLock)(buffer, out var data, out _, out var length), "tampon");
                        var bytes = new byte[length];
                        Marshal.Copy(data, bytes, 0, length);
                        Call<UnlockFn>(buffer, BufferUnlock)(buffer);
                        return (bytes, TimeSpan.FromTicks(timestamp));
                    }
                    finally
                    {
                        Marshal.Release(buffer);
                    }
                }
                finally
                {
                    Marshal.Release(sample);
                }
            }
        }

        public void Dispose()
        {
            if (_reader == IntPtr.Zero) return;
            Marshal.Release(_reader);
            _reader = IntPtr.Zero;
        }
    }

    /// <summary>Durée d'un fichier audio (null s'il est illisible).</summary>
    public static TimeSpan? GetDuration(string path)
    {
        try
        {
            using var d = new Decoder(path);
            return d.Duration;
        }
        catch (ActionException)
        {
            return null;
        }
    }

    /// <summary>
    /// Une lecture en cours, sur son propre fil d'exécution. <see cref="Gain"/> peut être modifié pendant la lecture
    /// (pris en compte au tampon suivant, ~40 ms).
    /// </summary>
    internal sealed class Playback
    {
        private const int BufferMs = 40, BufferCount = 4;
        private volatile bool _stop;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double _gain;

        public Playback(string path, double gain, TimeSpan start, TimeSpan? end)
        {
            _gain = Math.Clamp(gain, 0, 1);
            var thread = new Thread(() => Run(path, start, end)) { IsBackground = true, Name = "ZogmaDeck audio" };
            thread.Start();
        }

        public double Gain
        {
            get => Volatile.Read(ref _gain);
            set => Volatile.Write(ref _gain, Math.Clamp(value, 0, 1));
        }

        /// <summary>Ouverture réussie (ou erreur explicite) ; donne la durée du fichier.</summary>
        public Task Started => _started.Task;
        public TimeSpan? Duration { get; private set; }
        /// <summary>Fin de lecture : fin du fichier ou de l'extrait, arrêt, ou erreur.</summary>
        public Task Ended => _ended.Task;

        private volatile WaveOut? _output;

        /// <summary>Arrêt immédiat : les tampons déjà envoyés sont abandonnés (pas de chevauchement avec le son suivant).</summary>
        public void Stop()
        {
            _stop = true;
            _output?.Reset();
        }

        private void Run(string path, TimeSpan start, TimeSpan? end)
        {
            Decoder? decoder = null;
            WaveOut? output = null;
            try
            {
                decoder = new Decoder(path);
                Duration = decoder.Duration;
                if (start > TimeSpan.Zero && decoder.Duration is { } total && start >= total)
                    throw new ActionException($"Le début de l'extrait ({TimeFormat.Format(start.TotalSeconds)}) dépasse la durée de « {Path.GetFileName(path)} » ({TimeFormat.Format(total.TotalSeconds)}).");
                if (start > TimeSpan.Zero) decoder.Seek(start);
                var f = decoder.Format;
                output = new WaveOut(f, f.SampleRate * f.BlockAlign * BufferMs / 1000 / f.BlockAlign * f.BlockAlign, BufferCount);
                _output = output;
                if (_stop) output.Reset();
                _started.TrySetResult();

                // Octets restants de l'extrait (jusqu'à la fin du fichier si pas de fin).
                long remaining = end is { } e && e > start ? (long)((e - start).TotalSeconds * f.SampleRate) * f.BlockAlign : long.MaxValue;
                var pending = new Queue<byte[]>();
                var first = true;
                while (!_stop && remaining > 0)
                {
                    var block = decoder.Read();
                    if (block is null) break;
                    var (data, time) = block.Value;
                    var offset = 0;
                    if (first)
                    {
                        // Le positionnement tombe sur une trame : on retire ce qui précède le début demandé.
                        first = false;
                        if (start > time) offset = (int)Math.Min(data.Length, (long)((start - time).TotalSeconds * f.SampleRate) * f.BlockAlign);
                    }
                    var count = (int)Math.Min(data.Length - offset, remaining);
                    if (count <= 0) continue;
                    remaining -= count;
                    output.Write(data.AsSpan(offset, count), () => Gain, () => _stop);
                }
                if (!_stop) output.Drain(() => _stop);
            }
            catch (Exception ex)
            {
                var error = ex as ActionException ?? new ActionException($"Lecture impossible de « {Path.GetFileName(path)} » : {ex.Message}", ex);
                _started.TrySetException(error);
                _ended.TrySetException(error);
            }
            finally
            {
                _output = null;
                output?.Dispose();
                decoder?.Dispose();
                _started.TrySetResult();
                _ended.TrySetResult();
            }
        }
    }

    /// <summary>Sortie waveOut avec quelques tampons en rotation, volume appliqué aux échantillons 16 bits.</summary>
    private sealed class WaveOut : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly AutoResetEvent _done = new(false);
        private readonly IntPtr[] _headers;
        private readonly IntPtr[] _data;
        private readonly bool[] _queued;
        private readonly int _bufferBytes;
        private readonly byte[] _staging;
        private int _filled;
        private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
        private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.dwFlags));

        public WaveOut(PcmFormat f, int bufferBytes, int count)
        {
            _bufferBytes = Math.Max(f.BlockAlign, bufferBytes);
            _staging = new byte[_bufferBytes];
            var format = new WaveFormatEx
            {
                wFormatTag = 1, nChannels = (ushort)f.Channels, nSamplesPerSec = (uint)f.SampleRate,
                nAvgBytesPerSec = (uint)(f.SampleRate * f.BlockAlign), nBlockAlign = (ushort)f.BlockAlign, wBitsPerSample = 16,
            };
            var r = waveOutOpen(out _handle, 0xFFFFFFFF, ref format, _done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x00050000 /* CALLBACK_EVENT */);
            if (r != 0) throw new ActionException($"Aucune sortie audio disponible (erreur waveOut {r}).");
            _headers = new IntPtr[count];
            _data = new IntPtr[count];
            _queued = new bool[count];
            for (var i = 0; i < count; i++)
            {
                _data[i] = Marshal.AllocHGlobal(_bufferBytes);
                _headers[i] = Marshal.AllocHGlobal(HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { lpData = _data[i], dwBufferLength = (uint)_bufferBytes }, _headers[i], false);
                waveOutPrepareHeader(_handle, _headers[i], (uint)HeaderSize);
            }
        }

        private bool IsDone(int i) => (Marshal.ReadInt32(_headers[i], FlagsOffset) & 0x1 /* WHDR_DONE */) != 0;

        public void Write(ReadOnlySpan<byte> data, Func<double> gain, Func<bool> stop)
        {
            while (data.Length > 0 && !stop())
            {
                var n = Math.Min(data.Length, _bufferBytes - _filled);
                data[..n].CopyTo(_staging.AsSpan(_filled));
                _filled += n;
                data = data[n..];
                if (_filled == _bufferBytes) Flush(gain(), stop);
            }
        }

        /// <summary>Envoie le dernier tampon partiel et attend la fin de la lecture.</summary>
        public void Drain(Func<bool> stop)
        {
            if (_filled > 0) Flush(-1, stop);
            while (!stop() && _queued.Where((q, i) => q && !IsDone(i)).Any()) _done.WaitOne(50);
        }

        private double _lastGain = 1;

        private void Flush(double gain, Func<bool> stop)
        {
            if (gain < 0) gain = _lastGain;
            _lastGain = gain;
            var index = -1;
            while (index < 0 && !stop())
            {
                for (var i = 0; i < _queued.Length; i++)
                {
                    if (!_queued[i] || IsDone(i))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0) _done.WaitOne(50);
            }
            if (index < 0) return;

            var samples = MemoryMarshal.Cast<byte, short>(_staging.AsSpan(0, _filled - _filled % 2));
            if (gain < 0.9999)
            {
                for (var i = 0; i < samples.Length; i++) samples[i] = (short)Math.Round(samples[i] * gain);
            }
            Marshal.Copy(_staging, 0, _data[index], _filled);
            // Réécrit l'en-tête (longueur, indicateurs) avant de le renvoyer.
            Marshal.WriteInt32(_headers[index], (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.dwBufferLength)), _filled);
            Marshal.WriteInt32(_headers[index], FlagsOffset, Marshal.ReadInt32(_headers[index], FlagsOffset) & ~0x1);
            var r = waveOutWrite(_handle, _headers[index], (uint)HeaderSize);
            if (r != 0) throw new ActionException($"Lecture audio interrompue (erreur waveOut {r}).");
            _queued[index] = true;
            _filled = 0;
        }

        private int _disposed;

        /// <summary>Interrompt la lecture (appelable depuis un autre fil d'exécution).</summary>
        public void Reset()
        {
            if (Volatile.Read(ref _disposed) == 0) waveOutReset(_handle);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            waveOutReset(_handle);
            for (var i = 0; i < _headers.Length; i++)
            {
                waveOutUnprepareHeader(_handle, _headers[i], (uint)HeaderSize);
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_data[i]);
            }
            waveOutClose(_handle);
            _done.Dispose();
        }
    }

    // ---------- Interop ----------
    private static void EnsureStarted()
    {
        if (Startup.Value < 0) throw new ActionException($"Media Foundation indisponible (erreur 0x{Startup.Value:X8}).");
    }

    private static void Check(int hr, string step)
    {
        if (hr >= 0) return;
        throw new ActionException(hr switch
        {
            unchecked((int)0xC00D36C4) => "Format audio non pris en charge par Windows.",
            unchecked((int)0x80070002) or unchecked((int)0x80070003) => "Fichier audio introuvable.",
            _ => $"Erreur Media Foundation ({step}, 0x{hr:X8}).",
        });
    }

    private static uint GetUInt32(IntPtr attributes, Guid key) =>
        Call<GetUInt32Fn>(attributes, AttrGetUInt32)(attributes, ref key, out var v) >= 0 ? v : 0;

    private static T Call<T>(IntPtr comObject, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public long value;
        public IntPtr padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSelectionFn(IntPtr self, uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuidFn(IntPtr self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt32Fn(IntPtr self, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt32Fn(IntPtr self, ref Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetMediaTypeFn(IntPtr self, uint stream, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetMediaTypeFn(IntPtr self, uint stream, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetPositionFn(IntPtr self, ref Guid timeFormat, ref PropVariant position);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadSampleFn(IntPtr self, uint stream, uint flags, out uint actualStream, out uint streamFlags, out long timestamp, out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPresentationAttributeFn(IntPtr self, uint stream, ref Guid key, ref PropVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutPtrFn(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockFn(IntPtr self, out IntPtr data, out int maxLength, out int currentLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UnlockFn(IntPtr self);

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr handle);
}
