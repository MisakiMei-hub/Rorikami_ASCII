using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RouriAscii
{
    public static class Player
    {
        [StructLayout(LayoutKind.Sequential)] struct Coord
        {
            public short X, Y;
            public Coord(int x, int y) { X = (short)x; Y = (short)y; }
        }
        [StructLayout(LayoutKind.Sequential)] struct Rect
        { public short Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)] struct Cell
        {
            [FieldOffset(0)] public char Character;
            [FieldOffset(2)] public ushort Attribute;
        }
        [StructLayout(LayoutKind.Sequential)] struct BufferInfo
        {
            public Coord Size, Cursor;
            public ushort Attributes;
            public Rect Window;
            public Coord Maximum;
        }
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleScreenBufferInfo(IntPtr output, out BufferInfo info);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool WriteConsoleOutputW(IntPtr output, Cell[] cells, Coord size, Coord origin, ref Rect rectangle);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr handle, uint mode);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern uint mciSendStringW(string command, StringBuilder result, int length, IntPtr callback);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern uint mciGetErrorStringW(uint error, StringBuilder result, int length);

        sealed class Movie
        {
            public int Width, Height, Fps, DurationMs, OffsetMs, Count, Stride;
            public byte[] Data;
            public Movie(string path)
            {
                using (var file = File.OpenRead(path))
                using (var zip = new GZipStream(file, CompressionMode.Decompress))
                using (var memory = new MemoryStream())
                {
                    zip.CopyTo(memory);
                    Data = memory.ToArray();
                }
                if (Data.Length < 28 || Encoding.ASCII.GetString(Data, 0, 8) != "ASCII01\n")
                    throw new InvalidDataException("Invalid ASCII movie header.");
                Width = BitConverter.ToInt32(Data, 8); Height = BitConverter.ToInt32(Data, 12);
                Fps = BitConverter.ToInt32(Data, 16); DurationMs = BitConverter.ToInt32(Data, 20);
                OffsetMs = BitConverter.ToInt32(Data, 24);
                if (Width < 1 || Width > 500 || Height < 1 || Height > 200 || Fps < 1 || Fps > 120 || DurationMs <= 0)
                    throw new InvalidDataException("Invalid movie dimensions or timing.");
                Stride = checked(Width * Height * 2);
                if ((Data.Length - 28) % Stride != 0 || Data.Length == 28)
                    throw new InvalidDataException("Truncated ASCII movie.");
                Count = (Data.Length - 28) / Stride;
            }
        }

        static string Audio(string command)
        {
            var result = new StringBuilder(256);
            uint error = mciSendStringW(command, result, result.Capacity, IntPtr.Zero);
            if (error != 0)
            {
                var message = new StringBuilder(256);
                mciGetErrorStringW(error, message, message.Capacity);
                throw new InvalidOperationException("Audio: " + message);
            }
            return result.ToString();
        }

        static void CheckWave(string path)
        {
            using (var file = File.OpenRead(path))
            using (var reader = new BinaryReader(file))
            {
                if (file.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
                    throw new InvalidDataException("Invalid audio WAV.");
                file.Position = 8;
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
                    throw new InvalidDataException("Invalid audio WAV.");
            }
        }

        public static void Verify(string moviePath, string audioPath)
        {
            var movie = new Movie(moviePath);
            CheckWave(audioPath);
            for (int i = 28; i < movie.Data.Length; i += 2)
                if (movie.Data[i] < 32 || movie.Data[i] > 126 || movie.Data[i + 1] > 15)
                    throw new InvalidDataException("Invalid ASCII character or color.");
            Console.WriteLine("OK: {0}x{1}, {2} fps, {3} frames, video {4:F2}s, audio {5:F2}s, offset {6}ms",
                movie.Width, movie.Height, movie.Fps, movie.Count, (double)movie.Count / movie.Fps,
                movie.DurationMs / 1000.0, movie.OffsetMs);
        }

        public static void Snapshot(string path, int frame, string output)
        {
            var movie = new Movie(path);
            if (frame < 0 || frame >= movie.Count) throw new ArgumentOutOfRangeException("frame");
            var text = new StringBuilder();
            int start = 28 + frame * movie.Stride;
            for (int y = 0; y < movie.Height; y++)
            {
                for (int x = 0; x < movie.Width; x++) text.Append((char)movie.Data[start + (y * movie.Width + x) * 2]);
                text.Append("\r\n");
            }
            File.WriteAllText(output, text.ToString(), Encoding.ASCII);
            Console.WriteLine("Saved ASCII frame {0}: {1}", frame, output);
        }

        public static void Play(string moviePath, string audioPath, bool color, bool mute, double previewSeconds)
        {
            var movie = new Movie(moviePath);
            CheckWave(audioPath);
            IntPtr output = GetStdHandle(-11), input = GetStdHandle(-10);
            BufferInfo original;
            if (!GetConsoleScreenBufferInfo(output, out original))
                throw new InvalidOperationException("Open this player in a PowerShell console window.");
            string title = Console.Title;
            bool cursor = Console.CursorVisible, oldControl = Console.TreatControlCAsInput;
            uint oldMode;
            bool hasMode = GetConsoleMode(input, out oldMode);
            bool opened = false;
            var clock = new Stopwatch();
            int pausedAt = 0, paintedFrame = -1, paintedWidth = -1, paintedHeight = -1;
            bool paused = false, ended = false, dirty = true;
            int durationMs = movie.DurationMs;
            Cell[] cells = null;
            try
            {
                Console.Title = "Rorikami ASCII | Space: pause / replay | R: replay | Q/Esc: exit";
                Console.CursorVisible = false;
                Console.TreatControlCAsInput = true;
                // Disable Quick Edit: selecting text must not stall video while audio plays.
                if (hasMode) SetConsoleMode(input, (oldMode | 0x80u) & ~0x40u);
                if (!mute)
                {
                    Audio("open \"" + audioPath + "\" type waveaudio alias rouri_ascii_audio");
                    opened = true;
                    Audio("set rouri_ascii_audio time format milliseconds");
                    durationMs = Math.Min(durationMs, Int32.Parse(Audio("status rouri_ascii_audio length")));
                    Audio("play rouri_ascii_audio from 0");
                }
                clock.Start();
                while (true)
                {
                    while (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Q ||
                            (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)) return;
                        if (key.Key == ConsoleKey.Spacebar)
                        {
                            if (ended)
                            {
                                if (opened) { Audio("stop rouri_ascii_audio"); Audio("play rouri_ascii_audio from 0"); }
                                clock.Restart(); paused = false; ended = false; pausedAt = 0;
                            }
                            else if (paused)
                            {
                                if (opened) Audio("resume rouri_ascii_audio");
                                clock.Start(); paused = false;
                            }
                            else
                            {
                                if (opened) { Audio("pause rouri_ascii_audio"); pausedAt = Int32.Parse(Audio("status rouri_ascii_audio position")); }
                                else pausedAt = (int)clock.ElapsedMilliseconds;
                                clock.Stop(); paused = true;
                            }
                            dirty = true;
                        }
                        if (key.Key == ConsoleKey.R)
                        {
                            if (opened) { Audio("stop rouri_ascii_audio"); Audio("play rouri_ascii_audio from 0"); }
                            clock.Restart(); paused = false; ended = false; pausedAt = 0; dirty = true;
                        }
                    }
                    int position = (paused || ended) ? pausedAt : (opened ? Int32.Parse(Audio("status rouri_ascii_audio position")) : (int)clock.ElapsedMilliseconds);
                    // Preview mode is an explicitly bounded run; normal playback remains open at the end.
                    if (!paused && !ended && previewSeconds > 0 && position >= previewSeconds * 1000) break;
                    if (!paused && !ended && (position >= durationMs ||
                        (opened && clock.ElapsedMilliseconds > 100 && Audio("status rouri_ascii_audio mode") == "stopped")))
                    {
                        if (previewSeconds > 0) break;
                        if (opened) Audio("stop rouri_ascii_audio");
                        clock.Stop(); ended = true; pausedAt = durationMs; position = durationMs; dirty = true;
                    }
                    int frame = Math.Max(0, Math.Min(movie.Count - 1, (int)((long)Math.Max(0, position - movie.OffsetMs) * movie.Fps / 1000)));
                    BufferInfo info;
                    if (!GetConsoleScreenBufferInfo(output, out info)) throw new IOException("Console closed.");
                    int width = info.Window.Right - info.Window.Left + 1;
                    int height = info.Window.Bottom - info.Window.Top + 1;
                    if (width < 10 || height < 5) { Thread.Sleep(50); continue; }
                    if (width != paintedWidth || height != paintedHeight)
                    {
                        cells = new Cell[width * height];
                        paintedWidth = width; paintedHeight = height; dirty = true;
                    }
                    if (frame != paintedFrame || dirty)
                    {
                        for (int i = 0; i < cells.Length; i++) { cells[i].Character = ' '; cells[i].Attribute = 7; }
                        // Fill every available cell; one row is reserved for playback controls.
                        // The source matte is removed at conversion time, not added back by the renderer.
                        int drawWidth = width, drawHeight = height - 1;
                        int start = 28 + frame * movie.Stride;
                        for (int y = 0; y < drawHeight; y++)
                        {
                            int sourceY = y * movie.Height / drawHeight;
                            for (int x = 0; x < drawWidth; x++)
                            {
                                int source = start + (sourceY * movie.Width + x * movie.Width / drawWidth) * 2;
                                int dest = y * width + x;
                                cells[dest].Character = (char)movie.Data[source];
                                cells[dest].Attribute = color ? movie.Data[source + 1] : (ushort)15;
                            }
                        }
                        string status = String.Format(" {0} {1:mm\\:ss}/{2:mm\\:ss} | Space: {3} | R: replay | Q/Esc: exit{4}",
                            ended ? "ENDED" : (paused ? "PAUSED" : "PLAY"), TimeSpan.FromMilliseconds(position),
                            TimeSpan.FromSeconds(Math.Ceiling(durationMs / 1000.0)), ended ? "replay" : "pause",
                            mute ? " | MUTE" : "");
                        for (int x = 0; x < Math.Min(width - 1, status.Length); x++)
                        { cells[(height - 1) * width + x].Character = status[x]; cells[(height - 1) * width + x].Attribute = 11; }
                        Rect rectangle = info.Window;
                        if (!WriteConsoleOutputW(output, cells, new Coord(width, height), new Coord(0, 0), ref rectangle))
                            throw new IOException("Cannot draw the ASCII frame. Win32 error: " + Marshal.GetLastWin32Error());
                        paintedFrame = frame; dirty = false;
                    }
                    Thread.Sleep(paused || ended ? 30 : 5);
                }
            }
            finally
            {
                if (opened) mciSendStringW("close rouri_ascii_audio", null, 0, IntPtr.Zero);
                if (hasMode) SetConsoleMode(input, oldMode);
                try
                {
                    Console.TreatControlCAsInput = oldControl;
                    Console.CursorVisible = cursor;
                    Console.Title = title;
                    Console.ForegroundColor = (ConsoleColor)(original.Attributes & 15);
                    Console.BackgroundColor = (ConsoleColor)((original.Attributes >> 4) & 15);
                    Console.Clear();
                }
                catch (IOException) { }
            }
        }
    }
}
