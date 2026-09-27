// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Audio;
using MP3Sharp;
using System;

namespace ClassicUO.IO.Audio
{
    public class UOMusic : Sound
    {
        private const int NUMBER_OF_PCM_BYTES_TO_READ_PER_CHUNK = 0x8000; // 32768 bytes: 0.37 s at 22 kHz stereo, 0.17 s at 48 kHz
        // Buffers queued ahead. Refills happen on the main thread, so this is how long a stall
        // (entering the world, a GC) can last without a dropout: ~1 s at 48 kHz.
        private const int PENDING_BUFFERS = 6;
        private bool m_Playing;
        private readonly bool m_Repeat;
        private MP3Stream m_Stream;
        private readonly byte[] m_WaveBuffer = new byte[NUMBER_OF_PCM_BYTES_TO_READ_PER_CHUNK];


        public UOMusic(int index, string name, bool loop, string fileName) : base(name, index)
        {
            m_Repeat = loop;
            m_Playing = false;
            Channels = AudioChannels.Stereo;
            Delay = 0;

            Path = fileName;
        }

        private string Path { get; }

        public void Update()
        {
            // sanity - if the buffer empties, we will lose our sound effect. Thus we must continually check if it is dead.
            OnBufferNeeded(null, null);
        }

        protected override ArraySegment<byte> GetBuffer()
        {
            try
            {
                if (m_Playing && SoundInstance != null)
                {
                    int bytesReturned = m_Stream.Read(m_WaveBuffer, 0, m_WaveBuffer.Length);

                    if (bytesReturned != NUMBER_OF_PCM_BYTES_TO_READ_PER_CHUNK)
                    {
                        if (m_Repeat)
                        {
                            // Loop: fill the rest of this chunk from the start, and keep it (the
                            // count used to stay at the tail's length, dropping the loop's first
                            // part - an audible skip at every loop).
                            m_Stream.Position = 0;
                            bytesReturned += m_Stream.Read(m_WaveBuffer, bytesReturned, m_WaveBuffer.Length - bytesReturned);
                        }
                        else
                        {
                            if (bytesReturned == 0)
                            {
                                Stop();
                            }
                        }
                    }

                    return new ArraySegment<byte>(m_WaveBuffer, 0, bytesReturned);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }

            Stop();

            return ArraySegment<byte>.Empty;
        }

        protected override void OnBufferNeeded(object sender, EventArgs e)
        {
            if (m_Playing)
            {
                if (SoundInstance == null)
                {
                    Stop();

                    return;
                }

                while (SoundInstance.PendingBufferCount < PENDING_BUFFERS)
                {
                    var buffer = GetBuffer();

                    if (SoundInstance.IsDisposed || buffer.Count == 0)
                    {
                        break;
                    }

                    SoundInstance.SubmitBuffer(buffer.Array, buffer.Offset, buffer.Count);
                }
            }
        }

        protected override void BeforePlay()
        {
            if (m_Playing)
            {
                Stop();
            }

            try
            {
                if (m_Stream != null)
                {
                    m_Stream.Close();
                    m_Stream = null;
                }

                m_Stream = new MP3Stream(Path, NUMBER_OF_PCM_BYTES_TO_READ_PER_CHUNK);
                Frequency = m_Stream.Frequency;

                m_Playing = true;
            }
            catch
            {
                // file in use or access denied.
                m_Playing = false;
            }
        }

        protected override void AfterStop()
        {
            if (m_Playing)
            {
                m_Playing = false;
                m_Stream?.Close();
                m_Stream = null;
            }
        }
    }
}
