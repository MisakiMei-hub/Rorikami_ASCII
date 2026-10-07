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
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(IntPtr handle, byte[] data, int length, out int written, IntPtr overlapped);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern uint mciSendStringW(string command, StringBuilder result, int length, IntPtr callback);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern uint mciGetErrorStringW(uint error, StringBuilder result, int length);

        sealed class Movie
        {
            public int Width, Height, Fps, DurationMs, OffsetMs, Count, Stride, BytesPerCell;
            public bool Planar;
            public byte[] Data;
            public Movie(string path)
            {
                using (var file = File.OpenRead(path))
                {
                    // Single-member gzip records its uncompressed length in
                    // the trailer. Allocate once, avoiding a second full RGB copy.
                    if (file.Length < 18) throw new InvalidDataException("Truncated ASCII movie.");
                    file.Position = file.Length - 4;
                    var size = new byte[4];
                    if (file.Read(size, 0, 4) != 4) throw new InvalidDataException("Truncated gzip trailer.");
                    uint length = BitConverter.ToUInt32(size, 0);
                    if (length < 28 || length > 1024u * 1024u * 1024u)
                        throw new InvalidDataException("Invalid ASCII movie length.");
                    Data = new byte[(int)length];
                    file.Position = 0;
                    using (var zip = new GZipStream(file, CompressionMode.Decompress))
                    {
                        int offset = 0;
                        while (offset < Data.Length)
                        {
                            int read = zip.Read(Data, offset, Math.Min(1024 * 1024, Data.Length - offset));
                            if (read == 0) throw new InvalidDataException("Truncated ASCII movie.");
                            offset += read;
                        }
                        if (zip.ReadByte() != -1) throw new InvalidDataException("Invalid gzip length.");
                    }
                }
                string format = Encoding.ASCII.GetString(Data, 0, 8);
                if (format != "ASCII01\n" && format != "ASCII02\n" && format != "ASCII03\n")
                    throw new InvalidDataException("Invalid ASCII movie header.");
                Planar = format == "ASCII03\n";
                BytesPerCell = format == "ASCII01\n" ? 2 : 4;
                Width = BitConverter.ToInt32(Data, 8); Height = BitConverter.ToInt32(Data, 12);
                Fps = BitConverter.ToInt32(Data, 16); DurationMs = BitConverter.ToInt32(Data, 20);
                OffsetMs = BitConverter.ToInt32(Data, 24);
                if (Width < 1 || Width > 500 || Height < 1 || Height > 200 || Fps < 1 || Fps > 120 || DurationMs <= 0)
                    throw new InvalidDataException("Invalid movie dimensions or timing.");
                Stride = checked(Width * Height * BytesPerCell);
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
            for (int start = 28; start < movie.Data.Length; start += movie.Stride)
            {
                int end = start + (movie.Planar ? movie.Width * movie.Height : movie.Stride);
                for (int i = start; i < end; i += movie.Planar ? 1 : movie.BytesPerCell)
                    if (movie.Data[i] < 32 || movie.Data[i] > 126 || (movie.BytesPerCell == 2 && movie.Data[i + 1] > 15))
                        throw new InvalidDataException("Invalid ASCII character or color.");
            }
            Console.WriteLine("OK: {0}x{1}, {2} fps, {3} frames, video {4:F2}s, audio {5:F2}s, offset {6}ms",
                movie.Width, movie.Height, movie.Fps, movie.Count, (double)movie.Count / movie.Fps,
                movie.DurationMs / 1000.0, movie.OffsetMs);
            Console.WriteLine("Color format: {0}", movie.BytesPerCell == 4 ? "cell-aligned RGB true color" : "legacy 16-color");
        }

        public static void Snapshot(string path, int frame, string output)
        {
            var movie = new Movie(path);
            if (frame < 0 || frame >= movie.Count) throw new ArgumentOutOfRangeException("frame");
            var text = new StringBuilder();
            int start = 28 + frame * movie.Stride;
            for (int y = 0; y < movie.Height; y++)
            {
                for (int x = 0; x < movie.Width; x++) text.Append((char)movie.Data[GlyphOffset(movie, start, y * movie.Width + x)]);
                text.Append("\r\n");
            }
            File.WriteAllText(output, text.ToString(), Encoding.ASCII);
            Console.WriteLine("Saved ASCII frame {0}: {1}", frame, output);
        }

        static readonly int[] ClassicColors = {
            0x000000, 0x000080, 0x008000, 0x008080, 0x800000, 0x800080, 0x808000, 0xC0C0C0,
            0x808080, 0x0000FF, 0x00FF00, 0x00FFFF, 0xFF0000, 0xFF00FF, 0xFFFF00, 0xFFFFFF
        };
        static byte[] MakeLegacyColors()
        {
            var lut = new byte[32768];
            for (int index = 0; index < lut.Length; index++)
            {
                int r = ((index >> 10) & 31) * 8 + 4, g = ((index >> 5) & 31) * 8 + 4, b = (index & 31) * 8 + 4;
                int nearest = 8, best = Int32.MaxValue;
                for (int color = 1; color < ClassicColors.Length; color++)
                {
                    int dr = r - (ClassicColors[color] >> 16), dg = g - ((ClassicColors[color] >> 8) & 255), db = b - (ClassicColors[color] & 255);
                    int error = 2 * dr * dr + 4 * dg * dg + db * db;
                    if (error < best) { best = error; nearest = color; }
                }
                lut[index] = (byte)nearest;
            }
            return lut;
        }
        static readonly byte[] LegacyColors = MakeLegacyColors();
        static int GlyphOffset(Movie movie, int start, int pixel)
        { return start + pixel * (movie.Planar ? 1 : movie.BytesPerCell); }
        static int CellColor(Movie movie, int offset)
        {
            if (movie.BytesPerCell == 2) return ClassicColors[movie.Data[offset + 1]];
            if (movie.Planar)
            {
                int plane = movie.Width * movie.Height;
                return movie.Data[offset + plane] << 16 | movie.Data[offset + plane * 2] << 8 | movie.Data[offset + plane * 3];
            }
            return movie.Data[offset + 1] << 16 | movie.Data[offset + 2] << 8 | movie.Data[offset + 3];
        }
        static void WriteTerminal(IntPtr output, string text)
        {
            byte[] data = Encoding.ASCII.GetBytes(text);
            int written;
            if (!WriteFile(output, data, data.Length, out written, IntPtr.Zero) || written != data.Length)
                throw new IOException("Cannot write terminal frame. Win32 error: " + Marshal.GetLastWin32Error());
        }
        static string AnsiFrame(Movie movie, int frame, int width, int height, bool color, string status, bool clear)
        {
            var text = new StringBuilder(width * height * (color ? 18 : 1) + 1024);
            // Synchronized updates are ignored by terminals that do not support
            // them. Absolute row positions and disabled wrapping prevent scroll
            // shifts and leave no stale cells when the window is resized.
            text.Append("\x1b[?2026h\x1b[48;2;0;0;0m");
            if (clear) text.Append("\x1b[2J");
            if (!color) text.Append("\x1b[38;2;235;235;235m");
            int start = 28 + frame * movie.Stride, lastColor = -1;
            int rows = height - 1;
            for (int y = 0; y < rows; y++)
            {
                text.Append("\x1b[").Append(y + 1).Append(";1H");
                int sourceY = y * movie.Height / rows;
                for (int x = 0; x < width; x++)
                {
                    int offset = GlyphOffset(movie, start, sourceY * movie.Width + x * movie.Width / width);
                    char character = (char)movie.Data[offset];
                    if (color && character != ' ')
                    {
                        int rgb = CellColor(movie, offset);
                        if (rgb != lastColor)
                        {
                            text.Append("\x1b[38;2;").Append(rgb >> 16).Append(';').Append((rgb >> 8) & 255).Append(';').Append(rgb & 255).Append('m');
                            lastColor = rgb;
                        }
                    }
                    text.Append(character);
                }
            }
            text.Append("\x1b[").Append(height).Append(";1H\x1b[38;2;100;210;220m");
            int length = Math.Min(width, status.Length);
            text.Append(status, 0, length).Append(' ', width - length);
            text.Append("\x1b[0m\x1b[?2026l");
            return text.ToString();
        }
        public static int LastRenderedFrames { get; private set; }
        public static double LastRenderMilliseconds { get; private set; }
        public static string LastRenderer { get; private set; }

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
            uint oldOutputMode;
            bool hasOutputMode = GetConsoleMode(output, out oldOutputMode);
            bool ansi = hasOutputMode && SetConsoleMode(output, oldOutputMode | 4u);
            bool opened = false;
            var clock = new Stopwatch();
            int pausedAt = 0, paintedFrame = -1, paintedWidth = -1, paintedHeight = -1;
            bool paused = false, ended = false, dirty = true;
            int durationMs = movie.DurationMs;
            Cell[] cells = null;
            LastRenderedFrames = 0; LastRenderMilliseconds = 0;
            LastRenderer = ansi ? (color ? "RGB ANSI" : "monochrome ANSI") : "Win32 fallback";
            try
            {
                Console.Title = "Rorikami ASCII | Space: pause / replay | R: replay | Q/Esc: exit";
                Console.CursorVisible = false;
                Console.TreatControlCAsInput = true;
                if (ansi) WriteTerminal(output, "\x1b[?25l\x1b[?7l\x1b[2J\x1b[H");
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
                        if (!ansi) cells = new Cell[width * height];
                        paintedWidth = width; paintedHeight = height; dirty = true;
                    }
                    if (frame != paintedFrame || dirty)
                    {
                        long began = Stopwatch.GetTimestamp();
                        string status = String.Format(" {0} {1:mm\\:ss}/{2:mm\\:ss} | Space: {3} | R: replay | Q/Esc: exit{4}",
                            ended ? "ENDED" : (paused ? "PAUSED" : "PLAY"), TimeSpan.FromMilliseconds(position),
                            TimeSpan.FromSeconds(Math.Ceiling(durationMs / 1000.0)), ended ? "replay" : "pause", mute ? " | MUTE" : "");
                        if (ansi)
                        {
                            WriteTerminal(output, AnsiFrame(movie, frame, width, height, color, status, dirty));
                        }
                        else
                        {
                            for (int i = 0; i < cells.Length; i++) { cells[i].Character = ' '; cells[i].Attribute = 7; }
                            // Fill every available cell; reserve one row for controls.
                            int drawWidth = width, drawHeight = height - 1;
                            int start = 28 + frame * movie.Stride;
                            for (int y = 0; y < drawHeight; y++)
                            {
                                int sourceY = y * movie.Height / drawHeight;
                                for (int x = 0; x < drawWidth; x++)
                                {
                                    int source = GlyphOffset(movie, start, sourceY * movie.Width + x * movie.Width / drawWidth);
                                    int dest = y * width + x;
                                    cells[dest].Character = (char)movie.Data[source];
                                    if (!color) cells[dest].Attribute = 15;
                                    else if (movie.BytesPerCell == 2) cells[dest].Attribute = movie.Data[source + 1];
                                    else
                                    {
                                        int rgb = CellColor(movie, source);
                                        cells[dest].Attribute = LegacyColors[((rgb >> 19) & 31) * 1024 + ((rgb >> 11) & 31) * 32 + ((rgb >> 3) & 31)];
                                    }
                                }
                            }
                            for (int x = 0; x < Math.Min(width - 1, status.Length); x++)
                            { cells[(height - 1) * width + x].Character = status[x]; cells[(height - 1) * width + x].Attribute = 11; }
                            Rect rectangle = info.Window;
                            if (!WriteConsoleOutputW(output, cells, new Coord(width, height), new Coord(0, 0), ref rectangle))
                                throw new IOException("Cannot draw the ASCII frame. Win32 error: " + Marshal.GetLastWin32Error());
                        }
                        LastRenderedFrames++;
                        LastRenderMilliseconds += (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency;
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
                    if (ansi) WriteTerminal(output, "\x1b[?2026l\x1b[?7h\x1b[0m");
                    Console.TreatControlCAsInput = oldControl;
                    Console.CursorVisible = cursor;
                    Console.Title = title;
                    Console.ForegroundColor = (ConsoleColor)(original.Attributes & 15);
                    Console.BackgroundColor = (ConsoleColor)((original.Attributes >> 4) & 15);
                    Console.Clear();
                }
                catch (IOException) { }
                finally { if (hasOutputMode) SetConsoleMode(output, oldOutputMode); }
            }
        }
    }
}
